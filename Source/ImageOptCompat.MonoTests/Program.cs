using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
            CheckPatchAudit();
            // Last: once installed, the finder sees every exception this process turns into text.
            CheckRepeatedErrorFinder(h);
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

    /// The startup check's audit, on real Harmony. Codex's review 4 found the readback check passing
    /// with no hook installed at all. The audit must count only patches live under our own id, and
    /// must see them go when they are removed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckPatchAudit()
    {
        const string owner = "imageoptcompat.mono.audit";
        var audit = new Harmony(owner);
        var classes = PatchAudit.PatchClassesIn(typeof(ImageOptCompat.AuditFixture.Hooks));
        Check(classes.Count == 2, "the audit finds a container's patch classes the way PatchAll does");
        Check(PatchAudit.LiveCount(owner, classes) == 0, "no hook reads as live before anything is patched");

        audit.CreateClassProcessor(typeof(ImageOptCompat.AuditFixture.Hooks.OnFirst)).Patch();
        Check(PatchAudit.LiveCount(owner, classes) == 1, "one installed class of two reads as one live hook");

        var second = AccessTools.Method(typeof(ImageOptCompat.AuditFixture.Targets), nameof(ImageOptCompat.AuditFixture.Targets.Second));
        new Harmony("another.mod").Patch(second, prefix: new HarmonyMethod(typeof(ImageOptCompat.AuditFixture.OtherMod), nameof(ImageOptCompat.AuditFixture.OtherMod.Prefix)));
        Check(!PatchAudit.IsLive(owner, typeof(ImageOptCompat.AuditFixture.Hooks.OnSecond)),
            "another mod's patch on the same target does not count as ours");

        audit.CreateClassProcessor(typeof(ImageOptCompat.AuditFixture.Hooks.OnSecond)).Patch();
        Check(PatchAudit.LiveCount(owner, classes) == 2, "both hooks read as live once both are installed");

        audit.UnpatchAll(owner);
        Check(PatchAudit.LiveCount(owner, classes) == 0, "removed hooks no longer read as live");
        Check(Harmony.GetPatchInfo(second)?.Prefixes.Count == 1, "the other mod's patch is untouched");

        // A hand-installed postfix, as the main-menu status line is, then two mods that call Unpatch
        // the ways the release boot's pack does: Cherry Picker removes only its own postfix, and No
        // Version In Pause Menu removes every owner's ("*") from VersionControl.DrawInfoInCorner.
        var first = AccessTools.Method(typeof(ImageOptCompat.AuditFixture.Targets), nameof(ImageOptCompat.AuditFixture.Targets.First));
        var ours = AccessTools.Method(typeof(ImageOptCompat.AuditFixture.StatusLine), nameof(ImageOptCompat.AuditFixture.StatusLine.Postfix));
        audit.Patch(first, postfix: new HarmonyMethod(ours));
        Check(PatchAudit.IsLive(owner, first, ours), "a hand-installed postfix reads as live");
        new Harmony("Owlchemist.CherryPicker.Unpatcher").Unpatch(first, HarmonyPatchType.Postfix, "Owlchemist.CherryPicker");
        Check(PatchAudit.IsLive(owner, first, ours), "a mod removing only its own postfixes leaves ours live");
        new Harmony("Jetroid.DoNotDrawVersion").Unpatch(first, HarmonyPatchType.Postfix, "*");
        Check(!PatchAudit.IsLive(owner, first, ours), "a mod removing every owner's postfixes is seen");
    }

    /// The repeated-error finder on the game's own runtime and Harmony. Codex's review 4 found two
    /// faults: an error logged the RimWorld way, "Log.Error(... + ex)", never reached the finder, and
    /// two mods failing in one shared helper were counted as one, blamed on whichever came first.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckRepeatedErrorFinder(Harmony h)
    {
        RepeatedErrorFinder.TryInstall(h);
        Check(RepeatedErrorFinder.Installed, "the finder hooks the runtime's stack-trace method");

        var helper = DynamicMethodIn("FixtureGame", "FixtureGame.Helper", callee: null);
        var modA = DynamicMethodIn("ModA", "ModA.Caller", helper);
        var modB = DynamicMethodIn("ModB", "ModB.Caller", helper);
        foreach (var (name, method) in new[] { ("ModA", modA), ("ModB", modB) })
        {
            var pack = new ModContentPack { Name = name, PackageId = name.ToLowerInvariant() };
            pack.assemblies.loadedAssemblies.Add(method.DeclaringType!.Assembly);
            LoadedModManager.RunningMods.Add(pack);
        }

        ModAttribution.Reset();
        RepeatedErrorFinder.Reset();

        // The RimWorld way, as Verse.Root.Update writes it: caught, then concatenated into the text.
        for (var i = 0; i < RepeatedErrorFinder.NoticeAt; i++)
            Log.Error("Root level exception in Update(): " + ExceptionFrom(modB));
        var row = RepeatedErrorFinder.Snapshot().Single();
        Check(row.Count == 100 && row.Owner == "ModB (modb)" && row.Site == "ModB.Caller.Run" && row.ThrownIn == "FixtureGame.Helper.Run",
            $"errors caught and logged the RimWorld way are counted: {row.Count} from {row.Owner} at {row.Site}, thrown in {row.ThrownIn}");
        Check(Log.Warnings.Any(w => w.Contains("ModB (modb) has thrown the same InvalidOperationException 100 times")),
            "the notice names the mod");

        // Unity's formatter reads ex.StackTrace several times; an outer exception's text includes the inner one's.
        var once = ExceptionFrom(modB);
        _ = once.StackTrace;
        _ = once.StackTrace;
        _ = once.ToString();
        _ = new InvalidOperationException("outer", once).ToString();
        Check(RepeatedErrorFinder.Snapshot().Single().Count == 101, "one exception is one occurrence, however often its text is read");

        // In game, the Harmony mod's prefix replaces the text and skips the original. The postfix
        // must still run: that is the arrangement behind every "Duplicate stacktrace" line.
        var getStackTrace = AccessTools.Method(typeof(Environment), "GetStackTrace", new[] { typeof(Exception), typeof(bool) });
        var harmonyMod = new Harmony("net.pardeike.rimworld.lib.harmony");
        harmonyMod.Patch(getStackTrace, prefix: new HarmonyMethod(typeof(ImageOptCompat.Probe.HarmonyModStandIn), nameof(ImageOptCompat.Probe.HarmonyModStandIn.Prefix)));
        var text = ExceptionFrom(modB).ToString();
        harmonyMod.UnpatchAll(harmonyMod.Id);
        Check(text.Contains("Duplicate stacktrace") && RepeatedErrorFinder.Snapshot().Single().Count == 102,
            "behind a prefix that skips the original, as the Harmony mod's does, the error is still counted");

        RepeatedErrorFinder.Reset();
        _ = ExceptionFrom(modA).ToString();
        for (var i = 0; i < RepeatedErrorFinder.NoticeAt; i++) _ = ExceptionFrom(modB).ToString();
        var rows = RepeatedErrorFinder.Snapshot();
        Check(rows.Count == 2 && rows[0].Owner == "ModB (modb)" && rows[0].Count == 100 && rows[1].Owner == "ModA (moda)" && rows[1].Count == 1,
            "two mods failing in one shared helper are two errors, each named for its own mod");

        _ = Environment.StackTrace;
        Check(RepeatedErrorFinder.Snapshot().Sum(r => r.Count) == 101, "a stack trace asked for without an exception is not an error");

        // No mod frame at all: a game method that a mod patched. The notice names who patched it.
        RepeatedErrorFinder.Reset();
        var toInt = AccessTools.Method(typeof(Convert), nameof(Convert.ToInt32), new[] { typeof(string) });
        h.Patch(toInt, prefix: new HarmonyMethod(typeof(FixtureMod.Patches), nameof(FixtureMod.Patches.ToInt32Prefix)));
        for (var i = 0; i < RepeatedErrorFinder.NoticeAt; i++) _ = ExceptionFrom(toInt, "not a number").ToString();
        h.Unpatch(toInt, HarmonyPatchType.Prefix, h.Id);
        Check(Log.Warnings.Any(w => w.Contains("No single mod's code is on its stack") && w.Contains("carry patches from Fixture Mod (fixture.mod)")),
            "an error through patched game code names the mods whose patches it passed through");
    }

    /// A method in an assembly of its own, the way each mod's code lives in its own DLL. It calls
    /// `callee`, or throws when there is none.
    private static MethodInfo DynamicMethodIn(string assemblyName, string typeName, MethodInfo? callee)
    {
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule(assemblyName).DefineType(typeName, TypeAttributes.Public);
        var method = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        method.SetImplementationFlags(MethodImplAttributes.NoInlining);
        var il = method.GetILGenerator();
        if (callee == null)
        {
            il.Emit(OpCodes.Ldstr, "shared helper failed");
            il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
            il.Emit(OpCodes.Throw);
        }
        else
        {
            il.Emit(OpCodes.Call, callee);
            il.Emit(OpCodes.Ret);
        }

        return type.CreateType()!.GetMethod("Run")!;
    }

    private static Exception ExceptionFrom(MethodInfo method, params object[] args)
    {
        try { method.Invoke(null, args); }
        catch (TargetInvocationException e) { return e.InnerException!; }
        throw new InvalidOperationException("the fixture did not throw");
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

namespace ImageOptCompat.AuditFixture
{
    public static class Targets
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public static int First(int value) => value + 1;
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Second(int value) => value + 2;
    }

    /// Shaped like Texture2DReadPatches: patch classes nested in a container. One names its target
    /// without argument types, the other with them, so both ways of resolving a target run.
    public static class Hooks
    {
        [HarmonyPatch(typeof(Targets), nameof(Targets.First))]
        public static class OnFirst { public static void Prefix() { } }

        [HarmonyPatch(typeof(Targets), nameof(Targets.Second), typeof(int))]
        public static class OnSecond { public static void Postfix() { } }
    }

    public static class OtherMod { public static void Prefix() { } }

    /// Stands where MainMenuStatus.Postfix stands: one patch method installed by hand.
    public static class StatusLine { public static void Postfix() { } }
}

namespace ImageOptCompat.Probe
{
    /// Does what the Harmony mod's prefix on the same method does once a trace has been seen:
    /// replaces the text and skips the original.
    public static class HarmonyModStandIn
    {
        public static bool Prefix(Exception e, bool needFileInfo, ref string __result)
        {
            __result = "[Ref 0] Duplicate stacktrace, see ref for original";
            return false;
        }
    }

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
