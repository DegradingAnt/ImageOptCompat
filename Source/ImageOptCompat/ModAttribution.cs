using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace ImageOptCompat;

/// Turns a type into the name of the mod that shipped it.
///
/// A stack frame gives a type name like "UsefulMarks.MarkOverlay", which is not something a player
/// can act on and not always something an author recognises. What both need is the mod's display
/// name and packageId, so a bug report can be filed against the right Workshop page.
///
/// RimWorld already knows this: every ModContentPack lists the assemblies it loaded. This just
/// inverts that mapping once and caches it.
internal static class ModAttribution
{
    private static Dictionary<Assembly, string>? byAssembly;

    /// Built on first use, not at startup: most sessions never need it, and at startup the mod
    /// list is still being assembled.
    private static Dictionary<Assembly, string> Map()
    {
        if (byAssembly != null) return byAssembly;

        var map = new Dictionary<Assembly, string>();

        try
        {
            foreach (var mod in LoadedModManager.RunningMods)
            {
                var loaded = mod?.assemblies?.loadedAssemblies;
                if (loaded == null) continue;

                var label = string.IsNullOrEmpty(mod!.PackageId)
                    ? mod.Name
                    : $"{mod.Name} ({mod.PackageId})";

                foreach (var asm in loaded)
                {
                    // One assembly can only belong to one mod, but a defensive overwrite guard
                    // keeps a duplicate load from throwing during diagnostics.
                    if (asm != null) map[asm] = label;
                }
            }
        }
        catch (Exception e)
        {
            // Attribution is a convenience. Losing it must not cost the caller its report.
            Log.Warning($"[ImageOptCompat] could not build the mod attribution map: {e.Message}");
        }

        byAssembly = map;
        return byAssembly;
    }

    /// The owning mod's display name and packageId, or a plain statement that it is unknown.
    /// Never throws and never returns null - this runs inside diagnostics.
    internal static string Describe(Type? type)
    {
        if (type == null) return "unknown mod";

        try
        {
            var asm = type.Assembly;
            if (Map().TryGetValue(asm, out var label)) return label;

            // RimWorld's own types, Unity's, and anything loaded outside a ModContentPack.
            return asm.GetName().Name switch
            {
                "Assembly-CSharp" => "RimWorld (core)",
                null or "" => "unknown mod",
                var name => $"{name} (not a loaded mod assembly)",
            };
        }
        catch (Exception)
        {
            return "unknown mod";
        }
    }

    /// Frames that are never the answer when looking for "which mod asked for this".
    /// Verse.ContentFinder is the method being patched, so it is always on the stack.
    private static readonly string[] NeverTheCaller =
    {
        "Verse.ContentFinder", "Verse.ModContentLoader", "Verse.ModContentHolder",
    };

    /// Names the mod behind a lookup that no def triggered.
    ///
    /// RimWorld sets ContentFinderRequester.requester only while it resolves a def's references.
    /// Code that calls ContentFinder directly leaves it null, and the game's own error line then
    /// says nothing about who asked. Measured: 2,625 such lookups in one session, all for SongDef
    /// clip paths being requested as textures, with no way to tell which mod wanted them.
    ///
    /// Walks to the first frame that belongs to a real mod. Falls back to the first non-plumbing
    /// frame, so the answer is "RimWorld (core)" rather than silence when the game itself asked.
    /// Only called on a failed lookup, and only while recording is switched on.
    internal static string DescribeCaller()
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);
            string? firstNonPlumbing = null;

            for (var i = 0; i < trace.FrameCount; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                var type = method?.DeclaringType;
                if (type == null) continue;

                var full = type.FullName ?? string.Empty;
                if (NullTextureGuard.IsPlumbingFrame(full)) continue;
                if (StartsWithAny(full, NeverTheCaller)) continue;

                var owner = Describe(type);
                firstNonPlumbing ??= $"{owner} at {full}.{method!.Name}";

                // A real mod owns this frame, which is the answer we actually want.
                if (!owner.Equals("RimWorld (core)", StringComparison.Ordinal)
                 && !owner.Equals("unknown mod", StringComparison.Ordinal)
                 && !owner.EndsWith("(not a loaded mod assembly)", StringComparison.Ordinal))
                    return $"{owner} at {full}.{method!.Name}";
            }

            return firstNonPlumbing ?? "unknown caller";
        }
        catch (Exception)
        {
            return "unknown caller";
        }
    }

    private static bool StartsWithAny(string value, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// Lets a test or a reload start from scratch.
    internal static void Reset() => byAssembly = null;
}
