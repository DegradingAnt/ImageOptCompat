using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace ImageOptCompat;

/// Turns a type into the name of the mod that shipped it, and a call stack into the mod that
/// made the call.
///
/// A stack frame gives a type name like "UsefulMarks.MarkOverlay", which is not something a player
/// can act on and not always something an author recognises. What both need is the mod's display
/// name and packageId, so a bug report can be filed against the right Workshop page.
///
/// RimWorld already knows this: every ModContentPack lists the assemblies it loaded. This just
/// inverts that mapping once and caches it.
///
/// Getting this wrong is worse than saying nothing. A report that names a mod sends players to
/// that mod's author, so every rule below errs toward "no single mod" rather than a guess.
internal static class ModAttribution
{
    internal const string CoreLabel = "RimWorld (core)";
    internal const string UnknownLabel = "unknown mod";
    internal const string UnidentifiedSite = "an unidentified caller";
    private const string NotAModSuffix = "(not a loaded mod assembly)";
    private const string SharedSuffix = "mods ship a copy)";

    private static Dictionary<Assembly, string>? byAssembly;

    /// Built on first use, not at startup: most sessions never need it, and at startup the mod
    /// list is still being assembled.
    ///
    /// One assembly can appear under SEVERAL mods. RimWorld loads each mod's DLLs with
    /// Assembly.LoadFrom, and LoadFrom returns the assembly already loaded when another mod ships a
    /// copy with the same identity. Harmony is the common case: in the test install, 104 mod
    /// folders include a 0Harmony.dll of their own. This map used to keep whichever mod it saw
    /// last, which in the first live boot credited Harmony's own frames to WanderJoinsPlus. An
    /// assembly that more than one mod claims is now labelled as shared and credited to none.
    private static Dictionary<Assembly, string> Map()
    {
        if (byAssembly != null) return byAssembly;

        var claims = new Dictionary<Assembly, List<string>>();

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
                    if (asm == null) continue;
                    if (!claims.TryGetValue(asm, out var owners)) claims[asm] = owners = new List<string>();
                    if (!owners.Contains(label)) owners.Add(label);
                }
            }
        }
        catch (Exception e)
        {
            // Attribution is a convenience. Losing it must not cost the caller its report.
            Log.Warning($"[ImageOptCompat] could not build the mod attribution map: {e.Message}");
        }

        var map = new Dictionary<Assembly, string>(claims.Count);
        foreach (var pair in claims)
        {
            map[pair.Key] = pair.Value.Count == 1
                ? pair.Value[0]
                : $"{pair.Key.GetName().Name} (shared library: {pair.Value.Count} {SharedSuffix}";
        }

        byAssembly = map;
        return byAssembly;
    }

    /// The owning mod's display name and packageId, or a plain statement that it is unknown.
    /// Never throws and never returns null - this runs inside diagnostics.
    internal static string Describe(Type? type)
    {
        if (type == null) return UnknownLabel;

        try
        {
            var asm = type.Assembly;
            if (Map().TryGetValue(asm, out var label)) return label;

            // RimWorld's own types, Unity's, and anything loaded outside a ModContentPack.
            return asm.GetName().Name switch
            {
                "Assembly-CSharp" => CoreLabel,
                null or "" => UnknownLabel,
                var name => $"{name} {NotAModSuffix}",
            };
        }
        catch (Exception)
        {
            return UnknownLabel;
        }
    }

    /// True when an owner label names ONE specific mod: not the game, not an assembly outside the
    /// mod list, and not a library several mods ship. Only such a frame answers "which mod".
    internal static bool NamesOneMod(string owner) =>
        !owner.Equals(CoreLabel, StringComparison.Ordinal)
     && !owner.Equals(UnknownLabel, StringComparison.Ordinal)
     && !owner.EndsWith(NotAModSuffix, StringComparison.Ordinal)
     && !owner.EndsWith(SharedSuffix, StringComparison.Ordinal);

    /// Frames from these namespaces are plumbing, never the culprit - walk past them.
    ///
    /// MonoMod catches a generated frame that Harmony cannot map back to its original, such as a
    /// detour a mod made with MonoMod directly. Skipping it moves on to the real caller instead of
    /// naming the library that generated the code.
    private static readonly string[] PlumbingNamespaces =
    {
        "UnityEngine.", "HarmonyLib.", "MonoMod.", "ImageOptCompat.", "System.",
    };

    internal static bool IsPlumbing(string typeFullName) => StartsWithAny(typeFullName, PlumbingNamespaces);

    /// The method a stack frame belongs to, seen through Harmony.
    ///
    /// On RimWorld's Mono a patched method runs as a generated replacement. Its frame reports the
    /// type MonoMod.Utils.DynamicMethodDefinition and a name such as
    /// "UnityEngine.GUI.DrawTexture_Patch1", so a plain GetMethod() walk mistakes every patched
    /// method for Harmony's own code. The null-texture guard did exactly that in the first live
    /// boot and named Harmony's copy of GUI.DrawTexture as the caller of every null draw. Harmony
    /// keeps the replacement-to-original map and exposes it here; the Harmony mod's own stack
    /// traces use the same call.
    internal static MethodBase? FrameMethod(StackFrame? frame)
    {
        if (frame == null) return null;

        try
        {
            // Harmony returns null for some frames it cannot place, constructors included. Those
            // keep their own method; a leftover generated frame is then skipped as MonoMod plumbing.
            return OriginalOf(frame) ?? frame.GetMethod();
        }
        catch (Exception)
        {
            return frame.GetMethod();
        }
    }

    /// Separate and never inlined, so that where 0Harmony cannot load (the unit-test host) the
    /// failure is raised inside FrameMethod's try instead of while compiling FrameMethod itself.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MethodBase? OriginalOf(StackFrame frame) => Harmony.GetOriginalMethodFromStackframe(frame);

    /// The nearest frame that belongs to one specific mod, else the nearest frame that is not
    /// plumbing (usually the game's own code), else nothing identifiable.
    ///
    /// Nearest MOD rather than nearest frame: a mod that hands a missing texture to a vanilla
    /// helper such as Widgets.DrawTextureFitted has the helper as its nearest frame, and naming
    /// RimWorld for that would send the report to the wrong place. With no mod anywhere on the
    /// stack, the game's own frame is the honest answer and is returned with its core label.
    internal static (string Site, string Owner) FindCaller(StackTrace trace, string[] alsoSkip)
    {
        string? fallbackSite = null;
        var fallbackOwner = UnknownLabel;

        for (var i = 0; i < trace.FrameCount; i++)
        {
            var method = FrameMethod(trace.GetFrame(i));
            var type = method?.DeclaringType;
            if (type == null) continue;

            var full = type.FullName ?? string.Empty;
            if (IsPlumbing(full) || StartsWithAny(full, alsoSkip)) continue;

            var owner = Describe(type);
            var site = $"{full}.{method!.Name}";
            if (NamesOneMod(owner)) return (site, owner);

            if (fallbackSite == null)
            {
                fallbackSite = site;
                fallbackOwner = owner;
            }
        }

        return (fallbackSite ?? UnidentifiedSite, fallbackOwner);
    }

    /// Frames that are never the answer when looking for "which mod asked for this". The report
    /// observes Log.Error, and ContentFinder is the lookup that failed, so both always sit between
    /// this code and the mod that asked.
    private static readonly string[] NeverTheCaller =
    {
        "Verse.ContentFinder", "Verse.ModContentLoader", "Verse.ModContentHolder", "Verse.Log",
    };

    /// Names the mod behind a lookup that no def triggered.
    ///
    /// RimWorld sets ContentFinderRequester.requester only while it resolves a def's references.
    /// Code that calls ContentFinder directly leaves it null, and the game's own error line then
    /// says nothing about who asked, so the stack is the only witness.
    ///
    /// A cautionary measurement: one pre-review session logged 2,625 of these ownerless texture
    /// errors, and most of the paths were song and sound names. It looked like some mod asking for
    /// audio as textures. It was this patch's own former ContentFinder<T> hooks, which on Mono ran
    /// audio lookups through the texture code; the Mono probe's --legacy mode reproduces it, and
    /// the first boot without those hooks logged none. Read the frame, not the symptom.
    ///
    /// Only called on a failed lookup, and only while recording is switched on.
    internal static string DescribeCaller()
    {
        try
        {
            var (site, owner) = FindCaller(new StackTrace(fNeedFileInfo: false), NeverTheCaller);
            return string.Equals(site, UnidentifiedSite, StringComparison.Ordinal)
                ? "unknown caller"
                : $"{owner} at {site}";
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
