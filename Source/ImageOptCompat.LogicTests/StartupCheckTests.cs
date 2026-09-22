using NUnit.Framework;
using static ImageOptCompat.StartupCheck;

namespace ImageOptCompat.LogicTests;

/// The startup check's rules, one situation at a time. The live snapshot is gathered in game by
/// StartupCheckRunner; the Harmony self-test runs for real in ImageOptCompat.MonoTests.
[TestFixture, NonParallelizable]
public sealed class StartupCheckTests
{
    /// Everything enabled, installed and as tested.
    private static Snapshot Healthy() => new()
    {
        ImageOptActive = true,
        EarlyGuardsOn = true, NullGuardOn = true, AudioGuardOn = true, RepairOn = true, ReadbackOn = true,
        SoundFixOn = true, SoundFixInstalled = true, RepeatedFinderOn = true, RepeatedFinderInstalled = true,
        EarlyGuardsFound = 2, EarlyGuardsInstalled = 2, NullGuardTargets = 2,
        AudioGuardInstalled = true, TextureHooksInstalled = true, ImageOptTrackingFound = true,
        HarmonyFramesResolve = true, FglSupport = true,
    };

    private static Outcome OutcomeOf(Snapshot s, string name) =>
        Evaluate(s).Single(r => r.Name == name).Outcome;

    private static string SummaryOf(Snapshot s)
    {
        Record(Evaluate(s));
        return Summary();
    }

    [Test]
    public void AHealthyInstallPassesEveryCheckThatRuns()
    {
        var results = Evaluate(Healthy());
        Assert.That(results.Where(r => r.Outcome != Outcome.Off), Has.All.Matches<Result>(r => r.Outcome == Outcome.Pass));
        // The missing-texture report is off by default, so it is the one check that does not run.
        Assert.That(results.Single(r => r.Outcome == Outcome.Off).Name, Is.EqualTo("Missing-texture report"));
        Assert.That(SummaryOf(Healthy()), Is.EqualTo($"all {results.Count - 1} startup checks passed"));
    }

    [Test]
    public void AnEnabledFixThatDidNotInstallFails()
    {
        var s = Healthy();
        s.NullGuardTargets = 0;
        Assert.That(OutcomeOf(s, "Null-texture guard"), Is.EqualTo(Outcome.Failed));
        Assert.That(SummaryOf(s), Does.StartWith("1 of "));
    }

    /// A player bisecting with a fix switched off must not see it reported as broken.
    [Test]
    public void ASwitchedOffFixIsOffNotFailed()
    {
        var s = Healthy();
        s.NullGuardOn = false;
        s.NullGuardTargets = 0;
        Assert.That(OutcomeOf(s, "Null-texture guard"), Is.EqualTo(Outcome.Off));
        Assert.That(SummaryOf(s), Does.Contain("passed"));
    }

    [Test]
    public void ImageOptFixesAreOffWithoutImageOpt()
    {
        var s = Healthy();
        s.ImageOptActive = false;
        s.TextureHooksInstalled = false;
        s.ImageOptTrackingFound = false;
        Assert.That(OutcomeOf(s, "Double-extension repair"), Is.EqualTo(Outcome.Off));
        Assert.That(OutcomeOf(s, "Generic pixel readback"), Is.EqualTo(Outcome.Off));
    }

    /// Nothing to guard is fine; a guarded mod that changed shape is not.
    [TestCase(0, 0, true)]
    [TestCase(2, 2, true)]
    [TestCase(2, 1, false)]
    public void EarlyGuardsCompareInstalledWithFound(int found, int installed, bool passes)
    {
        var s = Healthy();
        s.EarlyGuardsFound = found;
        s.EarlyGuardsInstalled = installed;
        Assert.That(OutcomeOf(s, "Early-load guards"), Is.EqualTo(passes ? Outcome.Pass : Outcome.Failed));
    }

    [Test]
    public void TheWrongFasterGameLoadingBuildFails()
    {
        var s = Healthy();
        s.FglSupport = false;
        Assert.That(OutcomeOf(s, "Faster Game Loading build"), Is.EqualTo(Outcome.Failed));
        Assert.That(Evaluate(s).Any(r => r.Name == "Faster Game Loading settings"), Is.False);
    }

    [Test]
    public void WithoutFasterGameLoadingThereAreNoFasterGameLoadingChecks()
    {
        var s = Healthy();
        s.FglSupport = null;
        Assert.That(Evaluate(s).Any(r => r.Name.StartsWith("Faster Game Loading", StringComparison.Ordinal)), Is.False);
    }

    /// Untested is a warning, never a failure: nothing has been shown to break.
    [Test]
    public void UntestedVersionsAndSettingsAreUntestedNotFailed()
    {
        var s = Healthy();
        s.UntestedVersions = "Image Opt 0.1.14 (tested 0.1.13)";
        s.FglUntestedSettings = "delayGraphicLoading";
        Assert.That(OutcomeOf(s, "Tested versions"), Is.EqualTo(Outcome.Untested));
        Assert.That(OutcomeOf(s, "Faster Game Loading settings"), Is.EqualTo(Outcome.Untested));
        Assert.That(SummaryOf(s), Does.StartWith("all fixes in place, 2 untested"));
    }

    [Test]
    public void UnresolvedHarmonyFramesFailTheAttributionCheck()
    {
        var s = Healthy();
        s.HarmonyFramesResolve = false;
        Assert.That(OutcomeOf(s, "Mod names in reports"), Is.EqualTo(Outcome.Failed));
    }
}
