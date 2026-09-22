using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

/// Unity's GUI.DrawTexture writes "null texture passed to GUI.DrawTexture" every time it is handed
/// a null or destroyed texture. Unity emits that warning itself, so RimWorld's own log-dedup never
/// sees it and it is written once per call, forever. Measured on a 1,484-mod load: 222,128 of them
/// in a single session - 94% of a 236,731-line Player.log, spread evenly across the whole run.
///
/// RimWorld ticks and draws on the SAME thread. At 1x there is one tick per frame and slack to
/// absorb the cost; at 3x there are three, and a fixed per-frame tax comes straight out of tick
/// throughput. That is why the symptom is "fine at 1x, stutters and loses TPS above it", and why
/// nothing measuring per-pawn tick cost ever sees it: the work is not in the tick at all.
///
/// The guard substitutes BaseContent.BadTex so Unity is never handed null. That is graceful
/// degradation, not log suppression - the failure becomes VISIBLE as the vanilla magenta
/// bad-texture square instead of an invisible nothing, and the first few distinct call sites are
/// each reported ONCE with a stack trace so the mod at fault can be named and fixed at source.
///
/// Scope: this is NOT established to be an Image Opt bug. Image Opt's own load paths do recover
/// (TextureLoadPatch.PrefixV1 and _LoadTextureSync both fall back to VanillaLoadTexture), though
/// BatchedTextureCopier silently skips failed tasks with no recovery arm at all. The flood is a
/// known RimWorld-wide problem reported against several unrelated UI mods, and no existing mod
/// fixes it. It is cheap to stop here and expensive to leave running, so the guard installs
/// unconditionally - like EarlyUiGuards, and for the same reason.
internal static class NullTextureGuard
{
    /// Null draws intercepted during Repaint. Other events retain Unity's normal handling.
    internal static int Substituted;

    /// Patch targets found and hooked. Zero means the guard is not doing anything at all.
    internal static int InstalledCount;

    /// Distinct call sites already named, so each offender is reported once rather than per frame.
    private static readonly HashSet<string> ReportedSites = new(StringComparer.Ordinal);

    /// Call site -> how many null draws came from it, for the mod-author report. Only populated in
    /// deep-diagnostic mode; in normal mode the stack is walked at most MaxReportedSites times and
    /// the counts would be meaningless.
    private static readonly Dictionary<string, int> Tally = new(StringComparer.Ordinal);

    /// Bounded so a pathological session cannot grow the dictionary without limit.
    private const int MaxTalliedSites = 64;

    /// Reporting captures a stack trace, far too expensive to do per frame. Once this many distinct
    /// sites are named, the null path costs one comparison, one assignment and one increment.
    private const int MaxReportedSites = 8;
    private static int sampleAttempts;

    /// A session is silently flooded with null draws before anyone opens the settings page, so once
    /// enough are intercepted we log a one-line hint pointing at the report and placeholder options.
    internal const int FloodHintThreshold = 50;
    private static bool floodSummarized;

    /// True once enough null draws have been intercepted to warrant a single summary hint.
    /// threshold > 0 keeps a zero threshold from logging on every draw.
    internal static bool ShouldSummarizeFlood(int substituted, int threshold) =>
        threshold > 0 && substituted >= threshold;

    // Bound ATTEMPTS, not distinct callers. One repeating caller must not trigger a stack walk
    // forever just because the distinct-site count never reaches eight.
    internal static bool ShouldSample(bool deepDiagnostic, ref int attempts)
    {
        if (deepDiagnostic) return true;
        if (attempts >= MaxReportedSites) return false;
        attempts++;
        return true;
    }

    /// Frames from these namespaces are plumbing, never the culprit - walk past them.
    private static readonly string[] SkipNamespacePrefixes =
    {
        "UnityEngine.", "HarmonyLib.", "ImageOptCompat.", "System.",
    };

