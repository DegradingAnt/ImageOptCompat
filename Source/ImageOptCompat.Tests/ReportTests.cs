using ImageOptCompat;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// Who sees what, at each report level. Ant asked for a setting that decides how much the mod
/// reports, defaulting to game-breaking problems and problems its own settings can fix.
[TestFixture]
public class ReportTests
{
    /// The whole routing table, spelled out. Any change to who sees what must change this test.
    [TestCase(ReportLevel.Quiet, ReportKind.Breaking, true, false)]
    [TestCase(ReportLevel.Quiet, ReportKind.Problem, false, false)]
    [TestCase(ReportLevel.Quiet, ReportKind.Notice, false, false)]
    [TestCase(ReportLevel.Quiet, ReportKind.Hint, false, false)]
    [TestCase(ReportLevel.Quiet, ReportKind.Info, false, false)]
    [TestCase(ReportLevel.Important, ReportKind.Breaking, true, true)]
    [TestCase(ReportLevel.Important, ReportKind.Problem, true, true)]
    [TestCase(ReportLevel.Important, ReportKind.Notice, true, false)]
    [TestCase(ReportLevel.Important, ReportKind.Hint, true, false)]
    [TestCase(ReportLevel.Important, ReportKind.Info, false, false)]
    [TestCase(ReportLevel.Everything, ReportKind.Breaking, true, true)]
    [TestCase(ReportLevel.Everything, ReportKind.Problem, true, true)]
    [TestCase(ReportLevel.Everything, ReportKind.Notice, true, false)]
    [TestCase(ReportLevel.Everything, ReportKind.Hint, true, false)]
    [TestCase(ReportLevel.Everything, ReportKind.Info, true, false)]
    public void RoutingTable(ReportLevel level, ReportKind kind, bool logged, bool onScreen)
    {
        Assert.That(Report.Logs(level, kind), Is.EqualTo(logged), "logged");
        Assert.That(Report.OnScreen(level, kind), Is.EqualTo(onScreen), "on screen");
    }

    /// 0.2.0's "verbose" switch carries over as Everything, but only while the player has not
    /// chosen a level since. The old key is never written again, so a later choice sticks.
    [TestCase(ReportLevel.Important, true, ReportLevel.Everything)]
    [TestCase(ReportLevel.Important, false, ReportLevel.Important)]
    [TestCase(ReportLevel.Quiet, true, ReportLevel.Quiet)]
    [TestCase(ReportLevel.Everything, false, ReportLevel.Everything)]
    public void VerboseMigration(ReportLevel saved, bool legacyVerbose, ReportLevel expected) =>
        Assert.That(Report.Migrate(saved, legacyVerbose), Is.EqualTo(expected));
}
