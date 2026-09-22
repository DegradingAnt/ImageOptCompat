using System;
using System.IO;
using System.Linq;
using System.Text;
using ImageOptCompat;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// The extensible-WAV rewrite. The rule is narrow on purpose: only a header whose samples are
/// provably the same as a plain header's is rewritten; everything else is left to the game.
[TestFixture]
public class WavHeaderFixTests
{
    private static readonly byte[] SubtypeTail =
        { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };

    /// A WAV file as audio editors write it: fmt, then a LIST metadata chunk, then data.
    internal static byte[] Wav(ushort tag, ushort channels, uint rate, ushort bits, byte[] data,
                              ushort validBits = 0, ushort subCode = 1, uint fmtSize = 40)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var blockAlign = (ushort)(channels * (bits / 8));
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0u);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(fmtSize);
        w.Write(tag);
        w.Write(channels);
        w.Write(rate);
        w.Write(rate * blockAlign);
        w.Write(blockAlign);
        w.Write(bits);
        if (fmtSize >= 40)
        {
            w.Write((ushort)22);
            w.Write(validBits == 0 ? bits : validBits);
            w.Write(3u);
            w.Write(subCode);
            w.Write(SubtypeTail);
        }

        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write(4u);
        w.Write(Encoding.ASCII.GetBytes("INFO"));
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write((uint)data.Length);
        w.Write(data);
        w.Flush();

        var bytes = stream.ToArray();
        BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4);
        return bytes;
    }

    internal static byte[] Samples(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

    [Test]
    public void ExtensiblePcmBecomesPlainPcmWithTheSameSamples()
    {
        var samples = Samples(4000);
        var wav = Wav(0xFFFE, 2, 96000, 16, samples);
        Assert.That(WavHeaderFix.IsRewritable(wav, wav.Length), Is.True);

        var fixedWav = WavHeaderFix.Rewrite(wav)!;
        Assert.That(fixedWav, Is.Not.Null);
        Assert.That(Encoding.ASCII.GetString(fixedWav, 12, 4), Is.EqualTo("fmt "));
        Assert.That(BitConverter.ToUInt32(fixedWav, 16), Is.EqualTo(16u), "plain fmt chunk");
        Assert.That(BitConverter.ToUInt16(fixedWav, 20), Is.EqualTo((ushort)1), "PCM");
        Assert.That(BitConverter.ToUInt16(fixedWav, 22), Is.EqualTo((ushort)2), "channels");
        Assert.That(BitConverter.ToUInt32(fixedWav, 24), Is.EqualTo(96000u), "sample rate");
        Assert.That(BitConverter.ToUInt32(fixedWav, 28), Is.EqualTo(96000u * 4), "byte rate");
        Assert.That(BitConverter.ToUInt16(fixedWav, 34), Is.EqualTo((ushort)16), "bits");
        Assert.That(Encoding.ASCII.GetString(fixedWav, 36, 4), Is.EqualTo("data"));
        Assert.That(BitConverter.ToUInt32(fixedWav, 40), Is.EqualTo((uint)samples.Length));
        Assert.That(fixedWav.Skip(44).ToArray(), Is.EqualTo(samples), "sample bytes unchanged");
        Assert.That(BitConverter.ToUInt32(fixedWav, 4), Is.EqualTo((uint)(fixedWav.Length - 8)), "RIFF size");
    }

    [Test]
    public void ExtensibleFloatBecomesPlainFloat()
    {
        var fixedWav = WavHeaderFix.Rewrite(Wav(0xFFFE, 1, 48000, 32, Samples(400), subCode: 3))!;
        Assert.That(BitConverter.ToUInt16(fixedWav, 20), Is.EqualTo((ushort)3));
    }

    /// A plain header is what the game already reads. It must never be touched.
    [Test]
    public void PlainPcmIsLeftToTheGame()
    {
        var wav = Wav(1, 2, 44100, 16, Samples(400), fmtSize: 16);
        Assert.That(WavHeaderFix.IsRewritable(wav, wav.Length), Is.False);
        Assert.That(WavHeaderFix.Rewrite(wav), Is.Null);
    }

    /// Each of these carries something a plain header cannot say, so rewriting would change the
    /// sound, or the game's decoder cannot play it anyway.
    [TestCase((ushort)24, (ushort)20, (ushort)1, (ushort)2, Description = "20 valid bits padded to 24")]
    [TestCase((ushort)16, (ushort)0, (ushort)2, (ushort)2, Description = "ADPCM subformat")]
    [TestCase((ushort)16, (ushort)0, (ushort)1, (ushort)6, Description = "5.1 surround")]
    [TestCase((ushort)16, (ushort)0, (ushort)3, (ushort)2, Description = "16-bit float does not exist")]
    public void UnsafeExtensibleHeadersAreLeftAlone(ushort bits, ushort validBits, ushort subCode, ushort channels)
    {
        var wav = Wav(0xFFFE, channels, 48000, bits, Samples(channels * (bits / 8) * 10), validBits, subCode);
        Assert.That(WavHeaderFix.IsRewritable(wav, wav.Length), Is.False);
        Assert.That(WavHeaderFix.Rewrite(wav), Is.Null);
    }

    /// The prefix peeks at the first 4 KB only, so the header must be found without the samples.
    [Test]
    public void AShortPeekIsEnoughToDecide()
    {
        var wav = Wav(0xFFFE, 2, 96000, 16, Samples(100_000));
        Assert.That(WavHeaderFix.IsRewritable(wav, 128), Is.True);
    }

    [Test]
    public void AnOddSampleCountIsPaddedToAnEvenChunk()
    {
        var fixedWav = WavHeaderFix.Rewrite(Wav(0xFFFE, 1, 8000, 8, Samples(301)))!;
        Assert.That(fixedWav.Length % 2, Is.Zero);
        Assert.That(BitConverter.ToUInt32(fixedWav, 40), Is.EqualTo(301u), "declared size stays the real one");
    }

    /// This runs inside the game's sound loader. Nothing it is handed may make it throw.
    [Test]
    public void DamagedInputIsRefusedWithoutThrowing()
    {
        var good = Wav(0xFFFE, 2, 96000, 16, Samples(400));
        var inputs = new[]
        {
            Array.Empty<byte>(),
            Encoding.ASCII.GetBytes("RIFF"),
            good.Take(30).ToArray(),
            Enumerable.Repeat((byte)0xFF, 64).ToArray(),
        };

        foreach (var input in inputs)
        {
            Assert.That(() => WavHeaderFix.IsRewritable(input, input.Length), Throws.Nothing);
            Assert.That(WavHeaderFix.Rewrite(input), Is.Null);
        }
    }

    /// The file that failed in boot 2, from the installed Hamster mod: the samples must come out
    /// byte for byte. Skipped where that mod is not installed.
    [Test]
    public void TheMeasuredHamsterFileKeepsEverySample()
    {
        const string path = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2848286000\1.6\Sounds\Hampter\call\Hamster5.wav";
        if (!File.Exists(path)) Assert.Ignore("The Hamster mod is not installed here.");

        var original = File.ReadAllBytes(path);
        Assert.That(BitConverter.ToUInt16(original, 20), Is.EqualTo((ushort)0xFFFE), "still the extensible header");

        var fixedWav = WavHeaderFix.Rewrite(original)!;
        Assert.That(fixedWav, Is.Not.Null);

        var dataAt = IndexOf(original, "data");
        var size = (int)BitConverter.ToUInt32(original, dataAt + 4);
        Assert.That(fixedWav.Skip(44).Take(size).ToArray(), Is.EqualTo(original.Skip(dataAt + 8).Take(size).ToArray()));
    }

    private static int IndexOf(byte[] haystack, string id)
    {
        var needle = Encoding.ASCII.GetBytes(id);
        for (var i = 12; i + 4 <= haystack.Length; i++)
            if (haystack.Skip(i).Take(4).SequenceEqual(needle)) return i;
        return -1;
    }
}
