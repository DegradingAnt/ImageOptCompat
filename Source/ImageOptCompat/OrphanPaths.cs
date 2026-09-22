using System;
using System.Collections.Generic;
using System.IO;

namespace ImageOptCompat;

/// The sweep's DECISION logic, deliberately free of Verse and UnityEngine so it can be unit-tested
/// from a net9.0 test project without launching RimWorld.
public static class OrphanPaths
{
    public const string ZstdSuffix = ".dds.zstd";
    private static readonly string[] SourceExts = { ".png", ".jpg", ".jpeg" };

    /// True only for files this mod is allowed to delete. Plain .dds is excluded on purpose:
    /// mods legitimately ship .dds with no source image, and mod-shipped .dds carries the same
    /// 'DDS ' magic as generated .dds, so magic bytes cannot discriminate.
    public static bool IsSweepable(string path) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(ZstdSuffix, StringComparison.OrdinalIgnoreCase);

    public static string? StemOf(string path) =>
        IsSweepable(path) ? path.Substring(0, path.Length - ZstdSuffix.Length) : null;

    public static IEnumerable<string> CandidateSourcePaths(string path)
    {
        var stem = StemOf(path);
        if (stem == null) yield break;
        foreach (var ext in SourceExts) yield return stem + ext;
    }

    /// exists: injected so the decision can be tested without touching a disk.
    public static bool IsOrphan(string path, Func<string, bool> exists)
    {
        if (exists == null) throw new ArgumentNullException(nameof(exists));
        if (!IsSweepable(path)) return false;
        foreach (var candidate in CandidateSourcePaths(path))
            if (exists(candidate)) return false;
        return true;
    }

    public static bool IsOrphan(string path) => IsOrphan(path, File.Exists);

    /// Removes duplicates and nested folders from a list of sweep roots.
    ///
    /// Two mods can resolve to the same texture folder, and a LoadFolders redirect can place one
    /// mod's folder INSIDE another's. The sweep enumerates with SearchOption.AllDirectories, so a
    /// parent root already covers every root beneath it. Keeping both scanned the same tree twice,
    /// over-reported the scanned count, and attempted the same delete twice, which logged a
    /// spurious "could not delete" for the second attempt. It also matters for the parallel sweep:
    /// with overlapping roots two workers can race to delete one file.
    ///
    /// Verse-free and separate from the folder lookup so it can be tested directly.
    public static IReadOnlyList<string> Deduplicate(IEnumerable<string> roots, char separator)
    {
        if (roots == null) throw new ArgumentNullException(nameof(roots));

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root)) unique.Add(root.TrimEnd(separator));
        }

        // Shortest first, so a parent is always considered before anything nested inside it.
        var ordered = new List<string>(unique);
        ordered.Sort((a, b) => a.Length.CompareTo(b.Length));

        var kept = new List<string>();

        foreach (var candidate in ordered)
        {
            var nested = false;

            foreach (var parent in kept)
            {
                // The separator matters: "...\Textures2" must NOT count as nested in "...\Textures".
                if (candidate.StartsWith(parent + separator, StringComparison.OrdinalIgnoreCase))
                {
                    nested = true;
                    break;
                }
            }

            if (!nested) kept.Add(candidate);
        }

        return kept;
    }
}
