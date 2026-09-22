using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

/// Records every texture the game asked for and could not find, with the def that asked and the
/// mod that shipped it.
///
/// This is the other half of NullTextureGuard, and the more useful half for an author. The guard
/// stops the cost at draw time but can only name a CALLING METHOD. The real fault is earlier: a
/// def points at a texture path that does not resolve, ContentFinder returns null, MaterialPool
/// builds a Material with a null texture, and that material is then drawn every frame forever.
/// Measured on a 1,484-mod load: 60 missing paths produced 60 "MatFrom with null sourceTex" and
/// 222,128 draw warnings.
///
/// This postfix does two things, and the split between them is the important part.
///
/// 1. It REPAIRS one specific, provable failure: a path ending ".dds", which only ever arises from
///    a mod stripping a single extension off Image Opt's "name.dds.zstd" cache file. See
///    TryRescueDoubleExtension for why that case is safe to correct.
///
/// 2. For every OTHER failed lookup it OBSERVES ONLY, and never substitutes anything. That
///    restriction is deliberate rather than timid: ContentFinder.Get(path, reportFailure: false)
///    is the supported way for a mod to TEST whether a texture exists. Substituting there would
///    turn every such test into a false positive and break optional-content checks across a whole
///    modpack. A null here is frequently correct. Only a null that reaches a DRAW is unambiguously
///    a fault, which is why blanket substitution happens in NullTextureGuard and nowhere else.
internal static class MissingTextureReport
{
    internal sealed class Entry
    {
        internal string Path = string.Empty;
        internal string Def = "(no def - requested directly by code)";
        internal string Mod = "unknown mod";
        internal int Count;
    }

    private static readonly Dictionary<string, Entry> Missing = new(StringComparer.Ordinal);

    /// Bounded: a broken mod can ask for the same missing path thousands of times, but the number
    /// of DISTINCT paths is what matters and it must not grow without limit.
    private const int MaxPaths = 256;

    internal static int DistinctPaths => Missing.Count;

