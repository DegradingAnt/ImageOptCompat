using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ImageOptCompat;

/// The in-game counterpart of the offline test suites. Ant: "any of the tests we can build into
/// the mod itself? it should probably do all the prechecks when the game is loading so we can
/// inform the user of whatever is happening and if everything is alright".
///
/// Each check asks one question the offline tests answer before release, now asked of the player's
/// actual install: is each enabled fix really in place, do Harmony frames resolve (the attribution
/// depends on it), is the right Faster Game Loading build active, are the versions the tested ones.
///
/// The rules are a pure function of a Snapshot, so every rule is unit-tested without the game.
/// StartupCheckRunner gathers the live Snapshot while the game loads; MainMenuStatus shows the
/// result in the main-menu corner, the way the Harmony mod shows its own status there.
internal static class StartupCheck
{
    internal enum Outcome { Pass, Untested, Failed, Off }

    internal sealed class Result
    {
        internal Result(string name, Outcome outcome, string detail)
        {
            Name = name;
            Outcome = outcome;
            Detail = detail;
        }

        internal string Name { get; }
        internal Outcome Outcome { get; }
        internal string Detail { get; }

        public override string ToString() => $"{Label(Outcome)}  {Name}: {Detail}";
    }

    /// Everything the rules need, read from the running game in one place. Properties rather than
    /// fields, so a test harness that links these rules without the runner that fills them in does
    /// not see "never assigned" warnings.
    internal sealed class Snapshot
    {
        internal bool ImageOptActive { get; set; }
        internal bool EarlyGuardsOn { get; set; }
        internal bool NullGuardOn { get; set; }
        internal bool AudioGuardOn { get; set; }
        internal bool RepairOn { get; set; }
        internal bool ReportOn { get; set; }
        internal bool ReadbackOn { get; set; }
        internal bool SoundFixOn { get; set; }
        internal bool RepeatedFinderOn { get; set; }
        internal int EarlyGuardsFound { get; set; }
        internal int EarlyGuardsInstalled { get; set; }
        internal int NullGuardTargets { get; set; }
        internal bool AudioGuardInstalled { get; set; }
        internal bool TextureHooksInstalled { get; set; }
        internal bool ImageOptTrackingFound { get; set; }
        internal bool HarmonyFramesResolve { get; set; }
        internal bool SoundFixInstalled { get; set; }
        internal bool RepeatedFinderInstalled { get; set; }
        internal bool? FglSupport { get; set; }
        internal string? UntestedVersions { get; set; }
        internal string? FglUntestedSettings { get; set; }

        /// The readback fix's hooks as Harmony has them now: seven pixel reads plus the cleanup.
        internal int ReadbackHooksLive { get; set; }
        internal int ReadbackHooksExpected { get; set; }
        internal bool VehicleReadbackOn { get; set; }
        internal bool VehicleHookLive { get; set; }

        /// Whether the Faster Game Loading and version checks ran to the end. Their results default
        /// to "nothing wrong", so without this a check that never ran would read as a pass.
        internal bool ImageOptChecksRan { get; set; }

        /// The main-menu status line's hook, as Harmony has it now. Another mod can remove it.
        internal bool StatusLineLive { get; set; }
    }

    /// The results of the last run, for the main-menu line and the settings page.
    internal static IReadOnlyList<Result> Results { get; private set; } = Array.Empty<Result>();

    internal static void Record(IReadOnlyList<Result> results) => Results = results;

    internal static int Count(Outcome outcome) => Results.Count(r => r.Outcome == outcome);

    internal static string Label(Outcome outcome) => outcome switch
    {
        Outcome.Pass => "OK",
        Outcome.Untested => "UNTESTED",
        Outcome.Failed => "FAILED",
        _ => "off",
    };

