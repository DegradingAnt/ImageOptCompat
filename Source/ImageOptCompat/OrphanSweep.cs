using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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
        // Deduplicated, and nested roots dropped. Two mods can resolve to the same folder, and a
        // LoadFolders redirect can put one mod's texture folder INSIDE another's. SweepDir uses
        // SearchOption.AllDirectories, so a parent root already covers every child root: keeping
        // both scanned the same tree twice, over-reported the scanned count, and attempted the
        // same delete twice, which logged a spurious "could not delete" for the second attempt.
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = LoadedModManager.RunningMods;
        if (mods == null) return new List<string>();

        foreach (var mod in mods)
        {
            var folders = mod?.foldersToLoadDescendingOrder;
            if (folders == null) continue;
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                var texDir = Path.Combine(folder, GenFilePaths.TexturesFolder);
                if (Directory.Exists(texDir)) unique.Add(Path.GetFullPath(texDir).TrimEnd(Path.DirectorySeparatorChar));
            }
        }

        return new List<string>(OrphanPaths.Deduplicate(unique, Path.DirectorySeparatorChar));
    }

    /// Sweeps one resolved texture folder. Runs on a worker thread, so it must not call Verse.Log:
    /// RimWorld's log appends to a shared list and feeds the debug window, and neither is thread
    /// safe. Messages are handed back and written by the caller on its own thread instead.
    private static void SweepDir(string texDir, ref int deleted, ref int scanned, ConcurrentQueue<string> messages)
    {
        var verbose = ImageOptCompatMod.Settings.verbose;
        string[] files;

        try { files = Directory.GetFiles(texDir, "*.dds.zstd", SearchOption.AllDirectories); }
        catch (Exception e)
        {
            if (verbose) messages.Enqueue($"[ImageOptCompat] enumerate failed '{texDir}': {e.Message}");
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
                if (verbose) messages.Enqueue($"[ImageOptCompat] orphan removed: {f}");
            }
            catch (Exception e) { messages.Enqueue($"[ImageOptCompat] could not delete '{f}': {e.Message}"); }
        }
    }

    /// Sweeps every root in parallel.
    ///
    /// This is pure file work with no Unity or Verse call in the worker, which is the only reason
    /// it can leave the calling thread. It runs during startup across every active mod's texture
    /// folders, so on a large list the enumeration dominates and parallelising it is a direct
    /// saving on load time.
    ///
    /// Roots are already deduplicated and de-nested by ResolvedTextureDirs, so no two workers can
    /// reach the same file and a parallel File.Delete cannot race another worker's delete.
    private static void SweepAll(List<string> roots, out int deleted, out int scanned)
    {
        var totalDeleted = 0;
        var totalScanned = 0;
        var messages = new ConcurrentQueue<string>();
        var degree = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));

        try
        {
            Parallel.ForEach(roots, new ParallelOptions { MaxDegreeOfParallelism = degree }, root =>
            {
                var d = 0;
                var s = 0;
                SweepDir(root, ref d, ref s, messages);
                Interlocked.Add(ref totalDeleted, d);
                Interlocked.Add(ref totalScanned, s);
            });
        }
        catch (AggregateException e)
        {
            messages.Enqueue($"[ImageOptCompat] the sweep hit {e.InnerExceptions.Count} error(s); "
                           + "some folders may not have been swept.");
        }

        // Back on the calling thread, where Verse.Log is safe again.
        while (messages.TryDequeue(out var line)) Log.Warning(line);

        deleted = totalDeleted;
        scanned = totalScanned;
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

        SweepAll(roots, out deleted, out scanned);

        LastDeleted = deleted;
        LastScanned = scanned;
        if (deleted > 0)
            Log.Message($"[ImageOptCompat] swept {deleted.ToString(CultureInfo.InvariantCulture)} orphaned .dds.zstd of {scanned.ToString(CultureInfo.InvariantCulture)} scanned across {roots.Count.ToString(CultureInfo.InvariantCulture)} texture folder(s).");
        else if (ImageOptCompatMod.Settings.verbose)
            Log.Message($"[ImageOptCompat] no orphans among {scanned.ToString(CultureInfo.InvariantCulture)} .dds.zstd in {roots.Count.ToString(CultureInfo.InvariantCulture)} folder(s).");
    }
}
