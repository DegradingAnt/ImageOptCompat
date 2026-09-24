using System;
using HarmonyLib;
using Verse;

namespace ImageOptCompat;

/// Runs the startup check while the game is still loading.
///
/// [StaticConstructorOnStartup] is where the Harmony mod runs its own startup check: late in
/// loading, after every Mod constructor (so every fix has tried to install), still inside the
/// loading screen. The loading text is set the vanilla way, LongEventHandler.SetCurrentEventText,
/// which the vanilla screen shows and which Loading Progress (installed in the pack) picks up
/// through its own hook on that method. No dependency on either.
[StaticConstructorOnStartup]
internal static class StartupCheckRunner
{
    static StartupCheckRunner()
    {
        try
        {
            LongEventHandler.SetCurrentEventText(ModInfo.Name + ": checking that its fixes are in place");
            Run();
        }
        catch (Exception e)
        {
            Report.Write(ReportKind.Notice, $"the startup check could not run: {e.Message}");
        }
    }

    private static void Run()
    {
        var settings = ImageOptCompatMod.Settings;
        var harmony = new Harmony(ModInfo.HarmonyId);

        // Asked of Harmony itself, not of this mod's bookkeeping: see PatchAudit.
        var readbackHooks = PatchAudit.PatchClassesIn(typeof(Texture2DReadPatches));

        var snapshot = new StartupCheck.Snapshot
        {
            ImageOptActive = ImageOptCompatMod.ImageOptActive,
            EarlyGuardsOn = settings.earlyUiGuards,
            NullGuardOn = settings.nullTextureGuard,
            AudioGuardOn = settings.guardFailedAudioClips,
            RepairOn = settings.fixDoubleExtensionPaths,
            ReportOn = settings.reportMissingTextures,
            ReadbackOn = settings.genericPixelReadback,
            SoundFixOn = settings.fixSoundLoading,
            RepeatedFinderOn = settings.findRepeatedErrors,
            EarlyGuardsFound = EarlyUiGuards.FoundCount,
            EarlyGuardsInstalled = EarlyUiGuards.InstalledCount,
            NullGuardTargets = NullTextureGuard.InstalledCount,
            AudioGuardInstalled = FailedAudioClipGuard.Installed,
            SoundFixInstalled = SoundLoadingFix.Installed,
            RepeatedFinderInstalled = RepeatedErrorFinder.Installed,
            TextureHooksInstalled = MissingTextureReport.Installed,
            // Looked up only when the fix is on: a failed lookup raises an on-screen problem, and a fix
            // the player switched off has nothing to report. The row then reads "switched off".
            ImageOptTrackingFound = ImageOptCompatMod.ImageOptActive && settings.genericPixelReadback
                                 && Texture2DReadPatches.ImageOptTrackingFound,
            ReadbackHooksExpected = readbackHooks.Count,
            ReadbackHooksLive = PatchAudit.LiveCount(harmony.Id, readbackHooks),
            VehicleReadbackOn = settings.vehicleReadback,
            VehicleHookLive = PatchAudit.IsLive(harmony.Id, typeof(ModContentPack_AnyContentLoaded_Patch)),
            HarmonyFramesResolve = StartupCheck.HarmonyFramesResolve(harmony),
            FglSupport = ImageOptCompatMod.FglHasImageOptSupport,
            UntestedVersions = ImageOptCompatMod.UntestedVersions,
            FglUntestedSettings = ImageOptCompatMod.FglUntestedSettings,
            ImageOptChecksRan = ImageOptCompatMod.ImageOptChecksRan,
            StatusLineLive = PatchAudit.IsLive(harmony.Id, MainMenuStatus.Target, MainMenuStatus.PatchMethod),
        };

        var results = StartupCheck.Evaluate(snapshot);
        StartupCheck.Record(results);
        ReportWhatNothingElseReports(snapshot);

        Report.Write(ReportKind.Info, "startup check: " + StartupCheck.Summary() + Environment.NewLine
                                    + string.Join(Environment.NewLine, results));
    }

    /// Each fix already reported its own install failure, in its own words, when it tried. Two
    /// things nothing else reports: a hook another mod removed after it was installed, and the
    /// Harmony self-test. With its hook gone, the status line cannot report its own absence, so the
    /// log has to.
    private static void ReportWhatNothingElseReports(StartupCheck.Snapshot snapshot)
    {
        if (!snapshot.StatusLineLive)
        {
            Report.Write(ReportKind.Problem, "another mod removed this patch's main-menu status line (a postfix on "
                                           + "MainMenuDrawer.MainMenuOnGUI), so problems are not shown on screen. They "
                                           + "are still written to the log and listed on this patch's settings page.");
        }

        if (!snapshot.HarmonyFramesResolve)
        {
            Report.Write(ReportKind.Problem, "Harmony-patched methods could not be resolved to their originals, "
                                           + "so the mod named in null-texture and missing-texture reports may be "
                                           + "wrong. Harmony may have been updated.");
        }
    }
}
