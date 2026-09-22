using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using ImageOptCompat;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// Contracts between About.xml, which the player reads, and the code, which acts on it. No other
/// test reads the mod's own metadata, and each of these has a way to drift silently:
/// - the name: the mod list reads About.xml, while Mod Settings and every log line read ModInfo;
/// - the tested versions: About.xml promises them, VersionCheck enforces them;
/// - the Harmony id: by convention the packageId, and it is how other mods find our patches.
[TestFixture]
public class ModMetadataTests
{
    /// The repository root, found by walking up from the test binaries to About/About.xml.
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "About", "About.xml"))) return dir.FullName;
        }

        throw new DirectoryNotFoundException("About/About.xml was not found above the test directory.");
    }

    private static XElement About() => XDocument.Load(Path.Combine(RepoRoot(), "About", "About.xml")).Root!;

    private static string ProductionSource(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ImageOptCompat", file));

    [Test]
    public void NameMatchesTheModListName() =>
        Assert.That(ModInfo.Name, Is.EqualTo(About().Element("name")!.Value));

    [Test]
    public void LogTagIsTheBracketedName() =>
        Assert.That(ModInfo.Tag, Is.EqualTo("[" + ModInfo.Name + "]"));

    /// Every log line goes through ModInfo.Tag. A string literal carrying the old "[ImageOptCompat]"
    /// tag prints a name that matches nothing in the player's mod list. Comments may still mention
    /// it, since ModInfo explains the history.
    [Test]
    public void NoProductionCodeWritesTheOldTag()
    {
        var offenders = Directory.GetFiles(Path.Combine(RepoRoot(), "Source", "ImageOptCompat"), "*.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, number: i + 1)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && x.line.Contains("[ImageOptCompat]", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.number}")
            .ToList();

        Assert.That(offenders, Is.Empty);
    }

    /// The report level only works if nothing bypasses it. Before it existed, each file wrote to
    /// the log directly and chose its own severity, so no setting could have governed them.
    [Test]
    public void OnlyReportWritesToTheLog()
    {
        var offenders = Directory.GetFiles(Path.Combine(RepoRoot(), "Source", "ImageOptCompat"), "*.cs")
            .Where(file => !string.Equals(Path.GetFileName(file), "Report.cs", StringComparison.Ordinal))
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, number: i + 1)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && (x.line.Contains("Log.Message(", StringComparison.Ordinal)
                      || x.line.Contains("Log.Warning(", StringComparison.Ordinal)
                      || x.line.Contains("Log.Error(", StringComparison.Ordinal)))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.number}")
            .ToList();

        Assert.That(offenders, Is.Empty);
    }

    /// Ant's default: game-breaking problems and problems this mod's settings can fix.
    [Test]
    public void DefaultReportLevelIsImportant() =>
        Assert.That(ProductionSource("ImageOptCompatSettings.cs"),
            Does.Contain("public ReportLevel reportLevel = ReportLevel.Important;")
                .And.Contain("Scribe_Values.Look(ref reportLevel, \"reportLevel\", ReportLevel.Important);"));

    /// The Mod Settings list showed "ImageOptCompat" before the name was a single constant.
    [Test]
    public void SettingsEntryUsesTheName() =>
        Assert.That(ProductionSource("ImageOptCompatMod.cs"), Does.Contain("SettingsCategory() => ModInfo.Name;"));

    [Test]
    public void HarmonyIdIsThePackageId() =>
        Assert.That(ModInfo.HarmonyId, Is.EqualTo(About().Element("packageId")!.Value));

    /// The startup check asks Harmony which patches are live under ModInfo.HarmonyId. A Harmony
    /// instance made with any other id would install patches that check cannot see.
    [Test]
    public void EveryHarmonyInstanceUsesTheId()
    {
        var offenders = Directory.GetFiles(Path.Combine(RepoRoot(), "Source", "ImageOptCompat"), "*.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, number: i + 1)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && x.line.Contains("new Harmony(", StringComparison.Ordinal)
                     && !x.line.Contains("new Harmony(ModInfo.HarmonyId)", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.number}")
            .ToList();

        Assert.That(offenders, Is.Empty);
    }

    /// About.xml tells players which versions were tested, and VersionCheck warns about any other.
    /// If one moves without the other, the page promises what the code no longer checks.
    [TestCase("TestedImageOpt", "Image Opt ")]
    [TestCase("TestedFgl", "(Preview) ")]
    [TestCase("TestedHarmony", "Harmony ")]
    public void AboutPageStatesTheVersionTheCodeChecks(string constant, string label)
    {
        var field = typeof(ModInfo).Assembly.GetType("ImageOptCompat.VersionCheck")!
            .GetField(constant, BindingFlags.NonPublic | BindingFlags.Static)!;
        var version = (string)field.GetRawConstantValue()!;

        // The page writes Harmony's 2.4.2.0 the way players see it in the mod list: 2.4.2.
        var shown = version.EndsWith(".0", StringComparison.Ordinal) && version.Count(c => c == '.') == 3
            ? version[..^2]
            : version;

        Assert.That(About().Element("description")!.Value, Does.Contain(label + shown));
    }
}
