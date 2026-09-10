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
}
