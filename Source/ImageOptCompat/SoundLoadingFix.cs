using System;
using System.IO;
using System.Threading;
using HarmonyLib;
using RuntimeAudioClipLoader;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

/// Two repairs in RimWorld's runtime sound loader (RuntimeAudioClipLoader.Manager). Nothing on
/// disk is changed, and both patch non-generic methods only.
///
/// 1. Extensible WAV headers. Manager.Load gives the file to CustomAudioFileReader, which accepts
///    only plain PCM or float headers; the "extensible" header many audio editors write fails, and
///    the sound plays as silence. A prefix hands the decoder an in-memory copy whose header is the
///    plain equivalent (WavHeaderFix). Measured in boot 2: all 8 sounds of the Hamster mod.
///
/// 2. The hidden reason. When decoding fails, Load's catch block calls
///    SetAudioClipLoadState(null, Failed) for a clip that was never created. That throws on the
///    null dictionary key before the next line can log the real error, so every such failure read
///    "Value cannot be null. Parameter name: key". Skipping that one null call lets the game log
///    what actually went wrong.
internal static class SoundLoadingFix
{
    internal static bool Installed { get; private set; }

    /// Sound files read through the header rewrite this session.
    internal static int Repaired;

    /// Failed loads whose real error was let through instead of the null-key exception.
    internal static int Unmasked;

    private const int PeekBytes = 4096;

    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var load = AccessTools.Method(typeof(Manager), nameof(Manager.Load),
                new[] { typeof(Stream), typeof(AudioFormat), typeof(string), typeof(bool), typeof(bool), typeof(bool) });
            var setState = AccessTools.Method(typeof(Manager), nameof(Manager.SetAudioClipLoadState),
                new[] { typeof(AudioClip), typeof(AudioDataLoadState) });

            if (load == null || setState == null)
            {
                Report.Write(ReportKind.Problem, "RimWorld's sound loader has changed, so the sound-loading repair is off. "
                                               + "Sound files with an extensible WAV header will stay silent.");
                return;
            }

            harmony.Patch(load, prefix: new HarmonyMethod(AccessTools.Method(typeof(SoundLoadingFix), nameof(LoadPrefix))));
            harmony.Patch(setState, prefix: new HarmonyMethod(AccessTools.Method(typeof(SoundLoadingFix), nameof(SetStatePrefix))));
            Installed = true;
        }
        catch (Exception e)
        {
            Report.Write(ReportKind.Problem, $"the sound-loading repair could not be installed: {e.Message}");
        }
    }

    /// Parameter names match Manager.Load exactly: Harmony binds them by name.
    private static void LoadPrefix(ref Stream dataStream, AudioFormat audioFormat, string unityAudioClipName)
    {
        if (!ImageOptCompatMod.Settings.fixSoundLoading || audioFormat != AudioFormat.wav) return;

        var original = dataStream;
        if (original == null || !original.CanSeek) return;

        long start;
        try { start = original.Position; }
        catch (Exception) { return; }

        try
        {
            // Peek first: an ordinary file is never read twice or copied.
            var peek = new byte[PeekBytes];
            var peeked = ReadUpTo(original, peek, peek.Length);
            original.Position = start;
            if (!WavHeaderFix.IsRewritable(peek, peeked)) return;

            var whole = new byte[original.Length - start];
            var read = ReadUpTo(original, whole, whole.Length);
            if (read < whole.Length)
            {
                var trimmed = new byte[read];
                Buffer.BlockCopy(whole, 0, trimmed, 0, read);
                whole = trimmed;
            }

            var rewritten = WavHeaderFix.Rewrite(whole);
            if (rewritten == null)
            {
                original.Position = start;
                return;
            }

            // Load disposes the stream it is given once it is done with it; the original is now ours.
            dataStream = new MemoryStream(rewritten, writable: false);
            original.Dispose();
            Interlocked.Increment(ref Repaired);

            // The log is not safe off the main thread; the settings page shows the count either way.
            if (UnityData.IsInMainThread)
                Report.Write(ReportKind.Info, $"read '{unityAudioClipName}' as plain PCM: its WAV header is the "
                                            + "extensible kind RimWorld's decoder rejects.");
        }
        catch (Exception)
        {
            // Leave the game to try the file exactly as before.
            try { original.Position = start; }
            catch (Exception) { /* A stream that cannot seek back fails in vanilla too. */ }
        }
    }

    /// Only the null clip is skipped; every real clip's state is set as normal.
    private static bool SetStatePrefix(AudioClip audioClip)
    {
        if (!ReferenceEquals(audioClip, null) || !ImageOptCompatMod.Settings.fixSoundLoading) return true;
        Interlocked.Increment(ref Unmasked);
        return false;
    }

    private static int ReadUpTo(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }

        return total;
    }
}
