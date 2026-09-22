using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ImageOptCompat;

/// Reads a WAVE_FORMAT_EXTENSIBLE file as the plain PCM (or IEEE float) file it really is.
///
/// WHY. Many audio editors write "extensible" WAV headers (format tag 0xFFFE) even for ordinary
/// 16-bit stereo audio. RimWorld's decoder (RuntimeAudioClipLoader.CustomAudioFileReader) accepts
/// only format tags 1 (PCM) and 3 (float); anything else goes to NAudio's
/// WaveFormatConversionStream, which asks Windows' ACM for a converter that does not exist for
/// this header, and throws. Measured in boot 2: all 8 sounds of the Hamster mod fail that way,
/// and play as silence.
///
/// An extensible header whose SubFormat is PCM or IEEE float, whose valid bits equal its container
/// bits, and which has one or two channels, carries exactly the same samples a plain header would.
/// So this rewrites only the header - the sample bytes are copied unchanged - and only in memory.
/// Nothing on disk is touched, and every other file is left for the game to handle as before.
///
/// No Unity or Verse type here, so every rule is unit-tested directly.
internal static class WavHeaderFix
{
    private const ushort Extensible = 0xFFFE;
    private const ushort Pcm = 1;
    private const ushort IeeeFloat = 3;

    /// Bytes 2 to 15 of KSDATAFORMAT_SUBTYPE_PCM and _IEEE_FLOAT, as stored in a file.
    /// Bytes 0 and 1 hold the format code (1 or 3). GUID {0000000X-0000-0010-8000-00AA00389B71}.
    private static readonly byte[] SubtypeTail =
    {
        0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
    };

    /// The fields of a "fmt " chunk that the rewrite needs.
    [StructLayout(LayoutKind.Auto)]
    internal readonly struct Format
    {
        internal Format(ushort code, ushort channels, uint sampleRate, ushort blockAlign, ushort bits)
        {
            Code = code;
            Channels = channels;
            SampleRate = sampleRate;
            BlockAlign = blockAlign;
            Bits = bits;
        }

        internal ushort Code { get; }
        internal ushort Channels { get; }
        internal uint SampleRate { get; }
        internal ushort BlockAlign { get; }
        internal ushort Bits { get; }
    }

    /// True when the first `count` bytes hold an extensible fmt chunk this can safely rewrite.
    /// Used on a small peek, so an ordinary file is never read twice.
    internal static bool IsRewritable(byte[] buffer, int count) =>
        TryFindChunks(buffer, count, out var format, out _, out _) && format.Code != 0;

    /// The rewritten file, or null when this file is not one to rewrite. Never throws.
    internal static byte[]? Rewrite(byte[] wav)
    {
        try
        {
            if (!TryFindChunks(wav, wav.Length, out var format, out var dataOffset, out var dataSize)) return null;
            if (format.Code == 0 || dataOffset < 0) return null;

            using var output = new MemoryStream(44 + dataSize + 1);
            using var writer = new BinaryWriter(output);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)(36 + dataSize + (dataSize & 1)));
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16u);
            writer.Write(format.Code);
            writer.Write(format.Channels);
            writer.Write(format.SampleRate);
            writer.Write(format.SampleRate * format.BlockAlign);
            writer.Write(format.BlockAlign);
            writer.Write(format.Bits);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write((uint)dataSize);
            writer.Write(wav, dataOffset, dataSize);
            if ((dataSize & 1) != 0) writer.Write((byte)0);
            writer.Flush();
            return output.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Walks the RIFF chunks within `count` bytes. `format.Code` is the plain format code to write
    /// (1 or 3) when the fmt chunk is a rewritable extensible one, and 0 otherwise. The data chunk
    /// is optional here: a peek may end before it.
    private static bool TryFindChunks(byte[] b, int count, out Format format, out int dataOffset, out int dataSize)
    {
        format = default;
        dataOffset = -1;
        dataSize = 0;
        count = Math.Min(count, b.Length);
        if (count < 12 || !Is(b, 0, "RIFF") || !Is(b, 8, "WAVE")) return false;

        var found = false;
        var pos = 12;
        while (pos + 8 <= count)
        {
            var size = BitConverter.ToUInt32(b, pos + 4);
            var body = pos + 8;

            if (Is(b, pos, "fmt "))
            {
                if (body + Math.Min(size, 40u) > count) return found;
                format = ReadExtensible(b, body, size);
                found = true;
            }
            else if (Is(b, pos, "data"))
            {
                dataOffset = body;
                // Some files declare more data than they hold. Take what is there.
                dataSize = (int)Math.Min(size, (uint)Math.Max(0, count - body));
                return found;
            }

            var next = (long)body + size + (size & 1);
            if (next > int.MaxValue) return found;
            pos = (int)next;
        }

        return found;
    }

    /// The plain equivalent of an extensible fmt chunk, or Code 0 if it has none or is not
    /// extensible at all.
    private static Format ReadExtensible(byte[] b, int body, uint size)
    {
        if (size < 40 || BitConverter.ToUInt16(b, body) != Extensible) return default;

        var channels = BitConverter.ToUInt16(b, body + 2);
        var rate = BitConverter.ToUInt32(b, body + 4);
        var blockAlign = BitConverter.ToUInt16(b, body + 12);
        var bits = BitConverter.ToUInt16(b, body + 14);
        var extensionSize = BitConverter.ToUInt16(b, body + 16);
        var validBits = BitConverter.ToUInt16(b, body + 18);
        var subCode = BitConverter.ToUInt16(b, body + 24);

        if (extensionSize < 22) return default;
        for (var i = 0; i < SubtypeTail.Length; i++)
            if (b[body + 26 + i] != SubtypeTail[i]) return default;

        // RimWorld's decoder handles mono and stereo only; anything wider fails there anyway.
        if (channels is < 1 or > 2 || rate == 0) return default;
        if (validBits != 0 && validBits != bits) return default;          // padded samples: not equivalent
        if (blockAlign != channels * (bits / 8)) return default;
        if (subCode == Pcm && bits is 8 or 16 or 24 or 32) return new Format(Pcm, channels, rate, blockAlign, bits);
        if (subCode == IeeeFloat && bits == 32) return new Format(IeeeFloat, channels, rate, blockAlign, bits);
        return default;
    }

    private static bool Is(byte[] b, int offset, string id) =>
        offset + 4 <= b.Length
        && b[offset] == id[0] && b[offset + 1] == id[1] && b[offset + 2] == id[2] && b[offset + 3] == id[3];
}
