using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ImageOptCompat;

/// Guards the non-generic sound-grain constructor before it reads AudioClip.length. A failed
/// decoder can leave a live clip whose length is unsafe to read. Both Unity and RimWorld's own
/// RuntimeAudioClipLoader maintain load state, so either Failed state rejects the clip.
/// A shared silent clip preserves a valid grain for clip, folder and custom grain callers.
/// This avoids patching ContentFinder<T>, whose reference-type specializations share code on Mono.
internal static class FailedAudioClipGuard
{
    /// Failed clips replaced in sound grains this session.
    internal static int Suppressed;

    /// Clip names (the runtime loader uses source paths), for the diagnostic report.
    /// Bounded: a broken mod can ask for the same clip repeatedly.
    private static readonly HashSet<string> SuppressedPaths = new(StringComparer.Ordinal);

    private const int MaxNamedPaths = 64;
    private static AudioClip? silentClip;

    /// A line for the diagnostic report. States whether the guard is even watching, because a
    /// crash guard that found nothing and one that never installed read identically otherwise.
    internal static string ReportLine()
    {
        if (!ImageOptCompatMod.Settings.guardFailedAudioClips)
            return "Failed-audio guard: disabled in settings.";
        if (!Installed)
            return "Failed-audio guard: NOT INSTALLED. An undecodable sound file can still crash the game.";

        if (SuppressedPaths.Count == 0)
            return $"Failed-audio guard: active, {Suppressed} interception(s), no undecodable sound files seen.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Failed-audio guard: {Suppressed} interception(s) across {SuppressedPaths.Count} clip(s).");
        sb.AppendLine("These sound files exist but the engine cannot decode them. Reading their length");
        sb.AppendLine("is unsafe, so the grain uses a silent clip instead. The mod shipping");
        sb.AppendLine("them should re-encode to 16-bit PCM WAV or OGG:");

        foreach (var p in SuppressedPaths) sb.AppendLine($"    {p}");

        return sb.ToString();
    }

    internal static bool Installed { get; private set; }

    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            // ContentFinder<AudioClip>.Get shares code with Texture2D on Mono: patching it
            // redirects texture requests into the audio lookup. Guard the non-generic consumer
            // instead. This also covers folder grains and custom grains that bypass Get entirely.
            var target = AccessTools.Constructor(typeof(ResolvedGrain_Clip), new[] { typeof(AudioClip) });

            if (target == null)
            {
                Log.Warning("[ImageOptCompat] ResolvedGrain_Clip(AudioClip) was not found. "
                          + "The failed-audio guard is OFF, so a mod with an undecodable sound file can still "
                          + "crash the game in Unity's audio code.");
                return;
            }

            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(FailedAudioClipGuard), nameof(Prefix))));
            Installed = true;
        }
        catch (Exception e)
        {
            Log.Warning($"[ImageOptCompat] failed-audio guard could not be installed: {e.Message}");
        }
    }

    private static void Prefix(ref AudioClip clip)
    {
        if (!ImageOptCompatMod.Settings.guardFailedAudioClips || clip == null) return;
        AudioClip? candidate = clip;
        string name;
        try { name = clip.name; }
        catch { name = "<unreadable clip>"; }
        FilterFailed(name, ref candidate);
        if (candidate != null) return;

        // Skipping a constructor would leave a half-initialized grain that later dereferences
        // clip. One owned silent clip keeps every downstream consumer valid. Created only
        // on failure, never for ordinary Loading/Unloaded clips, and reused for the session.
        // Use a second, not one PCM frame: SubSustainer repeats short clips as often as 100 Hz.
        if (silentClip == null)
            silentClip = AudioClip.Create("ImageOptCompat failed audio", 44100, 1, 44100, false);
        clip = silentClip!;
    }

    private static void FilterFailed(string itemPath, ref AudioClip? __result)
    {
        if (!ImageOptCompatMod.Settings.guardFailedAudioClips) return;

        // Unity's == overload: already null, or destroyed. Vanilla handles that case correctly.
        if (__result == null) return;

        AudioDataLoadState state;

        try
        {
            state = __result!.loadState;
            // RimWorld's RuntimeAudioClipLoader creates a live Unity clip before decoding PCM.
            // Its own dictionary can say Failed while Unity still says Loaded. Keep the Unity
            // failure as well: Manager.GetAudioClipLoadState returns Unloaded for untracked clips.
            if (state != AudioDataLoadState.Failed
                && RuntimeAudioClipLoader.Manager.GetAudioClipLoadState(__result) == AudioDataLoadState.Failed)
                state = AudioDataLoadState.Failed;
        }
        catch (Exception)
        {
            // loadState is extern too. If even reading the state throws, the clip is in no
            // condition to have its length taken, so treat it as missing.
            state = AudioDataLoadState.Failed;
        }

        // Unloaded and Loading are ordinary states for a streaming clip. Only a decode failure is
        // unusable, and only it is dangerous to read length from.
        if (state != AudioDataLoadState.Failed) return;

        __result = null;
        Suppressed++;

        // Log once per clip. A mod can request the same sound hundreds of times, and this is a
        // crash guard, not a reason to flood the log we just spent the session clearing.
        if (SuppressedPaths.Count >= MaxNamedPaths || !SuppressedPaths.Add(itemPath)) return;

        Log.Warning($"[ImageOptCompat] the sound file for '{itemPath}' failed to decode, so it is being "
                  + "replaced with silence for sound grains. Reading its length may crash Unity's native audio "
                  + "code. The mod that ships this file needs to re-encode it as a standard PCM WAV or OGG.");
    }
}
