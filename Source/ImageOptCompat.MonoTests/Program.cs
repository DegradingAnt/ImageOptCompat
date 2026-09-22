using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ImageOptCompat;
using UnityEngine;
using Verse;
using Verse.Sound;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        checks++;
        Console.WriteLine("PASS: " + message);
    }
    private static int Main(string[] args)
    {
        try
        {
            Check(Type.GetType("Mono.Runtime") != null, "running on Mono, not the dotnet test host");
            Console.WriteLine("Core: " + typeof(object).Assembly.Location);
            Console.WriteLine("Harmony: " + typeof(Harmony).Assembly.Location);
            if (args.Contains("--legacy")) { ReproduceLegacyFailure(); return 0; }

            var dictionary = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
            foreach (var key in dictionary.Keys.ToList()) dictionary[key] = 3;
            Check(dictionary.Values.All(x => x == 3), "snapshot permits overwrites on the game's runtime");
            bool invalidated = false;
            try { foreach (var key in dictionary.Keys) dictionary[key] = 4; }
            catch (InvalidOperationException) { invalidated = true; }
            Check(invalidated, "live Keys iteration fails on our own overwrite, without another writer");

            var h = new Harmony("imageoptcompat.mono.regression");
            MissingTextureReport.TryInstall(h);
            FailedAudioClipGuard.TryInstall(h);
            Check(MissingTextureReport.Installed && FailedAudioClipGuard.Installed, "production hooks installed");
            Check(!Harmony.GetAllPatchedMethods().Any(x => x.DeclaringType?.IsGenericType == true), "no generic-class detours installed");
            CheckAssetTypes();
            CheckTextures();
            CheckAudio();
            Console.WriteLine("All " + checks + " Mono/Harmony checks passed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckAssetTypes()
    {
        var texture = new Texture2D();
        var clip = new AudioClip();
        ContentFinder<Texture2D>.Assets["same-path"] = texture;
        ContentFinder<AudioClip>.Assets["same-path"] = clip;
        Check(ReferenceEquals(ContentFinder<Texture2D>.Get("same-path"), texture), "texture request keeps texture specialization");
        Check(ReferenceEquals(ContentFinder<AudioClip>.Get("same-path"), clip), "audio request keeps audio specialization");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckTextures()
    {
        var texture = new Texture2D();
        ContentFinder<Texture2D>.Assets["Hologram"] = texture;
        Check(ReferenceEquals(ContentFinder<Texture2D>.Get("Hologram.dds"), texture), "cache suffix repaired through real Harmony fallback hook");
        Check(ReferenceEquals(ContentFinder<Texture2D>.Get("Hologram.dds.zstd"), texture), "full cache suffix repaired");
        var bundle = new Texture2D();
        ContentFinder<Texture2D>.Bundles["Hologram.dds"] = bundle;
        Check(ReferenceEquals(ContentFinder<Texture2D>.Get("Hologram.dds"), bundle), "original bundled dotted name wins over correction");
        ContentFinder<Texture2D>.Bundles.Clear();
        Check(ResourcesAPI.Instance.Load("Textures/Hologram.dds", typeof(Texture2D)) == null, "direct resource-only lookup retains its semantics");
        var before = MissingTextureReport.DistinctPaths;
        ContentFinder<Texture2D>.Get("optional", false);
        ContentFinder<AudioClip>.Get("missing-audio");
        Check(MissingTextureReport.DistinctPaths == before, "optional and audio failures are not texture reports");
        ContentFinderRequester.requester = new Def { defName = "BrokenDef", modContentPack = new ModContentPack() };
        ContentFinder<Texture2D>.Get("missing-required");
        Check(MissingTextureReport.BuildReport().Contains("BrokenDef"), "required error retains requester attribution");
        Check(Log.Errors.Any(x => x.Contains("missing-required")), "diagnostics do not suppress the original error");
        ContentFinderRequester.requester = null;
        var requests = ContentFinder<Texture2D>.Requests.Count;
        Check(ContentFinder<Texture2D>.Get("Hologram.dds.dds") == null, "retry does not strip multiple suffixes");
        Check(ContentFinder<Texture2D>.Requests.Count == requests + 2, "exactly one corrected lookup attempted");
        ImageOptCompatMod.Settings.fixDoubleExtensionPaths = false;
        Check(ContentFinder<Texture2D>.Get("Hologram.dds", false) == null, "repair toggle works while hooks remain installed");
        ImageOptCompatMod.Settings.fixDoubleExtensionPaths = true;
        ImageOptCompatMod.ImageOptActive = false;
        Check(ContentFinder<Texture2D>.Get("Hologram.dds", false) == null, "inactive Image Opt disables repair");
        ImageOptCompatMod.ImageOptActive = true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckAudio()
    {
        foreach (var state in new[] { AudioDataLoadState.Loaded, AudioDataLoadState.Loading, AudioDataLoadState.Unloaded })
        {
            var good = new AudioClip { loadState = state };
            Check(ReferenceEquals(new ResolvedGrain_Clip(good).clip, good), "healthy/streaming state preserved: " + state);
        }
        var failed = new AudioClip { name = "UnityFailed", loadState = AudioDataLoadState.Failed };
        var grain = new ResolvedGrain_Clip(failed);
        Check(failed.LengthReads == 0 && !ReferenceEquals(grain.clip, failed), "Unity failure replaced before native length read");
        var loaderFailed = new AudioClip { name = "LoaderFailed", loadState = AudioDataLoadState.Loaded };
        RuntimeAudioClipLoader.Manager.States[loaderFailed] = AudioDataLoadState.Failed;
        var second = new ResolvedGrain_Clip(loaderFailed);
        Check(loaderFailed.LengthReads == 0 && ReferenceEquals(second.clip, grain.clip), "loader-only failure uses the shared silent clip");
        Check(second.duration == 1f && second.clip != null, "constructor initializes a usable grain without rapid silent-loop restarts");
        ImageOptCompatMod.Settings.guardFailedAudioClips = false;
        bool reachedOriginal = false;
        try { _ = new ResolvedGrain_Clip(failed); } catch (InvalidOperationException) { reachedOriginal = true; }
        Check(reachedOriginal, "disabled audio guard leaves original constructor behavior");
        ImageOptCompatMod.Settings.guardFailedAudioClips = true;
    }

    // A separate process mode demonstrates why the former production design is unsafe.
    public static void TexturePostfix(ref Texture2D? __result) { }
    public static void AudioPostfix(ref AudioClip? __result) { }
    private static void ReproduceLegacyFailure()
    {
        var h = new Harmony("imageoptcompat.mono.legacy");
        h.Patch(typeof(ContentFinder<Texture2D>).GetMethod("Get"), postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(TexturePostfix))));
        LegacyLookup("audio-with-texture-patch");
        h.Patch(typeof(ContentFinder<AudioClip>).GetMethod("Get"), postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(AudioPostfix))));
        LegacyLookup("texture-with-audio-patch");
        Check(Log.Errors.Any(x => x.Contains("Texture2D at 'audio-with-texture-patch'")), "legacy texture patch corrupts audio lookup type");
        Check(Log.Errors.Any(x => x.Contains("AudioClip at 'texture-with-audio-patch'")), "legacy audio patch corrupts texture lookup type");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LegacyLookup(string path)
    {
        ContentFinder<Texture2D>.Get(path);
        ContentFinder<AudioClip>.Get(path);
    }
}