    /// One line for the log and the main-menu corner.
    internal static string Summary()
    {
        if (Results.Count == 0) return "startup checks have not run";
        var failed = Count(Outcome.Failed);
        var untested = Count(Outcome.Untested);
        var ran = Results.Count - Count(Outcome.Off);
        if (failed > 0) return $"{failed} of {ran} startup checks failed";
        if (untested > 0) return $"all fixes in place, {untested} untested version or setting";
        return $"all {ran} startup checks passed";
    }

    internal static List<Result> Evaluate(Snapshot s)
    {
        var results = new List<Result>
        {
            Fix("Early-load guards", s.EarlyGuardsOn,
                s.EarlyGuardsFound == 0
                    ? Pass("nothing to guard: neither Vanilla Expanded Framework nor Worldbuilder is active")
                    : s.EarlyGuardsInstalled == s.EarlyGuardsFound
                        ? Pass($"{s.EarlyGuardsInstalled} guard(s) in place")
                        : Fail($"{s.EarlyGuardsInstalled} of {s.EarlyGuardsFound} guards in place; a guarded mod may have been updated")),

            // Two terminal draw methods: DrawTexture and DrawTextureWithTexCoords.
            Fix("Null-texture guard", s.NullGuardOn,
                s.NullGuardTargets >= 2 ? Pass($"guarding {s.NullGuardTargets} draw methods")
                : s.NullGuardTargets == 1 ? Fail("only 1 of 2 draw methods guarded; Unity's IMGUI may have changed")
                : Fail("no draw method guarded; the null-texture log flood is not being stopped")),

            Fix("Failed-audio guard", s.AudioGuardOn,
                s.AudioGuardInstalled ? Pass("in place")
                : Fail("not in place; a sound file that fails to decode can crash the game")),

            Fix("Sound file loading repair", s.SoundFixOn,
                s.SoundFixInstalled ? Pass("in place")
                : Fail("not in place; sound files with an extensible WAV header stay silent")),

            Fix("Repeated-error finder", s.RepeatedFinderOn,
                s.RepeatedFinderInstalled ? Pass("in place")
                : Fail("not in place; repeated errors will not be traced to a mod")),

            WithImageOpt("Double-extension repair", s.RepairOn, s.ImageOptActive,
                s.TextureHooksInstalled ? Pass("in place") : Fail("not in place; Image Opt cache paths will not be repaired")),

            Fix("Missing-texture report", s.ReportOn,
                s.TextureHooksInstalled ? Pass("recording") : Fail("not in place; nothing is being recorded")),

            WithImageOpt("Generic pixel readback", s.ReadbackOn, s.ImageOptActive, Readback(s)),

            WithImageOpt("Vehicle readback", s.VehicleReadbackOn, s.ImageOptActive,
                s.VehicleHookLive ? Pass("in place")
                : Fail("not in place; vehicle liveries can render black or with colour masks")),

            // Not a fix, but it is how every other result reaches the player on screen.
            new("Main-menu status line",
                s.StatusLineLive ? Outcome.Pass : Outcome.Failed,
                s.StatusLineLive ? "in place"
                : "another mod removed it, so there is no status line, startup dialog or in-game message; "
                + "problems still go to the log and to this list"),

            new("Mod names in reports",
                s.HarmonyFramesResolve ? Outcome.Pass : Outcome.Failed,
                s.HarmonyFramesResolve ? "Harmony-patched methods resolve to their originals"
                : "Harmony-patched methods could not be resolved; reports may name the wrong mod"),

            TestedVersions(s),
        };

        AddFasterGameLoadingChecks(results, s);
        return results;
    }

    /// Both halves must hold: Image Opt's record of its own textures, which is upstream, and every
    /// one of this mod's hooks, which are ours. The record alone used to pass.
    private static (Outcome, string) Readback(Snapshot s)
    {
        if (!s.ImageOptTrackingFound)
            return Fail("Image Opt's texture record was not found; Image Opt may have changed version");
        if (s.ReadbackHooksExpected == 0)
            return Fail("its hooks could not be listed, so none could be checked");
        if (s.ReadbackHooksLive < s.ReadbackHooksExpected)
            return Fail($"{s.ReadbackHooksLive} of {s.ReadbackHooksExpected} hooks in place; mods reading "
                      + "Image Opt textures can get errors or blank data");
        return Pass($"all {s.ReadbackHooksLive} hooks in place, and Image Opt's texture record found");
    }

