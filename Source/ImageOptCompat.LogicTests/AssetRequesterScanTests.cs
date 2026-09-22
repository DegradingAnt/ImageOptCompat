using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Verse;

namespace ImageOptCompat.LogicTests;

/// AssetRequesterScan had NO tests. It also holds the subtlest rule in the mod, and getting that
/// rule wrong produces a FALSE CLEAN RESULT rather than an error, which is the worst failure shape
/// a diagnostic can have.
///
/// The rule: a .NET assembly stores type and member names as UTF-8 in the #Strings heap, but
/// STRING LITERALS as UTF-16 in the #US heap. A texture path a mod passes to ContentFinder is a
/// literal. Searching a DLL as UTF-8 therefore finds nothing and reads as "this mod is innocent".
/// That mistake already cost a wrong answer once in this project.
///
/// These write real bytes to a temp directory and point a stand-in mod at it, so the encoding
/// behaviour is exercised rather than described.
[TestFixture]
public class AssetRequesterScanTests
{
    private string root = null!;

    [SetUp]
    public void MakeFakeMod()
    {
        root = Path.Combine(Path.GetTempPath(), "ImageOptCompatScanTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);

        LoadedModManager.RunningMods.Clear();
        LoadedModManager.RunningMods.Add(new ModContentPack
        {
            RootDir = root,
            Name = "Fake Mod",
            PackageId = "fake.mod",
        });
    }

    [TearDown]
    public void Cleanup()
    {
        LoadedModManager.RunningMods.Clear();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private void WriteXml(string name, string content) =>
        File.WriteAllText(Path.Combine(root, name), content);

    /// Writes a file with a .dll extension whose payload is the needle encoded the way a real
    /// assembly stores a string literal: UTF-16.
    private void WriteFakeAssemblyWithLiteral(string name, string literal)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("MZ fake assembly header padding "));
        bytes.AddRange(Encoding.Unicode.GetBytes(literal));
        bytes.AddRange(Encoding.ASCII.GetBytes(" trailing"));
        File.WriteAllBytes(Path.Combine(root, name), bytes.ToArray());
    }

    private static List<AssetRequesterScan.Owner> Find(string path)
    {
        var all = AssetRequesterScan.FindAll(new[] { path });
        return all.TryGetValue(path, out var owners) ? owners : new List<AssetRequesterScan.Owner>();
    }

    // ---- the encoding rule, which is the whole point -----------------------------------------

    /// THE regression guard. The needle exists in the assembly only as UTF-16. A UTF-8-only search
    /// would return zero owners and the scan would silently exonerate the guilty mod.
    [Test]
    public void FindsALiteralStoredAsUtf16InAnAssembly()
    {
        WriteFakeAssemblyWithLiteral("Mod.dll", "Songs/Relax/Noodle_Planetfall_a");

        var owners = Find("Songs/Relax/Noodle_Planetfall_a");

        Assert.That(owners, Is.Not.Empty,
            "the literal is present as UTF-16. A UTF-8-only search misses it and reports the mod innocent.");
        Assert.That(owners[0].InCode, Is.True);
        Assert.That(owners[0].Mod, Does.Contain("fake.mod"));
    }

    /// The same string in an XML def is plain text, and must also be found.
    [Test]
    public void FindsAPathInAnXmlDef()
    {
        WriteXml("Defs.xml", "<Defs><ThingDef><texPath>Things/Item/Widget</texPath></ThingDef></Defs>");

        var owners = Find("Things/Item/Widget");

        Assert.That(owners, Is.Not.Empty);
        Assert.That(owners[0].InCode, Is.False);
    }

    /// Modders vary casing in XML, so the XML side matches loosely.
    [Test]
    public void XmlMatchingIgnoresCase()
    {
        WriteXml("Defs.xml", "<Defs><texPath>THINGS/ITEM/WIDGET</texPath></Defs>");
        Assert.That(Find("Things/Item/Widget"), Is.Not.Empty);
    }

    // ---- it must not invent owners -----------------------------------------------------------

    [Test]
    public void ReportsNoOwnerWhenNothingContainsTheString()
    {
        WriteXml("Defs.xml", "<Defs><texPath>Something/Else</texPath></Defs>");
        WriteFakeAssemblyWithLiteral("Mod.dll", "Unrelated/Literal");

        Assert.That(Find("Songs/Relax/Noodle_Planetfall_a"), Is.Empty,
            "a scan that names an innocent mod is worse than one that names nobody.");
    }

    /// A path built at runtime by joining a folder and a file name appears in NO file. The scan
    /// must return empty so the report can say so, rather than guessing.
    [Test]
    public void ReportsNoOwnerForARuntimeAssembledPath()
    {
        WriteFakeAssemblyWithLiteral("Mod.dll", "Songs/Relax/");
        Assert.That(Find("Songs/Relax/Noodle_Planetfall_a"), Is.Empty);
    }

    // ---- robustness --------------------------------------------------------------------------

    [Test]
    public void IgnoresFileTypesItDoesNotUnderstand()
    {
        File.WriteAllText(Path.Combine(root, "readme.txt"), "Songs/Relax/Noodle_Planetfall_a");
        File.WriteAllText(Path.Combine(root, "data.json"), "Songs/Relax/Noodle_Planetfall_a");

        Assert.That(Find("Songs/Relax/Noodle_Planetfall_a"), Is.Empty,
            "only XML and assemblies are scanned; a hit in a readme would be noise.");
    }

    [Test]
    public void SurvivesAMissingModDirectory()
    {
        LoadedModManager.RunningMods.Clear();
        LoadedModManager.RunningMods.Add(new ModContentPack
        {
            RootDir = Path.Combine(root, "does_not_exist"),
            Name = "Gone",
            PackageId = "gone.mod",
        });

        Assert.DoesNotThrow(() => Find("anything"));
    }

    [Test]
    public void HandlesAnEmptyRequestList() =>
        Assert.That(AssetRequesterScan.FindAll(new string[0]), Is.Empty);

    /// One pass must answer many paths. Searching per path would re-read every mod file per path,
    /// which on a real list is hours of disk with the UI frozen.
    [Test]
    public void AnswersSeveralPathsFromOneScan()
    {
        WriteFakeAssemblyWithLiteral("Mod.dll", "First/Path");
        WriteXml("Defs.xml", "<Defs><texPath>Second/Path</texPath></Defs>");

        var all = AssetRequesterScan.FindAll(new[] { "First/Path", "Second/Path", "Absent/Path" });

        Assert.That(all["First/Path"], Is.Not.Empty);
        Assert.That(all["Second/Path"], Is.Not.Empty);
        Assert.That(all["Absent/Path"], Is.Empty);
    }

    [Test]
    public void OwnerCapIsDeterministicAndCountsModsInsteadOfFiles()
    {
        LoadedModManager.RunningMods.Clear();
        for (var owner = 7; owner >= 0; owner--)
        {
            var dir = Path.Combine(root, "owner-" + owner);
            Directory.CreateDirectory(dir);
            LoadedModManager.RunningMods.Add(new ModContentPack
            {
                RootDir = dir, Name = "Owner " + owner, PackageId = "owner." + owner,
            });
            for (var file = 0; file < 7; file++)
                File.WriteAllText(Path.Combine(dir, file + ".xml"), "Shared/Asset");
        }

        var owners = Find("Shared/Asset");
        Assert.That(owners.Select(owner => owner.Mod), Is.EqualTo(Enumerable.Range(0, 6)
            .Select(owner => $"Owner {owner} (owner.{owner})")));
    }
}
