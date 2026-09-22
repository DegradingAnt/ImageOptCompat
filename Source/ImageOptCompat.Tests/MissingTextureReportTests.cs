using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// Covers the path-correction rule behind the double-extension fix - the actual root cause of the
/// measured 222,128-line null-texture flood.
///
/// Image Opt caches as "name.dds.zstd". A mod that builds content paths by scanning its own
/// texture folder calls Path.GetFileNameWithoutExtension, which strips only the LAST extension and
/// hands back "name.dds". The game is then asked for a file that does not exist.
///
/// The risk in correcting that is over-reach: a null from ContentFinder is frequently CORRECT,
/// because ContentFinder.Get(path, reportFailure: false) is how a mod tests whether an optional
/// texture exists. Most of these cases therefore assert that the rule does NOT fire.
[TestFixture]
public class MissingTextureReportTests
{
    private static string Correct(string? path)
    {
        Assert.That(MissingTextureReport.TryCorrectPath(path, out var corrected), Is.True,
            $"expected '{path}' to be corrected");
        return corrected;
    }

    private static void Refuses(string? path)
    {
        Assert.That(MissingTextureReport.TryCorrectPath(path, out var corrected), Is.False,
            $"expected '{path}' to be left alone");
        Assert.That(corrected, Is.Empty, "a refused path must not hand back a correction");
    }

    // ---- the measured case ------------------------------------------------------------------

    /// The exact path from the Player.log that started this: Holograms And Projectors asked for
    /// this and got nothing, 60 times, producing 60 materials with a null texture.
    [Test]
    public void CorrectsTheMeasuredHologramPath() =>
        Assert.That(Correct("Buildings/Art/SmallHologram/Hologram_small_01.dds"),
            Is.EqualTo("Buildings/Art/SmallHologram/Hologram_small_01"));

    [Test]
    public void CorrectsTheMaskVariantToo() =>
        Assert.That(Correct("Buildings/Art/Masks/SmallHologram/Hologram_small_10.dds"),
            Is.EqualTo("Buildings/Art/Masks/SmallHologram/Hologram_small_10"));

    [TestCase(".DDS")]
    [TestCase(".Dds")]
    public void ExtensionMatchingIsCaseInsensitive(string suffix) =>
        Assert.That(Correct("Things/Item/Widget" + suffix), Is.EqualTo("Things/Item/Widget"));

    // ---- what it must NOT touch -------------------------------------------------------------

    /// The important one. Ordinary content paths carry no extension at all, and a null for one of
    /// these can be a deliberate "does this optional texture exist?" test. Correcting it would
    /// turn every such test into a false positive.
    [TestCase("Things/Item/Apparel/Parka/Parka")]
    [TestCase("UI/Commands/ChangeColor")]
    [TestCase("Buildings/Art/SmallHologram/Hologram_small_01")]
    [TestCase("World/WorldObjects/Expanding/Settlement")]
    public void LeavesOrdinaryContentPathsAlone(string path) => Refuses(path);

    /// Other extensions are none of this rule's business. (Widget.dds.zstd is now corrected - see
    /// CorrectsAFullCachePath below - so only a single .zstd, .png or .jpg is refused here.)
    [TestCase("Things/Item/Widget.png")]
    [TestCase("Things/Item/Widget.zstd")]
    [TestCase("Things/Item/Widget.jpg")]
    public void LeavesOtherExtensionsAlone(string path) => Refuses(path);

    /// A mod that used the cache file's whole name (not stripped one extension) builds
    /// "name.dds.zstd". The ".dds" rule alone misses it; the full-artefact rule corrects it in one
    /// pass, and the result is itself correctable no further.
    [Test]
    public void CorrectsAFullCachePath() =>
        Assert.That(Correct("Things/Item/Widget.dds.zstd"), Is.EqualTo("Things/Item/Widget"));

    [Test]
    public void CorrectingTheFullPathLeavesNoFurtherArtefact() =>
        Assert.That(MissingTextureReport.TryCorrectPath("Things/Item/Widget", out _), Is.False);

    /// Must be a SUFFIX match, never a substring one.
    [TestCase("Things/Item/Widget.ddsx")]
    [TestCase("Things/.dds/Widget")]
    [TestCase("Things/Item/ddsWidget")]
    public void OnlyMatchesAtTheEnd(string path) => Refuses(path);

    /// Nothing would be left to look up.
    [TestCase(".dds")]
    [TestCase("")]
    [TestCase(null)]
    public void RefusesWhenThereIsNothingLeft(string? path) => Refuses(path);

    // ---- the recursion-safety property ------------------------------------------------------

    /// The retry calls the same patched ContentFinder method. It terminates only because the
    /// corrected path can never itself end in the artefact extension - so correcting twice is
    /// impossible by construction. If that ever stops holding, the patch becomes infinite.
    [TestCase("Buildings/Art/SmallHologram/Hologram_small_01.dds")]
    [TestCase("Things/Item/Widget.DDS")]
    public void ACorrectedPathIsNeverItselfCorrectable(string path)
    {
        var once = Correct(path);
        Assert.That(MissingTextureReport.TryCorrectPath(once, out _), Is.False,
            "a corrected path must not be correctable again, or the retry would recurse");
    }

    /// The double-extension case a mod could produce by stripping nothing at all. One pass is all
    /// the rule promises, and the result is still not correctable a third time.
    [Test]
    public void StripsExactlyOneArtefactExtension()
    {
        var once = Correct("Things/Item/Widget.dds.dds");
        Assert.That(once, Is.EqualTo("Things/Item/Widget.dds"));

        var twice = Correct(once);
        Assert.That(twice, Is.EqualTo("Things/Item/Widget"));
        Assert.That(MissingTextureReport.TryCorrectPath(twice, out _), Is.False);
    }
}