    private static Result TestedVersions(Snapshot s)
    {
        const string Name = "Tested versions";
        if (!s.ImageOptActive) return new Result(Name, Outcome.Off, "Image Opt is not active");
        if (!s.ImageOptChecksRan)
            return new Result(Name, Outcome.Failed, "the version and Faster Game Loading checks did not run; the log says why");
        return new Result(Name, s.UntestedVersions == null ? Outcome.Pass : Outcome.Untested,
            s.UntestedVersions ?? "Image Opt, Faster Game Loading and Harmony are the tested versions");
    }

    /// Only when Faster Game Loading is active; its settings only matter on the build that works.
    private static void AddFasterGameLoadingChecks(List<Result> results, Snapshot s)
    {
        if (!s.ImageOptActive || !s.ImageOptChecksRan || s.FglSupport == null) return;

        results.Add(new("Faster Game Loading build",
            s.FglSupport == true ? Outcome.Pass : Outcome.Failed,
            s.FglSupport == true ? "the Preview build, with Image Opt support"
            : "a build without Image Opt support; loading can black-screen. Use the Preview build"));

        if (s.FglSupport != true) return;

        results.Add(new("Faster Game Loading settings",
            s.FglUntestedSettings == null ? Outcome.Pass : Outcome.Untested,
            s.FglUntestedSettings ?? "the tested defaults"));
    }

    private static (Outcome, string) Pass(string detail) => (Outcome.Pass, detail);
    private static (Outcome, string) Fail(string detail) => (Outcome.Failed, detail);

    private static Result Fix(string name, bool on, (Outcome Outcome, string Detail) check) =>
        on ? new Result(name, check.Outcome, check.Detail) : new Result(name, Outcome.Off, "switched off in settings");

    private static Result WithImageOpt(string name, bool on, bool imageOptActive, (Outcome Outcome, string Detail) check) =>
        imageOptActive ? Fix(name, on, check) : new Result(name, Outcome.Off, "Image Opt is not active");

    private static bool probeResolved;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProbeTarget(int value) => value + 1;

    /// Runs inside Harmony's generated replacement of ProbeTarget, exactly where the null-texture
    /// guard's prefix runs inside GUI.DrawTexture's replacement.
    private static void ProbePrefix()
    {
        var trace = new StackTrace(fNeedFileInfo: false);
        for (var i = 0; i < trace.FrameCount; i++)
        {
            var method = ModAttribution.FrameMethod(trace.GetFrame(i));
            if (method?.DeclaringType != typeof(StartupCheck)
             || !string.Equals(method.Name, nameof(ProbeTarget), StringComparison.Ordinal)) continue;
            probeResolved = true;
            return;
        }
    }

    /// The live version of the Mono probe's attribution check. The first live boot showed that a
    /// patched frame reads as MonoMod.Utils.DynamicMethodDefinition unless Harmony resolves it; a
    /// future Harmony that changed that would silently turn every report back into a wrong one.
    /// Patches one private method of this class, calls it, and removes the patch again.
    internal static bool HarmonyFramesResolve(Harmony harmony)
    {
        probeResolved = false;
        var target = AccessTools.Method(typeof(StartupCheck), nameof(ProbeTarget));
        var prefix = AccessTools.Method(typeof(StartupCheck), nameof(ProbePrefix));
        if (target == null || prefix == null) return false;

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            _ = ProbeTarget(1);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { harmony.Unpatch(target, prefix); }
            catch (Exception) { /* Leaving a no-op prefix on a private method costs nothing. */ }
        }

        return probeResolved;
    }
}
