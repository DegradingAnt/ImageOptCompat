using System;
using System.IO;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// The stream side of the sound-loading repair.
///
/// RimWorld's loader takes a flag, diposeDataStreamIfNotNeeded, that says who owns the stream it
/// is given. The first version of the repair disposed the original whenever it rewrote a file, so a
/// caller that had kept its stream got it back closed. Codex's review 4 reproduced that on the real
/// loader. These pin both ownership choices and the failure paths.
[TestFixture]
public class SoundLoadingFixTests
{
    private static byte[] Extensible() =>
        WavHeaderFixTests.Wav(0xFFFE, 2, 96000, 16, WavHeaderFixTests.Samples(4000));

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Test]
    public void AStreamHandedOverIsReplacedAndDisposed()
    {
        var bytes = Extensible();
        var original = new MemoryStream(bytes);

        var repaired = SoundLoadingFix.Repair(original, disposeOriginal: true);

        Assert.That(repaired, Is.Not.Null.And.Not.SameAs(original));
        Assert.That(original.CanRead, Is.False,
            "the caller let the loader dispose it, and the loader never sees it now, so the repair does");
        Assert.That(ReadAll(repaired!), Is.EqualTo(WavHeaderFix.Rewrite(bytes)));
    }

    [Test]
    public void AStreamTheCallerKeepsIsLeftOpenAndRewound()
    {
        var bytes = Extensible();
        var original = new MemoryStream();
        original.Write(new byte[7], 0, 7);
        original.Write(bytes, 0, bytes.Length);
        original.Position = 7;

        var repaired = SoundLoadingFix.Repair(original, disposeOriginal: false);

        Assert.That(repaired, Is.Not.Null);
        Assert.That(original.CanRead, Is.True, "diposeDataStreamIfNotNeeded was false: the caller still owns it");
        Assert.That(original.Position, Is.EqualTo(7), "rewound to where the caller left it");

        original.Dispose();
        Assert.That(ReadAll(repaired!), Is.EqualTo(WavHeaderFix.Rewrite(bytes)),
            "the replacement must not depend on a stream its caller may close");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AnOrdinaryFileIsNotTouched(bool disposeOriginal)
    {
        var original = new MemoryStream(WavHeaderFixTests.Wav(1, 2, 44100, 16, WavHeaderFixTests.Samples(400), fmtSize: 16));

        Assert.That(SoundLoadingFix.Repair(original, disposeOriginal), Is.Null);
        Assert.That(original.CanRead, Is.True);
        Assert.That(original.Position, Is.Zero);
    }

    /// The peek succeeds and the full read fails. The game must get the original back, open and
    /// rewound, whoever owns it, so it can try the file exactly as it would have.
    [TestCase(true)]
    [TestCase(false)]
    public void AReadFailureLeavesTheOriginalForTheGame(bool disposeOriginal)
    {
        var original = new FailingStream(Extensible(), failOnRead: 2);

        Assert.That(SoundLoadingFix.Repair(original, disposeOriginal), Is.Null);
        Assert.That(original.Disposed, Is.False);
        Assert.That(original.Position, Is.Zero);
    }

    [Test]
    public void AStreamThatCannotSeekIsNotRead()
    {
        var original = new FailingStream(Extensible(), failOnRead: 0, canSeek: false);

        Assert.That(SoundLoadingFix.Repair(original, disposeOriginal: true), Is.Null);
        Assert.That(original.Reads, Is.Zero);
        Assert.That(original.Disposed, Is.False);
    }

    [Test]
    public void NoStreamIsLeftToTheGame() => Assert.That(SoundLoadingFix.Repair(null, disposeOriginal: true), Is.Null);

    /// A seekable stream that throws on a chosen Read call, and records whether it was disposed.
    private sealed class FailingStream : Stream
    {
        private readonly MemoryStream inner;
        private readonly int failOnRead;

        public FailingStream(byte[] bytes, int failOnRead, bool canSeek = true)
        {
            inner = new MemoryStream(bytes);
            this.failOnRead = failOnRead;
            CanSeek = canSeek;
        }

        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        public override bool CanRead => !Disposed;
        public override bool CanSeek { get; }
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++Reads == failOnRead) throw new IOException("injected read failure");
            return inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
