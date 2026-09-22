using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ImageOptCompat;

/// Two mods dereference not-yet-initialised statics from the pre-load UI. Normally the window is
/// too small to notice; anything that lengthens the pre-DefOf phase makes them throw every frame,
/// which stops the UI drawing at all (observed 2026-09-08: a black screen with a repeating
/// "Root level exception in OnGUI()" NullReferenceException).
///
/// Both are guarded by prefixing THEIR patch and skipping it until the thing it needs exists.
/// Reflection only - ImageOptCompat references neither mod, and both guards fail open.
internal static class EarlyUiGuards
{
    private static FieldInfo? vefKeyBindingField;
    private static FieldInfo? worldbuilderSettingsField;

    // PASS-2 FIX: these guards sit on OnGUI/Update, so they run every frame for the whole session.
    // The thing they check can only ever go null -> non-null, never back. Once satisfied, latch and
    // stop reflecting: a FieldInfo.GetValue per frame forever is pure overhead after early load.
    private static volatile bool vefReady;
    private static volatile bool worldbuilderReady;

    internal static int InstalledCount { get; private set; }

    /// Guard targets whose mod is present. Equal to InstalledCount when every guard went in; zero
    /// when neither mod is active, which is fine and not a failure.
    internal static int FoundCount { get; private set; }
    internal static int VefSkips { get; private set; }
    internal static int WorldbuilderSkips { get; private set; }

    internal static void TryInstall(Harmony harmony)
    {
        if (harmony == null) return;
        InstalledCount = 0;
        FoundCount = 0;

        // --- VEF: Restart.VFE_Dev_Restart is a KeyBindingDef in a [DefOf] class, null pre-init.
        TryPatch(harmony,
            "VEF.Sounds.VanillaExpandedFramework_DebugWindowsOpener_DevToolStarterOnGUI_Patch",
            "Prefix",
            nameof(VefDevToolGuard),
            () => { vefKeyBindingField = FieldOn("VEF.Sounds.Restart", "VFE_Dev_Restart"); return vefKeyBindingField != null; });

        // --- Worldbuilder: WorldbuilderMod.settings is null until settings load.
        TryPatch(harmony,
            "Worldbuilder.Rand_EnsureStateStackEmpty_Patch",
            "Prefix",
            nameof(WorldbuilderRandGuard),
            () => { worldbuilderSettingsField = FieldOn("Worldbuilder.WorldbuilderMod", "settings"); return worldbuilderSettingsField != null; });

        if (InstalledCount > 0)
            Report.Write(ReportKind.Info, $"early-UI guards installed: {InstalledCount}");
    }

    private static FieldInfo? FieldOn(string typeName, string fieldName)
    {
        var t = AccessTools.TypeByName(typeName);
        return t == null ? null : AccessTools.Field(t, fieldName);
    }

    private static void TryPatch(Harmony harmony, string typeName, string methodName,
                                 string guardName, Func<bool> prepare)
    {
        try
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) return;                       // mod absent - nothing to guard
            FoundCount++;

            // The mod IS present but has changed shape: a renamed method or field. This used to
            // return silently, leaving the guard off with no sign of it - the one failure this
            // patch keeps warning about everywhere else. Fail open, but say so.
            var target = AccessTools.Method(type, methodName);
            if (target == null || !prepare())
            {
                Report.Write(ReportKind.Problem, $"{typeName} is active but could not be guarded; that mod may have "
                                               + "been updated. Its loading-screen error can come back until this "
                                               + "patch is updated too.");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(EarlyUiGuards), guardName));
            InstalledCount++;
        }
        catch (Exception e)
        {
            // Fail OPEN: a guard that cannot install must never stop the game loading.
            Report.Write(ReportKind.Problem, $"could not guard {typeName}.{methodName}: {e.Message}");
        }
    }

    /// Their Prefix returns void, so skipping it is safe.
    public static bool VefDevToolGuard()
    {
        if (vefReady) return true;
        try
        {
            if (vefKeyBindingField?.GetValue(null) != null) { vefReady = true; return true; }
            VefSkips++;
            return false;
        }
        catch { vefReady = true; return true; }
    }

    /// Their Prefix returns bool and CONTROLS whether Verse.Rand.EnsureStateStackEmpty runs.
    /// Skipping it must therefore set __result = true, or the game's own method is suppressed.
    public static bool WorldbuilderRandGuard(ref bool __result)
    {
        if (worldbuilderReady) return true;
        try
        {
            if (worldbuilderSettingsField?.GetValue(null) != null) { worldbuilderReady = true; return true; }
            __result = true;
            WorldbuilderSkips++;
            return false;
        }
        catch { worldbuilderReady = true; return true; }
    }
}
