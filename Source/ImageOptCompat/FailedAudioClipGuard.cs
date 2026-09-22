using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

/// Stops a hard crash in Unity's native audio code when a mod ships a sound file Unity cannot
/// decode.
///
/// MEASURED CRASH (2026-09-21, two runs, both died the same way):
///     UnityEngine.AudioClip:get_length
///     Verse.Sound.ResolvedGrain_Clip:.ctor
///     Verse.Sound.AudioGrain_Clip/&lt;GetResolvedGrains&gt;d__1:MoveNext
///     Verse.Sound.SubSoundDef:&lt;ResolveReferences&gt;b__37_0
///     FasterGameLoading.DeferredLoader/&lt;ResolveSubSoundDefsCoroutine&gt;d__5:MoveNext
///
/// The chain, and why nothing already in place catches it:
///
///  - Verse.ModContentLoader.LoadItem gives a FAILED TEXTURE a fallback (BaseContent.BadTex) and
///    gives FAILED AUDIO nothing, returning null. That asymmetry is vanilla behaviour.
///  - Verse.Sound.AudioGrain_Clip.GetResolvedGrains does guard the plain case:
///    `if (audioClip != null)`. Unity's operator makes that catch a destroyed clip too, and it
///    fired 625 times in the crash run. So a missing clip is handled correctly.
///  - The clip that crashed therefore PASSED that guard. It is a live AudioClip whose audio data
///    failed to decode. ResolvedGrain_Clip's constructor then reads `clip.length`, which is
///    extern, and Unity dereferences the absent sample data. That is an access violation, not a
///    managed exception.
///  - Faster Game Loading wraps each resolution in try/catch and logs "Error resolving AudioGrain".
///    A catch block cannot catch an access violation, so its guard does not help here.
///
/// THE FIX, and why it is shaped this way: report a decode-failed clip as MISSING, so RimWorld's
/// own guard above handles it on the path it already has. Nothing here replaces vanilla or Faster
/// Game Loading behaviour, and no new error path is invented.
///
/// Only AudioDataLoadState.Failed is treated as missing. Unloaded and Loading are normal states
/// for a clip that streams or has not been touched yet, and nulling those would silence sounds
/// that were going to work.
internal static class FailedAudioClipGuard
{
    /// Clips reported as missing this session.
    internal static int Suppressed;

    /// The paths involved, so the settings report can name them without trawling the log.
    /// Bounded: a broken mod can ask for the same clip repeatedly.
    private static readonly HashSet<string> SuppressedPaths = new(StringComparer.Ordinal);

    private const int MaxNamedPaths = 64;

    /// A line for the diagnostic report. States whether the guard is even watching, because a
    /// crash guard that found nothing and one that never installed read identically otherwise.
    internal static string ReportLine()
    {
        if (!Installed)
            return "Failed-audio guard: NOT INSTALLED. An undecodable sound file can still crash the game.";

        if (SuppressedPaths.Count == 0)
            return $"Failed-audio guard: active, {Suppressed} interception(s), no undecodable sound files seen.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Failed-audio guard: {Suppressed} interception(s) across {SuppressedPaths.Count} clip(s).");
        sb.AppendLine("These sound files exist but the engine cannot decode them. Reading their length");
        sb.AppendLine("would crash the game, so they are reported as missing instead. The mod shipping");
        sb.AppendLine("them should re-encode to 16-bit PCM WAV or OGG:");

        foreach (var p in SuppressedPaths) sb.AppendLine($"    {p}");

        return sb.ToString();
    }

    internal static bool Installed { get; private set; }

    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(ContentFinder<AudioClip>), nameof(ContentFinder<AudioClip>.Get),
                new[] { typeof(string), typeof(bool) });

            if (target == null)
            {
                Log.Warning("[ImageOptCompat] ContentFinder<AudioClip>.Get(string, bool) was not found. "
                          + "The failed-audio guard is OFF, so a mod with an undecodable sound file can still "
                          + "crash the game in Unity's audio code.");
                return;
            }

            harmony.Patch(target,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(FailedAudioClipGuard), nameof(Postfix))));
            Installed = true;
        }
        catch (Exception e)
        {
            Log.Warning($"[ImageOptCompat] failed-audio guard could not be installed: {e.Message}");
        }
    }

    /// Parameter names match ContentFinder&lt;T&gt;.Get exactly. Harmony binds them BY NAME, and a
    /// mismatch would bind nothing while still reporting the patch as installed.
    private static void Postfix(string itemPath, ref AudioClip? __result)
    {
        if (!ImageOptCompatMod.Settings.guardFailedAudioClips) return;

        // Unity's == overload: already null, or destroyed. Vanilla handles that case correctly.
        if (__result == null) return;

        AudioDataLoadState state;

        try
        {
            state = __result!.loadState;
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
                  + "reported as missing. Reading its length would crash the game in Unity's native audio "
                  + "code. The mod that ships this file needs to re-encode it as a standard PCM WAV or OGG.");
    }
}