    /// The recorded paths, for AssetRequesterScan to trace back to an owning mod. Copied rather
    /// than exposed live, because the scan takes seconds and the dictionary keeps being written.
    internal static List<string> RecordedPaths() => new(Missing.Keys);
    internal static bool Installed { get; private set; }

    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            // The closed generic is the patch target; ContentFinder<T> is generic, and only the
            // Texture2D instantiation is of interest here.
            var target = AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get),
                new[] { typeof(string), typeof(bool) });

            if (target == null)
            {
                Log.Warning("[ImageOptCompat] ContentFinder<Texture2D>.Get(string, bool) was not found. "
                          + "Missing-texture reporting is off; the game's API may have changed.");
                return;
            }

            harmony.Patch(target,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(MissingTextureReport), nameof(Postfix))));
            Installed = true;
        }
        catch (Exception e)
        {
            Log.Warning($"[ImageOptCompat] missing-texture reporting could not be installed: {e.Message}");
        }
    }

    /// How many lookups the double-extension fallback rescued this session.
    internal static int Rescued;

    /// Paths already rescued, so the explanation is logged once per path rather than per lookup.
    private static readonly HashSet<string> RescuedPaths = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static bool retrying;

    /// Parameter names match ContentFinder<T>.Get exactly - Harmony binds them BY NAME, and a
    /// mismatch here would bind nothing while still reporting the patch as installed.
    private static void Postfix(string itemPath, bool reportFailure, ref Texture2D? __result)
    {
        // Unity's == overload: also true for a destroyed texture, which is equally unusable.
        if (__result != null) return;
        if (string.IsNullOrEmpty(itemPath)) return;
        if (!UnityData.IsInMainThread || retrying) return;

        if (TryRescueDoubleExtension(itemPath, ref __result)) return;
        // The repair installs this same postfix by default. Recording must have its own gate,
        // and optional existence probes are not missing-content errors.
        if (!ImageOptCompatMod.Settings.reportMissingTextures || !reportFailure) return;

        try
        {
            if (!UnityData.IsInMainThread) return;   // Dictionary is not thread-safe

            if (Missing.TryGetValue(itemPath, out var entry))
            {
                entry.Count++;
                return;
            }

            if (Missing.Count >= MaxPaths) return;

            var def = ContentFinderRequester.requester;
            Missing[itemPath] = new Entry
            {
                Path = itemPath,
                Count = 1,
                Def = def?.defName ?? "(no def - requested directly by code)",

                // With no def there is nothing to attribute to, so read the stack instead.
                // Without this, every code-driven lookup reported "unknown mod", which is the
                // majority of them: 2,625 in the measured session.
                Mod = def == null
                    ? ModAttribution.DescribeCaller()
                    : def.modContentPack == null
                        ? ModAttribution.Describe(def.GetType())
                        : $"{def.modContentPack.Name} ({def.modContentPack.PackageId})",
            };
        }
        catch (Exception)
        {
            // Diagnostics must never break a texture lookup.
        }
    }

    /// Image Opt writes its cache beside the source image as "name.png.dds.zstd" -> in practice
    /// "name.dds.zstd". A mod that builds its content paths by SCANNING its own texture folder and
    /// calling Path.GetFileNameWithoutExtension gets "name.dds" back, because that method strips
    /// only the LAST extension. It then asks ContentFinder for "name.dds", which is not a content
    /// path and resolves to nothing.
    ///
    /// Measured: Holograms And Projectors (Vesper.HologramsAndProjectors) does exactly this and
    /// produced 60 missing paths, 60 materials with a null texture, and 222,128 draw warnings in
    /// one session. Any mod that scans its texture directory breaks the same way, so the fix is
    /// generic rather than a list of package ids.
    ///
    /// SAFETY, and it is the reason this is narrow rather than clever:
    ///  - it runs ONLY after the lookup already returned null, so no successful result is changed;
    ///  - it fires ONLY on a path ending in an Image Opt artefact extension. A real RimWorld
    ///    content path never carries a file extension, so a null here cannot be a deliberate
    ///    "does this optional texture exist?" test that we would be falsifying;
    ///  - a retry guard prevents re-entry, even for names ending in multiple .dds suffixes.
    /// The Image Opt cache extension, as it appears AFTER a mod has stripped ".zstd" off
    /// "name.dds.zstd", OR kept the whole "name.dds.zstd" name. Both are corrected on purpose; the
    /// rule is narrow in the other direction (only these artefact suffixes, never a content path).
    /// The Image Opt cache artefact left after a single `Path.GetFileNameWithoutExtension` strips
    /// ".zstd": "name.dds.zstd" -> "name.dds". A mod that scans its own folder and strips one
    /// extension hands the game "name.dds", which is not a content path and resolves to nothing.
    private const string Artefact = ".dds";

    /// The full cache artefact, when a mod used the cache file's whole name instead of stripping one
    /// extension: "name.dds.zstd". Handled first (longer match) so a path built from the full cache
    /// file is corrected too, without changing the single-strip ".dds" case above.
    private const string ArtefactFull = ".dds.zstd";

    /// The decision, split out with no Verse or Unity dependency so it can be tested directly -
    /// the same shape as OrphanPaths and VehicleReadback.NeedsCpuReadback.
    ///
    /// Correct ONLY when the path ends in an Image Opt artefact extension. A genuine RimWorld content
    /// path normally omits the file extension. A filename stem can itself contain dots; the caller
    /// retries only once, so a missing name.dds.dds cannot incorrectly resolve all the way to name.
    internal static bool TryCorrectPath(string? itemPath, out string corrected)
    {
        corrected = string.Empty;

        if (string.IsNullOrEmpty(itemPath)) return false;

        // Match the longer artefact first so "name.dds.zstd" is corrected to "name" in one pass and
        // cannot be left ending in ".zstd" (which the ".dds" rule below would not touch).
        var artefact = ArtefactFull;
        if (!itemPath!.EndsWith(artefact, StringComparison.OrdinalIgnoreCase))
        {
            artefact = Artefact;
            if (!itemPath.EndsWith(artefact, StringComparison.OrdinalIgnoreCase)) return false;
        }

        var candidate = itemPath.Substring(0, itemPath.Length - artefact.Length);

        // A bare artefact (".dds", ".dds.zstd") leaves nothing to look up.
        if (candidate.Length == 0) return false;

        corrected = candidate;
        return true;
    }

    private static bool TryRescueDoubleExtension(string itemPath, ref Texture2D? result)
    {
        if (!ImageOptCompatMod.ImageOptActive || !ImageOptCompatMod.Settings.fixDoubleExtensionPaths) return false;
        if (!TryCorrectPath(itemPath, out var corrected)) return false;

        try
        {
            retrying = true;
            // reportFailure: false - if the corrected path is also missing, the original lookup
            // has already logged it and a second error line would only double the noise.
            var found = ContentFinder<Texture2D>.Get(corrected, reportFailure: false);

            // Unity's == overload, so this also rejects a destroyed texture. The compiler cannot
            // see through that operator and still treats `found` as possibly null afterwards,
            // hence the suppression - the check above is stricter than a reference test, not weaker.
            if (found == null) return false;

            result = found!;
            Rescued++;

            // Explain the mechanism ONCE. The measured session repaired 60 paths, and repeating
            // a 300-character explanation per path put 18 KB of our own noise into a log we are
            // trying to make readable. The full list is in the diagnostic report instead.
            if (RescuedPaths.Count < MaxPaths && RescuedPaths.Add(itemPath))
            {
                if (RescuedPaths.Count == 1)
                    Log.Message($"[ImageOptCompat] repaired a texture path: '{itemPath}' does not exist, but "
                              + $"'{corrected}' does. A mod built this path by scanning its texture folder and "
                              + "stripping one extension, which turns Image Opt's 'name.dds.zstd' cache file into "
                              + "'name.dds'. Left alone, the texture is null and Unity logs a warning on every "
                              + "frame it is drawn. Further repairs this session are counted, not logged: see "
                              + "the mod settings page for the total and the full list.");
                else if (ImageOptCompatMod.Settings.verbose)
                    Log.Message($"[ImageOptCompat] repaired texture path '{itemPath}' -> '{corrected}'.");
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            retrying = false;
        }
    }

    /// A report an author can paste straight into a bug report: which mod, which def, which file.
    internal static string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Missing textures - files a def asked for that do not resolve");
        sb.AppendLine();

        if (!Installed || !ImageOptCompatMod.Settings.reportMissingTextures)
        {
            sb.AppendLine("Missing-texture recording is off or its patch is not installed.");
            sb.AppendLine("An empty report does not mean that nothing is missing.");
            return sb.ToString();
        }

        if (Missing.Count == 0)
        {
            sb.AppendLine("No missing textures were seen this session.");
            return sb.ToString();
        }

        // Group by mod: an author only cares about their own rows, and one broken mod usually
        // accounts for a whole block of paths.
        var byMod = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var entry in Missing.Values)
        {
            if (!byMod.TryGetValue(entry.Mod, out var list)) byMod[entry.Mod] = list = new List<Entry>();
            list.Add(entry);
        }

        foreach (var pair in byMod)
        {
            sb.AppendLine($"{pair.Key} - {pair.Value.Count} missing file(s)");
            pair.Value.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

            foreach (var e in pair.Value)
                sb.AppendLine($"    {e.Path}   (def: {e.Def}, asked {e.Count}x)");

            sb.AppendLine();
        }

        if (Missing.Count >= MaxPaths)
            sb.AppendLine($"(list capped at {MaxPaths} distinct paths)");

        return sb.ToString();
    }
}