    /// Fail-open: a guard that throws during construction would take the whole mod down with it,
    /// and what it guards against is a performance problem, not a correctness one.
    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(NullTextureGuard), nameof(Prefix)));

            foreach (var target in Targets())
            {
                try
                {
                    harmony.Patch(target, prefix: prefix);
                    InstalledCount++;
                }
                catch (Exception e)
                {
                    // One unpatchable overload must not cost us the other eight.
                    Log.Warning($"[ImageOptCompat] could not guard {target.Name}: {e.Message}");
                }
            }

            if (InstalledCount == 0)
                Log.Warning("[ImageOptCompat] null-texture guard found no GUI.DrawTexture overloads to patch. "
                          + "Unity's IMGUI module may have changed; the null-texture log flood is NOT being stopped.");
            else if (ImageOptCompatMod.Settings.verbose)
                Log.Message($"[ImageOptCompat] null-texture guard installed on {InstalledCount} draw method(s).");
        }
        catch (Exception e)
        {
            Log.Warning($"[ImageOptCompat] null-texture guard could not be installed: {e.Message}");
        }
    }

    /// Discovered by name rather than by listing nine explicit signatures. Unity's overloads differ
    /// in their later parameters (borderWidth vs borderWidths, borderRadius vs borderRadiuses), and
    /// Harmony binds injected parameters BY NAME - so enumerating is both shorter and safer than a
    /// hand-written list that silently matches nothing the day Unity renames an argument.
    private static IEnumerable<MethodInfo> Targets() => SelectTargets(typeof(GUI), typeof(Texture));

    /// The selection rule, with its two Unity types passed in so it can be exercised without a
    /// running Unity - same approach as VehicleReadback.NeedsCpuReadback and OrphanPaths.
    internal static IEnumerable<MethodInfo> SelectTargets(Type source, Type textureType)
    {
        // Plain reflection, not AccessTools: this is the same set AccessTools.GetDeclaredMethods
        // returns, but it keeps the selection rule free of Harmony. Lib.Harmony is referenced with
        // ExcludeAssets="runtime" (the game supplies 0Harmony), so a Harmony call here cannot be
        // exercised by the test host at all.
        const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.DeclaredOnly;

        // Only the TERMINAL overload of each name, not all ten. Unity's public DrawTexture
        // overloads are pure forwarders - DrawTexture(Rect, Texture) calls the 3-arg, which calls
        // the 4-arg, down to the 12-arg internal one that holds the null check and the warning.
        // Patching every link would run this prefix three or four times per single draw call, on
        // the hottest path in the game, to reach a check that exists in exactly one place.
        //
        // The terminal is identified as the overload with the MOST parameters, which stays correct
        // if Unity adds another convenience forwarder. UnityApiContractTests pins the expected
        // arity, so a restructure that moves the check fails a test instead of silently no-opping.
        var terminals = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        foreach (var m in source.GetMethods(AllDeclared))
        {
            if (!m.IsStatic) continue;
            if (!IsDrawMethodName(m.Name)) continue;

            // Harmony binds injected parameters BY NAME, so an overload that does not name its
            // texture "image" would bind nothing and the patch would be a silent no-op - the
            // failure mode this whole rule exists to catch.
            var parameters = m.GetParameters();
            var named = false;
            foreach (var p in parameters)
            {
                if (string.Equals(p.Name, "image", StringComparison.Ordinal) && p.ParameterType == textureType)
                {
                    named = true;
                    break;
                }
            }

            if (!named) continue;

            if (!terminals.TryGetValue(m.Name, out var best)
             || parameters.Length > best.GetParameters().Length)
                terminals[m.Name] = m;
        }

        return terminals.Values;
    }

    internal static bool IsDrawMethodName(string name) =>
        string.Equals(name, "DrawTexture", StringComparison.Ordinal)
     || string.Equals(name, "DrawTextureWithTexCoords", StringComparison.Ordinal);

    /// Runs on EVERY GUI draw, so the non-null path must stay allocation-free and branch-cheap.
    private static bool Prefix(ref Texture image)
    {
        // Unity overloads == / != on Object: this is false for a DESTROYED texture as well as a
        // null reference, which is exactly the case we need to catch. Do NOT rewrite this as
        // "is not null" - that is a reference comparison and skips Unity's alive check entirely.
        if (image != null || !ImageOptCompatMod.Settings.nullTextureGuard) return true;
        // Preserve Unity's invalid-context checks and Layout/input handling. Event.current can
        // be null outside OnGUI; never dereference it without testing it first. Read it once into a
        // local: the null-flood path is the common case the guard exists for, and a redundant second
        // property read per null draw costs the same every frame with no different result.
        if (!UnityData.IsInMainThread) return true;
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint)
            return true;

        // A transparent texture is NOT invisible with alphaBlend=false. Skip the invalid draw
        // instead, preserving the original DrawTexture null branch's no-draw behavior.
        var showPlaceholder = ImageOptCompatMod.Settings.nullTextureShowPlaceholder;
        if (showPlaceholder)
        {
            var replacement = BaseContent.BadTex;
            if (replacement == null) return false; // May not exist during early loading.
            image = replacement;
        }
        Substituted++;

        // One hint per session, once a flood is under way, so the fix's existence is discoverable
        // without opening the settings page. Log.Message (not Warning) so it reads as an
        // informational note rather than another symptom, and does not muddy the warning-counting
        // sampling tests.
        if (!floodSummarized && ShouldSummarizeFlood(Substituted, FloodHintThreshold))
        {
            floodSummarized = true;
            Log.Message("[ImageOptCompat] null-texture guard: " + Substituted + " null draw(s) intercepted so far. "
                      + "Open the settings page for the 'Copy diagnostic report' button, or turn on "
                      + "'Show a placeholder' to see which elements are missing their texture.");
        }

        // Normal mode walks the stack at most MaxReportedSites times, then the null path costs
        // only a comparison and two increments. Deep-diagnostic mode walks EVERY time to build an
        // accurate per-mod tally - far more expensive, and opt-in for exactly that reason.
        var deepDiagnostic = ImageOptCompatMod.Settings.nullTextureDeepDiagnostic;
        if (ShouldSample(deepDiagnostic, ref sampleAttempts)) ReportCallSite(deepDiagnostic);
        return showPlaceholder;
    }

    /// Names the first frame outside Unity, Harmony and ourselves, so the log says which mod asked
    /// to draw the missing texture. Once per distinct site - the point is to identify it, not to
    /// count it. NoInlining keeps this frame off the stack we are about to walk.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReportCallSite(bool tally)
    {
        try
        {
            // Neither collection is thread-safe and IMGUI is main-thread, but a stray off-thread
            // draw would corrupt them rather than merely race. Cheap to rule out.
            if (!UnityData.IsInMainThread) return;

            var (site, owner) = FindCaller();
            var key = $"{owner} -> {site}";

            if (tally && (Tally.ContainsKey(key) || Tally.Count < MaxTalliedSites))
                Tally[key] = Tally.TryGetValue(key, out var n) ? n + 1 : 1;

            if (ReportedSites.Count >= MaxReportedSites || !ReportedSites.Add(key)) return;

            Log.Warning($"[ImageOptCompat] null texture drawn by {site}, shipped by {owner}. Guarded the "
                      + "draw so Unity stops logging it every frame. This is a missing or destroyed "
                      + "texture in that mod, not a rendering fault. "
                      + $"Normal diagnostics sample only the first {MaxReportedSites} null draws; turn on the "
                      + "deep diagnostic in this patch's settings for a full per-mod count.");
        }
        catch (Exception e)
        {
            // Diagnostics must never be the thing that breaks the frame.
            Log.Warning($"[ImageOptCompat] null-texture guard could not identify a call site: {e.Message}");
        }
    }

    /// A dev-facing report: which mod, which method, how many times. Sorted worst-first so the
    /// offender to chase is the top line. Safe to call at any time.
    internal static string BuildReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Image Opt + Faster Game Loading Compatibility Patch - null texture report");
        sb.AppendLine($"Draw methods guarded: {InstalledCount}");
        sb.AppendLine($"Null draws intercepted: {Substituted}");
        sb.AppendLine($"Deep diagnostic: {(ImageOptCompatMod.Settings.nullTextureDeepDiagnostic ? "on" : "off")}");
        sb.AppendLine();

        if (Tally.Count > 0)
        {
            sb.AppendLine("Per call site, worst first:");
            var rows = new List<KeyValuePair<string, int>>(Tally);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var row in rows) sb.AppendLine($"  {row.Value,9:N0}  {row.Key}");

            if (Tally.Count >= MaxTalliedSites)
                sb.AppendLine($"  (list capped at {MaxTalliedSites} distinct sites)");
        }
        else if (ReportedSites.Count > 0)
        {
            sb.AppendLine("Call sites seen (counts need the deep diagnostic):");
            foreach (var site in ReportedSites) sb.AppendLine($"  {site}");
        }
        else
        {
            sb.AppendLine("No null texture draws were intercepted this session.");
        }

        return sb.ToString();
    }

    /// The offending method, and the mod that shipped it.
    private static (string Site, string Owner) FindCaller()
    {
        // fNeedFileInfo: false - symbols are absent for Workshop mods anyway, and reading them
        // would turn a costly call into an unacceptable one.
        var trace = new StackTrace(fNeedFileInfo: false);

        for (var i = 0; i < trace.FrameCount; i++)
        {
            var method = trace.GetFrame(i)?.GetMethod();
            var type = method?.DeclaringType;
            if (type == null) continue;

            var full = type.FullName ?? string.Empty;
            if (IsPlumbingFrame(full)) continue;

            return ($"{full}.{method!.Name}", ModAttribution.Describe(type));
        }

        return ("an unidentified caller", "unknown mod");
    }

    /// True for frames that are never the culprit - Unity's own draw code, Harmony's generated
    /// wrappers, this patch, and the BCL. Everything else is a mod, and a mod is what we want named.
    internal static bool IsPlumbingFrame(string typeFullName)
    {
        foreach (var prefix in SkipNamespacePrefixes)
        {
            if (typeFullName.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
