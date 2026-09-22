using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ImageOptCompat;

/// Asks Harmony which of this mod's attribute-declared patches are live right now.
///
/// WHY. The startup check used to pass the pixel-readback fix because Image Opt's texture record
/// existed. That proves Image Opt is there, not that this mod's hooks are. A class that failed to
/// install, or a mod that later calls UnpatchAll() without an id and removes everyone's patches,
/// leaves no trace in this mod's own state. Codex's review 4 reproduced the false pass with no hook
/// installed at all.
///
/// HOW. Harmony keeps, for every patched method, each patch and the id of the Harmony instance
/// that owns it. That is the record the Harmony mod prints under each frame of its stack traces.
/// A patch class counts as live when its target carries a patch from that class under this mod's
/// id. The classes are found the way PatchAll finds them, so a new patch class is audited with no
/// list to keep up to date.
internal static class PatchAudit
{
    /// The patch classes nested in a container, by PatchAll's own test for a patch class.
    internal static List<Type> PatchClassesIn(Type container) =>
        container.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(type => type.HasHarmonyAttribute())
                 .ToList();

    internal static int LiveCount(string owner, IEnumerable<Type> patchClasses) =>
        patchClasses.Count(patchClass => IsLive(owner, patchClass));

    internal static bool IsLive(string owner, Type patchClass)
    {
        try
        {
            var target = TargetOf(patchClass);
            var patches = target == null ? null : Harmony.GetPatchInfo(target);
            if (patches == null) return false;

            return patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers)
                .Any(patch => string.Equals(patch.owner, owner, StringComparison.Ordinal)
                           && patch.PatchMethod?.DeclaringType == patchClass);
        }
        catch (Exception)
        {
            // A class whose target cannot be resolved is not live, and saying so is the point.
            return false;
        }
    }

    /// The method a patch class's attributes name, resolved from the same merged attributes
    /// Harmony's PatchClassProcessor reads.
    private static MethodInfo? TargetOf(Type patchClass)
    {
        var info = HarmonyMethodExtensions.GetMergedFromType(patchClass);
        if (info?.declaringType == null || info.methodName == null) return null;

        return info.argumentTypes == null
            ? AccessTools.Method(info.declaringType, info.methodName)
            : AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes);
    }
}
