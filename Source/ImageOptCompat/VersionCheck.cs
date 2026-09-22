using System;
using System.Collections.Generic;

namespace ImageOptCompat;

/// The pure version-check decision - which tested mods are missing or have drifted - kept Verse-free
/// (no Log call in it) so it can be unit-tested without the game or Assembly-CSharp. Extracted from
/// ImageCompatMod so the tested set and the decision live in ONE place and are covered by tests.
internal static class VersionCheck
{
    private const string TestedImageOpt = "0.1.13";
    private const string TestedFgl = "2026.09.07.1";
    private const string TestedHarmony = "2.4.2.0";

    /// One note per missing/drifted mod, in a fixed order. Empty means all matched.
    internal static List<string> BuildUntestedNotes(IDictionary<string, string?> installed)
    {
        var notes = new List<string>();
        CompareImpl("dev.soeur.imageopt", "Image Opt", TestedImageOpt, installed, notes);
        CompareImpl("Taranchuk.FasterGameLoading", "Faster Game Loading", TestedFgl, installed, notes);
        CompareImpl("brrainz.harmony", "Harmony", TestedHarmony, installed, notes);
        return notes;
    }

    private static void CompareImpl(string packageId, string label, string tested,
                                    IDictionary<string, string?> installed, List<string> notes)
    {
        var version = installed.TryGetValue(packageId, out var v) ? v : null;
        // A missing/unreadable version is left unreported - the mod list already warns about a
        // missing dependency, and Harmony is a modDependencies entry, so a null here is expected
        // rather than an untested-version drift.
        if (string.IsNullOrEmpty(version) || string.Equals(version, tested, StringComparison.Ordinal)) return;
        notes.Add($"{label} {version} (tested {tested})");
    }
}
