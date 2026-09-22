using System;
using System.Collections.Generic;
using ImageOptCompat;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// The version-check decision, unit-tested without ModLister or the game.
[TestFixture]
public class VersionCheckTests
{
    private static IDictionary<string, string?> Installed(params (string Id, string? Version)[] mods)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in mods) dict[id] = version;
        return dict;
    }

    // All three at their tested versions -> nothing untested.
    [Test]
    public void AllMatched_ReturnsNull()
    {
        var notes = VersionCheck.BuildUntestedNotes(Installed(
            ("dev.soeur.imageopt", "0.1.13"),
            ("Taranchuk.FasterGameLoading", "2026.09.07.1"),
            ("brrainz.harmony", "2.4.2.0")));
        Assert.That(notes, Is.Empty);
    }

    // THE NEW CHECK: Harmony drift is now reported alongside the other two.
    [Test]
    public void HarmonyDrift_IsReported()
    {
        var notes = VersionCheck.BuildUntestedNotes(Installed(
            ("dev.soeur.imageopt", "0.1.13"),
            ("Taranchuk.FasterGameLoading", "2026.09.07.1"),
            ("brrainz.harmony", "2.5.0.0")));
        Assert.That(notes, Contains.Item("Harmony 2.5.0.0 (tested 2.4.2.0)"));
    }

    [Test]
    public void ImageOptDrift_IsReported()
    {
        var notes = VersionCheck.BuildUntestedNotes(Installed(
            ("dev.soeur.imageopt", "0.1.14"),
            ("Taranchuk.FasterGameLoading", "2026.09.07.1"),
            ("brrainz.harmony", "2.4.2.0")));
        Assert.That(notes, Contains.Item("Image Opt 0.1.14 (tested 0.1.13)"));
    }

    [Test]
    public void FglDrift_IsReported()
    {
        var notes = VersionCheck.BuildUntestedNotes(Installed(
            ("dev.soeur.imageopt", "0.1.13"),
            ("Taranchuk.FasterGameLoading", "2026.09.08.0"),
            ("brrainz.harmony", "2.4.2.0")));
        Assert.That(notes, Contains.Item("Faster Game Loading 2026.09.08.0 (tested 2026.09.07.1)"));
    }

    // A missing/unreadable version is not an untested drift - the mod list handles that separately.
    [Test]
    public void MissingVersion_IsNotReported()
    {
        var notes = VersionCheck.BuildUntestedNotes(Installed(
            ("dev.soeur.imageopt", "0.1.13"),
            ("Taranchuk.FasterGameLoading", null),
            ("brrainz.harmony", null)));
        Assert.That(notes, Is.Empty);
    }

    [Test]
    public void PackageIdCaseIsIgnored()
    {
        var notes = VersionCheck.BuildUntestedNotes(Installed(
            ("DEV.SOEUR.IMAGEOPT", "0.1.13"),
            ("taranchuk.fastergameloading", "2026.09.07.1"),
            ("BRRAINZ.HARMONY", "2.4.2.0")));
        Assert.That(notes, Is.Empty);
    }

    [Test]
    public void MultipleDrifts_AreJoinedWithSemicolon()
    {
        var notes = VersionCheck.BuildUntestedNotes(Installed(
            ("dev.soeur.imageopt", "0.2.0"),
            ("Taranchuk.FasterGameLoading", "2026.09.08.0"),
            ("brrainz.harmony", "2.4.2.0")));
        Assert.That(notes, Contains.Item("Image Opt 0.2.0 (tested 0.1.13)"));
        Assert.That(notes, Contains.Item("Faster Game Loading 2026.09.08.0 (tested 2026.09.07.1)"));
    }
}
