using System;
using System.Collections.Generic;
using ImageOptCompat;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

[TestFixture]
public class OrphanPathsTests
{
    private static Func<string, bool> Existing(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return p => set.Contains(p);
    }

    // THE SAFETY RULE. Measured on a 1465-mod install: 5 .dds files had no source image and were
    // mod-SHIPPED originals with no way to regenerate. If this test ever goes green->red, the sweep
    // has become destructive.
    [TestCase(@"C:\m\Textures\Skunk_east.dds")]
    [TestCase(@"C:\m\Textures\Aspiration_Writing.dds")]
    [TestCase(@"C:\m\Textures\a.DDS")]
    public void PlainDds_IsNeverSweepable(string path)
    {
        Assert.That(OrphanPaths.IsSweepable(path), Is.False);
        Assert.That(OrphanPaths.IsOrphan(path, Existing()), Is.False,
            "a .dds with no source must NOT be reported as an orphan - it is a mod-shipped original");
    }

    [Test]
    public void Zstd_WithNoSource_IsOrphan()
    {
        const string p = @"C:\m\Textures\gone.dds.zstd";
        Assert.That(OrphanPaths.IsOrphan(p, Existing()), Is.True);
    }

    [TestCase(".png")]
    [TestCase(".jpg")]
    [TestCase(".jpeg")]
    public void Zstd_WithAnySource_IsNotOrphan(string ext)
    {
        const string p = @"C:\m\Textures\keep.dds.zstd";
        Assert.That(OrphanPaths.IsOrphan(p, Existing(@"C:\m\Textures\keep" + ext)), Is.False);
    }

    [Test]
    public void Zstd_MatchIsCaseInsensitive()
    {
        Assert.That(OrphanPaths.IsSweepable(@"C:\m\Textures\A.DDS.ZSTD"), Is.True);
    }

    [Test]
    public void StemStripsOnlyTheFullSuffix()
    {
        Assert.That(OrphanPaths.StemOf(@"C:\m\Textures\x.dds.zstd"), Is.EqualTo(@"C:\m\Textures\x"));
        Assert.That(OrphanPaths.StemOf(@"C:\m\Textures\x.dds"), Is.Null);
    }

    [Test]
    public void NullAndEmptyAreNotSweepable()
    {
        Assert.That(OrphanPaths.IsSweepable(null!), Is.False);
        Assert.That(OrphanPaths.IsSweepable(""), Is.False);
    }

    [Test]
    public void NullExistsProbeThrows()
    {
        Assert.Throws<ArgumentNullException>(() => OrphanPaths.IsOrphan(@"C:\m\Textures\x.dds.zstd", null!));
    }
}
