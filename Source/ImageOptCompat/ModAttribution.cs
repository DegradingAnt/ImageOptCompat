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

    /// Lets a test or a reload start from scratch.
    internal static void Reset() => byAssembly = null;
}
