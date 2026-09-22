using System.Linq;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// The sweep DELETES files, and it now runs its roots in parallel. Both of those make root
/// deduplication load-bearing rather than tidiness:
///
///  - a root nested inside another is already covered by SearchOption.AllDirectories, so keeping
///    both scanned the same tree twice and over-reported the scanned count;
///  - the second delete attempt on an already-deleted file logged a spurious "could not delete";
///  - with the sweep parallelised, two workers on overlapping roots can race for the same file.
///
/// A forward slash is used as the separator in these cases so they read the same on any platform.
[TestFixture]
public class OrphanRootDeduplicationTests
{
    private static string[] Dedup(params string[] roots) =>
        OrphanPaths.Deduplicate(roots, '/').ToArray();

    [Test]
    public void KeepsUnrelatedRoots()
    {
        var kept = Dedup("Mods/A/Textures", "Mods/B/Textures");
        Assert.That(kept, Has.Length.EqualTo(2));
    }

    [Test]
    public void RemovesAnExactDuplicate() =>
        Assert.That(Dedup("Mods/A/Textures", "Mods/A/Textures"), Has.Length.EqualTo(1));

    /// Two mods resolving to the same folder differ only in case on Windows.
    [Test]
    public void RemovesADuplicateThatDiffersOnlyInCase() =>
        Assert.That(Dedup("Mods/A/Textures", "mods/a/TEXTURES"), Has.Length.EqualTo(1));

    [Test]
    public void RemovesATrailingSeparatorDuplicate() =>
        Assert.That(Dedup("Mods/A/Textures", "Mods/A/Textures/"), Has.Length.EqualTo(1));

    /// The LoadFolders case: one mod's texture folder resolves inside another's.
    [Test]
    public void DropsARootNestedInsideAnother()
    {
        var kept = Dedup("Mods/A/Textures", "Mods/A/Textures/Sub");
        Assert.That(kept, Is.EquivalentTo(new[] { "Mods/A/Textures" }));
    }

    /// Order must not matter. The parent has to win even when the child is seen first.
    [Test]
    public void DropsANestedRootRegardlessOfInputOrder()
    {
        var kept = Dedup("Mods/A/Textures/Sub", "Mods/A/Textures");
        Assert.That(kept, Is.EquivalentTo(new[] { "Mods/A/Textures" }));
    }

    [Test]
    public void DropsDeeplyNestedRoots()
    {
        var kept = Dedup("Mods/A/Textures", "Mods/A/Textures/A/B/C", "Mods/A/Textures/D");
        Assert.That(kept, Is.EquivalentTo(new[] { "Mods/A/Textures" }));
    }

    /// The separator is the whole point of the prefix test. A sibling whose name merely STARTS
    /// with another root's name is not nested, and dropping it would silently skip a real folder.
    [Test]
    public void ASiblingWithASharedPrefixIsNotNested()
    {
        var kept = Dedup("Mods/A/Textures", "Mods/A/Textures2");
        Assert.That(kept, Has.Length.EqualTo(2));
    }

    [Test]
    public void IgnoresEmptyAndWhitespaceRoots() =>
        Assert.That(Dedup("Mods/A/Textures", "", "   "), Has.Length.EqualTo(1));

    [Test]
    public void HandlesNoRootsAtAll() =>
        Assert.That(Dedup(), Is.Empty);
}
