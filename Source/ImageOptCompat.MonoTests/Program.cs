using System;
using System.Collections.Generic;
using System.IO;
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
            var audio = Array.IndexOf(args, "--audio");
            if (audio >= 0) { CheckRealDecoder(args[audio + 1]); return 0; }

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
            CheckAttribution(h);
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

    /// The first live boot blamed WanderJoinsPlus for every null draw. Two faults combined: a
    /// patched method's frame reads as MonoMod.Utils.DynamicMethodDefinition unless resolved
    /// through Harmony, and Harmony's assembly is listed under every mod that ships a copy of it.
    /// This reproduces both on the game's own Mono and Harmony, so neither can come back unseen.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckAttribution(Harmony h)
    {
        var fixture = new ModContentPack { Name = "Fixture Mod", PackageId = "fixture.mod" };
        fixture.assemblies.loadedAssemblies.Add(typeof(Program).Assembly);
        var harmonyMod = new ModContentPack { Name = "Harmony", PackageId = "brrainz.harmony" };
        var bundler = new ModContentPack { Name = "WanderJoinsPlus", PackageId = "ogliss.wanderjoinsplus" };
        harmonyMod.assemblies.loadedAssemblies.Add(typeof(Harmony).Assembly);
        bundler.assemblies.loadedAssemblies.Add(typeof(Harmony).Assembly);
        LoadedModManager.RunningMods.AddRange(new[] { harmonyMod, fixture, bundler });
        ModAttribution.Reset();

        h.Patch(typeof(GUI).GetMethod(nameof(GUI.DrawTexture)),
            prefix: new HarmonyMethod(typeof(ImageOptCompat.Probe.AttributionProbe).GetMethod(nameof(ImageOptCompat.Probe.AttributionProbe.Prefix))));
        FixtureMod.Window.DoContents();

        Check(ImageOptCompat.Probe.AttributionProbe.Resolved, "the patched draw frame maps back to GUI.DrawTexture through Harmony");
        Check(ImageOptCompat.Probe.AttributionProbe.NaiveType != typeof(GUI).FullName,
            "a plain GetMethod() read of that frame does not see GUI (it sees: "
            + (ImageOptCompat.Probe.AttributionProbe.NaiveType ?? "null") + ")");
        Check(ImageOptCompat.Probe.AttributionProbe.Seen == ("FixtureMod.Window.DoContents", "Fixture Mod (fixture.mod)"),
            "null-draw attribution names the calling mod frame, got " + ImageOptCompat.Probe.AttributionProbe.Seen);
        var harmonyOwner = ModAttribution.Describe(typeof(Harmony));
        Check(!ModAttribution.NamesOneMod(harmonyOwner) && !harmonyOwner.Contains("WanderJoinsPlus"),
            "Harmony's assembly is credited to no single mod, got " + harmonyOwner);

        FixtureMod.Loader.LoadIcon();
        Check(MissingTextureReport.BuildReport().Contains("Fixture Mod (fixture.mod) at FixtureMod.Loader.LoadIcon"),
            "a code-driven missing texture is attributed through the real Log.Error detour");

        // The startup check's in-game version of the checks above: it patches a private method,
        // resolves the frame through Harmony, and must remove its patch again.
        Check(StartupCheck.HarmonyFramesResolve(h), "startup self-test resolves a patched frame on the game's Harmony");
        var probeTarget = typeof(StartupCheck).GetMethod("ProbeTarget", BindingFlags.NonPublic | BindingFlags.Static)!;
        var leftover = Harmony.GetPatchInfo(probeTarget);
        Check(leftover == null || leftover.Prefixes.Count == 0, "startup self-test removes its own patch");
    }

    /// End to end against the GAME's decoder, not a stand-in: load the installed Assembly-CSharp,
    /// give its CustomAudioFileReader the file that failed in boot 2, then the rewritten copy.
    /// The first must fail (the bug, reproduced) and the second must decode with the same format.
    private static void CheckRealDecoder(string wavPath)
    {
        var managed = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var game = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
        var readerType = game.GetType("RuntimeAudioClipLoader.CustomAudioFileReader", throwOnError: true)!;
        var wav = Enum.Parse(game.GetType("RuntimeAudioClipLoader.AudioFormat", throwOnError: true)!, "wav");
        var original = File.ReadAllBytes(wavPath);

        Exception? originalError = null;
        try { Activator.CreateInstance(readerType, new MemoryStream(original), wav); }
        catch (Exception e) { originalError = e.InnerException ?? e; }
        Check(originalError != null, "the game's decoder rejects the original extensible file ("
            + (originalError?.GetType().Name ?? "no error") + ": " + originalError?.Message + ")");

        var rewritten = WavHeaderFix.Rewrite(original);
        Check(rewritten != null, "the header rewrite accepts the measured file");
        var reader = Activator.CreateInstance(readerType, new MemoryStream(rewritten!), wav)!;
        var length = (long)readerType.GetProperty("Length")!.GetValue(reader)!;
        var format = readerType.GetProperty("WaveFormat")!.GetValue(reader)!;
        var rate = (int)format.GetType().GetProperty("SampleRate")!.GetValue(format)!;
        var channels = (int)format.GetType().GetProperty("Channels")!.GetValue(format)!;
        Check(length > 0 && rate == 96000 && channels == 2,
            $"the game's decoder reads the rewritten file: {length} bytes of samples, {rate} Hz, {channels} channels");
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

namespace ImageOptCompat.Probe
{
    /// A Harmony prefix standing where production's null-texture prefix stands. It lives under the
    /// ImageOptCompat namespace so the walk treats it as plumbing, exactly as it treats ours.
    public static class AttributionProbe
    {
        public static (string Site, string Owner) Seen;
        public static bool Resolved;
        public static string? NaiveType;

        public static void Prefix()
        {
            var trace = new System.Diagnostics.StackTrace(false);
            Seen = ModAttribution.FindCaller(trace, Array.Empty<string>());
            for (var i = 0; i < trace.FrameCount; i++)
            {
                var frame = trace.GetFrame(i);
                var resolved = ModAttribution.FrameMethod(frame);
                if (resolved?.DeclaringType != typeof(GUI) || resolved.Name != nameof(GUI.DrawTexture)) continue;
                Resolved = true;
                NaiveType = frame.GetMethod()?.DeclaringType?.FullName;
                break;
            }
        }
    }
}
