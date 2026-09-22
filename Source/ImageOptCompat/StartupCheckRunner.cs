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
        var snapshot = new StartupCheck.Snapshot
        {
            ImageOptActive = ImageOptCompatMod.ImageOptActive,
            EarlyGuardsOn = settings.earlyUiGuards,
            NullGuardOn = settings.nullTextureGuard,
            AudioGuardOn = settings.guardFailedAudioClips,
            RepairOn = settings.fixDoubleExtensionPaths,
            ReportOn = settings.reportMissingTextures,
            ReadbackOn = settings.genericPixelReadback,
            EarlyGuardsFound = EarlyUiGuards.FoundCount,
            EarlyGuardsInstalled = EarlyUiGuards.InstalledCount,
            NullGuardTargets = NullTextureGuard.InstalledCount,
            AudioGuardInstalled = FailedAudioClipGuard.Installed,
            TextureHooksInstalled = MissingTextureReport.Installed,
            ImageOptTrackingFound = ImageOptCompatMod.ImageOptActive && Texture2DReadPatches.ImageOptTrackingFound,
            HarmonyFramesResolve = StartupCheck.HarmonyFramesResolve(new Harmony("degradingant.imageoptcompat")),
            FglSupport = ImageOptCompatMod.FglHasImageOptSupport,
            UntestedVersions = ImageOptCompatMod.UntestedVersions,
            FglUntestedSettings = ImageOptCompatMod.FglUntestedSettings,
        };

        var results = StartupCheck.Evaluate(snapshot);
        StartupCheck.Record(results);

        // Each fix already reported its own install failure, in its own words, when it tried. The
        // one check nothing else covers is the Harmony self-test, so only it reports here.
        if (!snapshot.HarmonyFramesResolve)
        {
            Report.Write(ReportKind.Problem, "Harmony-patched methods could not be resolved to their originals, "
                                           + "so the mod named in null-texture and missing-texture reports may be "
                                           + "wrong. Harmony may have been updated.");
        }

        Report.Write(ReportKind.Info, "startup check: " + StartupCheck.Summary() + Environment.NewLine
                                    + string.Join(Environment.NewLine, results));
    }
}
