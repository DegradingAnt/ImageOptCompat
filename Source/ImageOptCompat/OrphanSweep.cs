using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Verse;

namespace ImageOptCompat;

/// Image Opt writes .dds / .dds.zstd beside each source image. When a mod later drops or renames its
/// source images - switching to AssetBundles, for instance - the converted files remain and are
/// still served, so the mod renders stale or missing textures. Confirmed on Image Opt's Steam page;
/// the fix there was delete-and-resubscribe.
///
/// FGL ships a cleanup but its condition is `HasSourceImage(f) && !HasValidCacheMagic(f)`, which by
/// construction can never fire on an orphan (an orphan has no source image). This is the complement.
///
/// SAFETY: .dds.zstd only. Measured on a 1465-mod install: of 1954 .dds files, 5 had no source image
/// and were mod-SHIPPED originals with no way to regenerate. Mod-shipped .dds carries the same 'DDS '
/// magic as generated .dds, so magic bytes cannot discriminate. Never auto-delete a .dds.
public static class OrphanSweep
{
    private static bool hasRun;

    public static int LastDeleted { get; private set; }
    public static int LastScanned { get; private set; }

    /// The RESOLVED texture folders of every running mod - the same source Image Opt's own
    /// compressor enumerates, so LoadFolders.xml redirection is honoured rather than guessed at.
    private static List<string> ResolvedTextureDirs()
    {
        var roots = new List<string>();
        var mods = LoadedModManager.RunningMods;
        if (mods == null) return roots;

        foreach (var mod in mods)
        {
            var folders = mod?.foldersToLoadDescendingOrder;
            if (folders == null) continue;
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                var texDir = Path.Combine(folder, GenFilePaths.TexturesFolder);
                if (Directory.Exists(texDir)) roots.Add(texDir);
            }
        }
        return roots;
    }

    private static void SweepDir(string texDir, ref int deleted, ref int scanned)
    {
        string[] files;
        try { files = Directory.GetFiles(texDir, "*.dds.zstd", SearchOption.AllDirectories); }
        catch (Exception e)
        {
            if (ImageOptCompatMod.Settings.verbose) Log.Warning($"[ImageOptCompat] enumerate failed '{texDir}': {e.Message}");
            return;
        }

        foreach (var f in files)
        {
            scanned++;
            if (!OrphanPaths.IsOrphan(f)) continue;
            try
            {
                File.Delete(f);
                deleted++;
                if (ImageOptCompatMod.Settings.verbose) Log.Message($"[ImageOptCompat] orphan removed: {f}");
            }
            catch (Exception e) { Log.Warning($"[ImageOptCompat] could not delete '{f}': {e.Message}"); }
        }
    }

    public static void Run(bool force = false)
    {
        if (hasRun && !force) return;

        // CODEX P2-4: Run(force:true) is reachable from the "Sweep now" button, which stays enabled
        // while the settings window says nothing here does anything. Without this check that button
        // deletes files from active mods during exactly the Image-Opt-disabled bisect the soft
        // dependency exists to make safe. The guard belongs at the entry point, not at the caller.
        if (!ImageOptCompatMod.ImageOptActive)
        {
            // REVIEW #3: zero the counters. Leaving the previous run's numbers made the settings
            // line "Last sweep: N orphan(s) removed" describe a sweep that never happened - exactly
            // the kind of stale readout that misleads a later diagnosis.
            LastDeleted = 0;
            LastScanned = 0;
            Log.Message("[ImageOptCompat] sweep skipped - Image Opt is not active, so nothing here owns any .dds.zstd.");
            return;
        }

        // CODEX P2-3: RimWorld resolves load folders through LoadFolders.xml, so textures live at
        // paths like `1.6/Mods/FacialAnim/Textures` and `Compatibility/1.6/VanillaPsycastsExpanded/
        // Textures`. Walking root and one level below it misses those entirely. foldersToLoadDescending
        // Order is the RESOLVED set - the same source Image Opt's own compressor enumerates.
        var roots = ResolvedTextureDirs();

        // Do NOT latch on an empty pass. Same failure that disabled the vehicle fix: if this runs
        // before RunningMods is populated, latching here would suppress every later real sweep.
        if (roots.Count == 0)
        {
            LastDeleted = 0;
            LastScanned = 0;
            return;
        }
        hasRun = true;

        var deleted = 0;
        var scanned = 0;

        foreach (var texDir in roots) SweepDir(texDir, ref deleted, ref scanned);

        LastDeleted = deleted;
        LastScanned = scanned;
        if (deleted > 0)
            Log.Message($"[ImageOptCompat] swept {deleted.ToString(CultureInfo.InvariantCulture)} orphaned .dds.zstd of {scanned.ToString(CultureInfo.InvariantCulture)} scanned across {roots.Count.ToString(CultureInfo.InvariantCulture)} texture folder(s).");
        else if (ImageOptCompatMod.Settings.verbose)
            Log.Message($"[ImageOptCompat] no orphans among {scanned.ToString(CultureInfo.InvariantCulture)} .dds.zstd in {roots.Count.ToString(CultureInfo.InvariantCulture)} folder(s).");
    }
}
