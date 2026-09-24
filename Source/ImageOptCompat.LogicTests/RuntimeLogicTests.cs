using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using ImageOptCompat;
using NUnit.Framework;
using UnityEngine;
using Verse;
using UObject = UnityEngine.Object;

namespace ImageOptCompat.LogicTests;

// Deliberately tests actual source decisions, not Unity rendering or Harmony installation.
[TestFixture, NonParallelizable]
public sealed class RuntimeLogicTests
{
    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static object? Call(Type type, string name, params object?[] args) =>
        type.GetMethod(name, Statics)!.Invoke(null, args);
    private static void Set(Type type, string name, object? value) => type.GetField(name, Statics)!.SetValue(null, value);
    private static void Clear(Type type, string name) => type.GetField(name, Statics)!.GetValue(null)!.GetType()
        .GetMethod("Clear")!.Invoke(type.GetField(name, Statics)!.GetValue(null), null);

    [SetUp]
    public void Reset()
    {
        ImageOptCompatMod.Settings = new();
        ImageOptCompatMod.ImageOptActive = true;
        UnityData.MeasureDiagnosticChecks = false;
        UnityData.IsInMainThread = true;
        Texture2D.FailAt = null;
        Texture2DReadPatches.ClearCopies();
        ImageOpt.Texture2DPatch.NativeTextures.Clear();
        ContentFinder<Texture2D>.Assets.Clear();
        ContentFinder<Texture2D>.Requests.Clear();
        ContentFinder<Texture2D>.Bundles.Clear();
        ResourcesAPI.Assets.Clear();
        ResourcesAPI.Postprocess = ResourcePostfix;
        Log.ErrorObserver = text => Call(typeof(MissingTextureReport), "ObserveError", text);
        RuntimeAudioClipLoader.Manager.States.Clear();
        ContentFinderRequester.requester = null;
        Clear(typeof(MissingTextureReport), "Missing");
        Clear(typeof(NullTextureGuard), "ReportedSites");
        Clear(typeof(NullTextureGuard), "Tally");
        typeof(NullTextureGuard).GetField("sampleAttempts", Statics)?.SetValue(null, 0);
        typeof(NullTextureGuard).GetField("floodSummarized", Statics)?.SetValue(null, false);
        NullTextureGuard.Substituted = 0;
        typeof(FailedAudioClipGuard).GetField("Suppressed", Statics)?.SetValue(null, 0);
        Event.current = new() { type = EventType.Repaint };
        Event.RejectWorkerReads = false;
        BaseContent.BadTex = new();
        LoadedModManager.RunningMods.Clear();
        ModAttribution.Reset();
        Log.Warnings.Clear();
        Log.Messages.Clear();
        Log.Errors.Clear();
        Report.Reset();
        RepeatedErrorFinder.Reset();
        HarmonyLib.Harmony.PatchInfo.Clear();
        LongEventHandler.Pending.Clear();
    }

    private static UObject? ResourcePostfix(string path, Type type, UObject? texture)
    {
        var method = typeof(MissingTextureReport).GetMethod("ResourcePostfix", Statics)!;
        object?[] args = [path, type, texture];
        method.Invoke(null, args);
        return (UObject?)args[^1];
    }

    private static bool Draw(ref Texture? texture)
    {
        object?[] args = [texture];
        var result = Call(typeof(NullTextureGuard), "Prefix", args);
        texture = (Texture?)args[0];
        return result is not false; // Original void prefix always allowed the draw.
    }

    [Test]
    public void RecordingOffDoesNotCollectFailedLookups()
    {
        ContentFinder<Texture2D>.Get("absent");
        Assert.That(MissingTextureReport.DistinctPaths, Is.Zero);
    }

    [Test]
    public void OptionalProbesAreNotReportedAsBrokenContent()
    {
        ImageOptCompatMod.Settings.reportMissingTextures = true;
        ContentFinder<Texture2D>.Get("optional", false);
        Assert.That(MissingTextureReport.DistinctPaths, Is.Zero);
    }

    [Test]
    public void RequiredFailureRetainsDefAndModAttribution()
    {
        ImageOptCompatMod.Settings.reportMissingTextures = true;
        MissingTextureReport.TryInstall(new());
        ContentFinderRequester.requester = new() { defName = "BrokenDef", modContentPack = new() { Name = "Test owner", PackageId = "test.owner" } };
        ContentFinder<Texture2D>.Get("missing-required");
        Assert.That(MissingTextureReport.BuildReport(), Does.Contain("BrokenDef").And.Contain("Test owner (test.owner)"));
    }

    [Test]
    public void DoubleExtensionRescueWorksWithoutRecording()
    {
        var texture = new Texture2D();
        ContentFinder<Texture2D>.Assets["Hologram"] = texture;
        Assert.That(ContentFinder<Texture2D>.Get("Hologram.dds"), Is.SameAs(texture));
        Assert.That(MissingTextureReport.DistinctPaths, Is.Zero);
    }

    [Test]
    public void OriginalBundleTextureWinsOverCorrectedPath()
    {
        var original = new Texture2D();
        ContentFinder<Texture2D>.Bundles["Hologram.dds"] = original;
        ContentFinder<Texture2D>.Assets["Hologram"] = new Texture2D();
        Assert.That(ContentFinder<Texture2D>.Get("Hologram.dds"), Is.SameAs(original));
    }

    [Test]
    public void ResourceOnlyRequestDoesNotFallBackToMods()
    {
        ContentFinder<Texture2D>.Assets["Hologram"] = new Texture2D();
        Assert.That(new ResourcesAPI().Load("Textures/Hologram.dds", typeof(Texture2D)), Is.Null);
    }

    [Test]
    public void TextureReportPreservesApostropheAtEndOfPath()
    {
        ImageOptCompatMod.Settings.reportMissingTextures = true;
        ContentFinder<Texture2D>.Get("missing'");
        Assert.That(MissingTextureReport.RecordedPaths(), Does.Contain("missing'"));
    }

    [Test]
    public void CachedReadbackCallsAreCounted()
    {
        var texture = new Texture2D();
        ImageOpt.Texture2DPatch.NativeTextures.Add(texture.GetInstanceID());
        var first = Texture2DReadPatches.Readable(texture);
        Assert.That(Texture2DReadPatches.Readable(texture), Is.SameAs(first));
        Assert.That(Texture2DReadPatches.Served, Is.EqualTo(2));
    }

    [Test]
    public void InactiveImageOptDoesNotRewritePaths()
    {
        ImageOptCompatMod.ImageOptActive = false;
        ContentFinder<Texture2D>.Assets["Hologram"] = new();
        Assert.That(ContentFinder<Texture2D>.Get("Hologram.dds"), Is.Null);
        Assert.That(ContentFinder<Texture2D>.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void RetryStripsOnlyOneSuffixAndDoesNotRecordItsOwnProbe()
    {
        ImageOptCompatMod.Settings.reportMissingTextures = true;
        ContentFinder<Texture2D>.Assets["Hologram"] = new();
        Assert.That(ContentFinder<Texture2D>.Get("Hologram.dds.dds"), Is.Null);
        Assert.That(ContentFinder<Texture2D>.Requests, Has.Count.EqualTo(2));
        Assert.That(MissingTextureReport.DistinctPaths, Is.EqualTo(1));
    }

    [Test]
    public void WorkerLookupNeverRetriesOrRecords()
    {
        UnityData.IsInMainThread = false;
        ImageOptCompatMod.Settings.reportMissingTextures = true;
        ContentFinder<Texture2D>.Get("Hologram.dds");
        Assert.That(ContentFinder<Texture2D>.Requests, Has.Count.EqualTo(1));
        Assert.That(MissingTextureReport.DistinctPaths, Is.Zero);
    }

    [Test]
    public void DefaultNullDrawIsSkippedWithoutAllocatingATexture()
    {
        Texture? texture = null;
        var before = UObject.NativeAllocations.Count;
        Assert.That(Draw(ref texture), Is.False);
        Assert.That(UObject.NativeAllocations.Count, Is.EqualTo(before));
        Assert.That(texture, Is.Null);
    }

    [Test]
    public void WorkerDrawDoesNotReadImGuiState()
    {
        UnityData.IsInMainThread = false;
        Event.RejectWorkerReads = true;
        Texture? texture = null;
        Assert.That(Draw(ref texture), Is.True);
    }

    [Test]
    public void PlaceholderModeDrawsBadTex()
    {
        ImageOptCompatMod.Settings.nullTextureShowPlaceholder = true;
        Texture? texture = null;
        Assert.That(Draw(ref texture), Is.True);
        Assert.That(texture, Is.SameAs(BaseContent.BadTex));
    }

    [Test]
    public void NullGuardCanBeDisabledWhileInstalled()
    {
        ImageOptCompatMod.Settings.nullTextureGuard = false;
        Texture? texture = null;
        Assert.That(Draw(ref texture), Is.True);
        Assert.That(texture, Is.Null);
        Assert.That(NullTextureGuard.Substituted, Is.Zero);
    }

    [Test]
    public void InvalidGuiContextAndLayoutPassThroughWithoutDiagnostics()
    {
        Texture? texture = null;
        Event.current = null;
        Assert.That(Draw(ref texture), Is.True);
        Event.current = new() { type = EventType.Layout };
        Assert.That(Draw(ref texture), Is.True);
        Assert.That(NullTextureGuard.Substituted, Is.Zero);
    }

    private static void DrawNullFromFixtureMod() =>
        FixtureMod.Window.DrawNull(() => { Texture? texture = null; Draw(ref texture); });

    [Test]
    public void RepeatedCallerExhaustsNormalSamplingBudget()
    {
        OwnTestAssembly("Fixture Mod", "fixture.mod");
        UnityData.DiagnosticChecks = 0;
        UnityData.MeasureDiagnosticChecks = true;
        for (var i = 0; i < 1000; i++) DrawNullFromFixtureMod();
        Assert.That(UnityData.DiagnosticChecks, Is.EqualTo(8));
        Assert.That(Log.Warnings, Has.Count.EqualTo(1));
        // WHO is named matters as much as how often. The first live boot also logged exactly one
        // line, and that line blamed a mod that had nothing to do with it.
        Assert.That(Log.Warnings[0], Does.Contain("drawn from FixtureMod.Window.DrawNull in Fixture Mod (fixture.mod)"));
    }

    /// The live-boot case: the only candidate frame belongs to an assembly several mods list, as
    /// Harmony's does. The warning must say no single mod was found, and name neither claimant.
    [Test]
    public void NullDrawWithNoSingleOwnerNamesNoMod()
    {
        OwnTestAssembly("Harmony", "brrainz.harmony");
        OwnTestAssembly("WanderJoinsPlus", "ogliss.wanderjoinsplus");
        DrawNullFromFixtureMod();
        Assert.That(Log.Warnings, Has.Count.EqualTo(1));
        Assert.That(Log.Warnings[0], Does.Contain("No single mod's code is on the call stack"));
        Assert.That(Log.Warnings[0], Does.Not.Contain("WanderJoinsPlus").And.Not.Contain("brrainz"));
    }

    [Test]
    public void FloodHintSummarizesOnceWhenManyNullDraws()
    {
        ImageOptCompatMod.Settings.nullTextureGuard = true;
        for (int i = 0; i < NullTextureGuard.FloodHintThreshold + 10; i++) { Texture? texture = null; Draw(ref texture); }
        var summarized = (bool)typeof(NullTextureGuard).GetField("floodSummarized", Statics)!.GetValue(null)!;
        Assert.That(summarized, Is.True);
    }

    [Test]
    public void FloodHintStaysUnsetBelowThreshold()
    {
        ImageOptCompatMod.Settings.nullTextureGuard = true;
        for (int i = 0; i < NullTextureGuard.FloodHintThreshold - 1; i++) { Texture? texture = null; Draw(ref texture); }
        var summarized = (bool)typeof(NullTextureGuard).GetField("floodSummarized", Statics)!.GetValue(null)!;
        Assert.That(summarized, Is.False);
    }

    [Test]
    public void DeepDiagnosticCountsEveryDrawButLogsOnlyOnce()
    {
        OwnTestAssembly("Fixture Mod", "fixture.mod");
        ImageOptCompatMod.Settings.nullTextureDeepDiagnostic = true;
        for (var i = 0; i < 20; i++) DrawNullFromFixtureMod();
        var tally = (IDictionary)typeof(NullTextureGuard).GetField("Tally", Statics)!.GetValue(null)!;
        Assert.That(tally.Values.Cast<int>().Sum(), Is.EqualTo(20));
        // The per-mod count is the point of the deep diagnostic, so the row must be the right mod.
        Assert.That(tally["Fixture Mod (fixture.mod) -> FixtureMod.Window.DrawNull"], Is.EqualTo(20));
        Assert.That(Log.Warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void AssemblyAttributionFindsOwningMod()
    {
        OwnTestAssembly("Owned", "owned.mod");
        var owner = ModAttribution.Describe(typeof(RuntimeLogicTests));
        Assert.That(owner, Is.EqualTo("Owned (owned.mod)"));
        Assert.That(ModAttribution.NamesOneMod(owner), Is.True);
        Assert.That(ModAttribution.Describe(null), Is.EqualTo("unknown mod"));
        Assert.That(ModAttribution.NamesOneMod(ModAttribution.Describe(null)), Is.False);
        Assert.That(ModAttribution.NamesOneMod(ModAttribution.CoreLabel), Is.False);
        Assert.That(ModAttribution.NamesOneMod(ModAttribution.Describe(typeof(Assert))), Is.False);

        // A second mod listing the SAME assembly: what Assembly.LoadFrom does for each of the 104
        // mods in the test install that ship their own 0Harmony.dll. It belongs to neither. The
        // old last-wins map returned "WanderJoinsPlus" here.
        OwnTestAssembly("WanderJoinsPlus", "ogliss.wanderjoinsplus");
        ModAttribution.Reset();
        var shared = ModAttribution.Describe(typeof(RuntimeLogicTests));
        Assert.That(shared, Does.Contain("shared library: 2 mods ship a copy"));
        Assert.That(shared, Does.Not.Contain("Owned").And.Not.Contain("WanderJoinsPlus"));
        Assert.That(ModAttribution.NamesOneMod(shared), Is.False);

        // The same situation for the GAME's assembly: 50 mod folders in the test install ship copies
        // of Assembly-CSharp.dll. RimWorld's own code must stay "RimWorld (core)", not a shared library.
        var core = ModAttribution.CoreAssembly;
        try
        {
            ModAttribution.CoreAssembly = typeof(RuntimeLogicTests).Assembly;
            Assert.That(ModAttribution.Describe(typeof(RuntimeLogicTests)), Is.EqualTo(ModAttribution.CoreLabel));
        }
        finally
        {
            ModAttribution.CoreAssembly = core;
        }
    }

    private static Texture2D Native()
    {
        var texture = new Texture2D();
        ImageOpt.Texture2DPatch.NativeTextures.Add(texture.GetInstanceID());
        return texture;
    }

    [Test]
    public void ReadbackToggleAndWorkerDeferralPreserveCacheRecovery()
    {
        var source = Native();
        UnityData.IsInMainThread = false;
        Assert.That(Texture2DReadPatches.Readable(source), Is.Null);
        UnityData.IsInMainThread = true;
        ImageOptCompatMod.Settings.genericPixelReadback = false;
        Assert.That(Texture2DReadPatches.Readable(source), Is.Null);
        ImageOptCompatMod.Settings.genericPixelReadback = true;
        var copy = Texture2DReadPatches.Readable(source);
        Assert.That(copy, Is.Not.Null);
        Assert.That(Texture2DReadPatches.Readable(source), Is.SameAs(copy));
    }

    [Test]
    public void DestroyedCopyIsRecreatedAndTeardownReleasesIt()
    {
        var source = Native();
        var copy = Texture2DReadPatches.Readable(source)!;
        UObject.DestroyImmediate(copy);
        var next = Texture2DReadPatches.Readable(source)!;
        Assert.That(next, Is.Not.SameAs(copy));
        Texture2DReadPatches.ClearCopies();
        Assert.That(next.destroyed, Is.True);
        Assert.That(source.destroyed, Is.False);
    }

    [Test]
    public void ServedCounterResetsOnContentTeardown()
    {
        // The readback counter must zero when cached copies are released on content teardown,
        // so the settings readout never describes copies from a previous content set.
        var source = Native();
        var copy = Texture2DReadPatches.Readable(source)!;
        Assert.That(Texture2DReadPatches.Served, Is.GreaterThan(0));
        Texture2DReadPatches.ClearCopies();
        Assert.That(Texture2DReadPatches.Served, Is.Zero);
        Assert.That(source.destroyed, Is.False);   // the original native is never freed by default
    }

    /// A copy that fails is not retried on every read. A mod reading pixel by pixel, or once per
    /// frame, used to repeat the Blit and the log line each time. Content teardown clears the record,
    /// so a new content set gets a fresh try.
    [Test]
    public void AFailedCopyIsTriedOncePerTextureUntilContentTeardown()
    {
        var source = Native();
        var blits = 0;
        Graphics.OnBlit = () => blits++;
        try
        {
            Texture2D.FailAt = "Blit";
            for (var i = 0; i < 5; i++) Assert.That(Texture2DReadPatches.Readable(source), Is.Null);
            Assert.That(blits, Is.EqualTo(1), "one attempt, not one per read");
            Assert.That(Log.Warnings, Has.Count.EqualTo(1), "and one log line");

            Texture2D.FailAt = null;
            Assert.That(Texture2DReadPatches.Readable(source), Is.Null, "not retried within the same content set");

            Texture2DReadPatches.ClearCopies();
            Assert.That(Texture2DReadPatches.Readable(source), Is.Not.Null, "a new content set gets a fresh try");
            Assert.That(blits, Is.EqualTo(2));
        }
        finally
        {
            Graphics.OnBlit = null;
        }
    }

    [TestCase("Blit"), TestCase("ReadPixels"), TestCase("Apply"), TestCase("Compress"), TestCase("Property")]
    public void CopyFailureReleasesOwnedResources(string stage)
    {
        var source = new Texture2D(8, 8, TextureFormat.DXT5) { ThrowFilter = stage == "Property" };
        var previous = new RenderTexture();
        RenderTexture.active = previous;
        var before = UObject.NativeAllocations.Count;
        Texture2D.FailAt = stage;
        Assert.That(VehicleReadback.ToCpuReadable(source), Is.Null);
        Assert.That(UObject.NativeAllocations.Count, Is.EqualTo(before));
        Assert.That(RenderTexture.Outstanding, Is.Zero);
        Assert.That(RenderTexture.active, Is.SameAs(previous));
        Assert.That(source.destroyed, Is.False);
    }

    [Test]
    public void HolderConversionRetriesEmptyOrWorkerPassAndHandlesReload()
    {
        var pack = new ModContentPack();
        var holder = new ModContentHolder<Texture2D>();
        ModContentPack_AnyContentLoaded_Patch.Postfix(pack, holder);
        var source = new Texture2D();
        holder.contentList["texture"] = source;
        UnityData.IsInMainThread = false;
        ModContentPack_AnyContentLoaded_Patch.Postfix(pack, holder);
        Assert.That(holder.contentList["texture"], Is.SameAs(source));
        UnityData.IsInMainThread = true;
        ModContentPack_AnyContentLoaded_Patch.Postfix(pack, holder);
        var copy = holder.contentList["texture"];
        Assert.That(copy, Is.Not.SameAs(source));
        ModContentPack_AnyContentLoaded_Patch.Postfix(pack, holder);
        Assert.That(holder.contentList["texture"], Is.SameAs(copy));
        var next = new ModContentHolder<Texture2D>();
        next.contentList["texture"] = source;
        ModContentPack_AnyContentLoaded_Patch.Postfix(pack, next);
        Assert.That(next.contentList["texture"], Is.Not.SameAs(source));
    }

    [Test]
    public void EarlyGuardsProtectBothModsAndPreserveVanillaRng()
    {
        Set(typeof(EarlyUiGuards), "vefReady", false);
        Set(typeof(EarlyUiGuards), "worldbuilderReady", false);
        VEF.Sounds.Restart.VFE_Dev_Restart = null;
        Worldbuilder.WorldbuilderMod.settings = null;
        EarlyUiGuards.TryInstall(new());
        Assert.That(EarlyUiGuards.InstalledCount, Is.EqualTo(2));
        Assert.That(EarlyUiGuards.VefDevToolGuard(), Is.False);
        var result = false;
        Assert.That(EarlyUiGuards.WorldbuilderRandGuard(ref result), Is.False);
        Assert.That(result, Is.True);
        VEF.Sounds.Restart.VFE_Dev_Restart = new();
        Worldbuilder.WorldbuilderMod.settings = new();
        Assert.That(EarlyUiGuards.VefDevToolGuard(), Is.True);
        Assert.That(EarlyUiGuards.WorldbuilderRandGuard(ref result), Is.True);
    }

    [Test]
    public void SweepPreservesSourceBackedAndPlainDdsFiles()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        var load = Path.Combine(root, "1.6", "Mods", "Example");
        var textures = Path.Combine(load, "Textures");
        Directory.CreateDirectory(textures);
        foreach (var name in new[] { "orphan.dds.zstd", "keep.dds.zstd", "keep.png", "original.dds" })
            File.WriteAllText(Path.Combine(textures, name), "fixture");
        LoadedModManager.RunningMods.Add(new() { foldersToLoadDescendingOrder = new() { load } });
        OrphanSweep.Run(force: true);
        Assert.That(File.Exists(Path.Combine(textures, "orphan.dds.zstd")), Is.False);
        Assert.That(File.Exists(Path.Combine(textures, "keep.dds.zstd")), Is.True);
        Assert.That(File.Exists(Path.Combine(textures, "original.dds")), Is.True);
        Assert.That(OrphanSweep.LastResult, Is.EqualTo("1 orphan(s) removed, 2 file(s) scanned"));
        ImageOptCompatMod.ImageOptActive = false;
        File.WriteAllText(Path.Combine(textures, "inactive.dds.zstd"), "fixture");
        OrphanSweep.Run(force: true);
        Assert.That(File.Exists(Path.Combine(textures, "inactive.dds.zstd")), Is.True);
        Assert.That(OrphanSweep.LastDeleted, Is.Zero);
    }

    /// A skipped sweep used to report "0 orphan(s) removed, 0 file(s) scanned" on the "Sweep now"
    /// button, which reads as a clean result. It now says it was skipped, and why.
    [Test]
    public void ASkippedSweepSaysSoInsteadOfReportingZeroFiles()
    {
        ImageOptCompatMod.ImageOptActive = false;
        OrphanSweep.Run(force: true);
        Assert.That(OrphanSweep.LastResult, Does.StartWith("skipped").And.Contain("Image Opt is not active"));

        ImageOptCompatMod.ImageOptActive = true;   // active, but no running mod has a texture folder
        OrphanSweep.Run(force: true);
        Assert.That(OrphanSweep.LastResult, Does.StartWith("skipped").And.Contain("no active mod has a texture folder"));
    }

    [Test]
    public void ResolvedTextureDirsFindsOnlyExistingTextureFolders()
    {
        // The parallel folder resolution must still return exactly the folders that exist, and
        // drop the texture folder of a mod that has none. Order is not asserted: dedup sorts by
        // length, so a parallel add order is fine.
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "texdirs", Guid.NewGuid().ToString("N"));
        var withA = Path.Combine(root, "A", "1.6", "Mods", "Alpha", "Textures");
        var withoutB = Path.Combine(root, "B", "1.6", "Mods", "Beta");
        Directory.CreateDirectory(withA);
        Directory.CreateDirectory(withoutB);
        LoadedModManager.RunningMods.Add(new() { foldersToLoadDescendingOrder = new() { Path.Combine(root, "A", "1.6", "Mods", "Alpha") } });
        LoadedModManager.RunningMods.Add(new() { foldersToLoadDescendingOrder = new() { Path.Combine(root, "B", "1.6", "Mods", "Beta") } });

        var dirs = (List<string>?)Call(typeof(OrphanSweep), "ResolvedTextureDirs");
        Assert.That(dirs, Is.Not.Null);
        Assert.That(dirs, Has.Count.EqualTo(1));
        Assert.That(dirs![0], Does.EndWith("Textures"));
    }

    /// Tests a reentrant insert during conversion. The actual boot failure needs no second
    /// writer: Mono invalidates Keys enumeration on our own value overwrite. The separate
    /// MonoTests probe verifies that runtime behavior; this net9 test checks an extra edge case.
    [Test]
    public void HolderConversionSurvivesAReentrantInsert()
    {
        var holder = new ModContentHolder<Texture2D>();
        holder.contentList["a"] = new Texture2D();
        holder.contentList["b"] = new Texture2D();

        var inserted = 0;
        Graphics.OnBlit = () =>
        {
            // One insert only: a second would also fire while draining the snapshot's entries.
            if (inserted++ == 0) holder.contentList["reentrant_arrival"] = new Texture2D();
        };

        try
        {
            UnityData.IsInMainThread = true;
            Assert.DoesNotThrow(
                () => VehicleReadback.ConvertHolder(holder, "smashphil.vehicleframework"),
                "ConvertHolder must tolerate another mod inserting into contentList mid-conversion. "
              + "If this throws, the .ToList() snapshot has been removed again - see the comment on "
              + "that line before 'optimising' it away.");
        }
        finally
        {
            Graphics.OnBlit = null;
        }

        // And the late arrival must still be there afterwards: we snapshot the keys, we do not
        // snapshot and then write back a stale dictionary.
        Assert.That(holder.contentList.ContainsKey("reentrant_arrival"), Is.True);
    }

    [Test]
    public void HolderConversionReplacesEveryNonNullSourceOnce()
    {
        // Every non-null source becomes a CPU-readable copy; null entries stay untouched.
        // This net9 host cannot prove Mono's dictionary iteration rules (see MonoTests).
        var pack = new ModContentPack();
        var holder = new ModContentHolder<Texture2D>();
        var s1 = new Texture2D();
        var s2 = new Texture2D();
        holder.contentList["a"] = s1;
        holder.contentList["b"] = s2;
        holder.contentList["c"] = null!;

        var before = VehicleReadback.Replaced;
        UnityData.IsInMainThread = true;
        VehicleReadback.ConvertHolder(holder, "smashphil.vehicleframework");

        Assert.That(VehicleReadback.Replaced, Is.EqualTo(before + 2));
        Assert.That(holder.contentList["a"], Is.Not.SameAs(s1));
        Assert.That(holder.contentList["b"], Is.Not.SameAs(s2));
        Assert.That(holder.contentList["c"], Is.Null);
    }

    // ---- failed-audio crash guard behaviour ---------------------------------------------
    // A decode-failed clip stays live (passes Verse's `audioClip != null` guard) but reading its
    // extern clip.length dereferences absent sample data -> access violation -> hard crash. The
    // filter marks failed clips unusable and the constructor guard substitutes shared silence.
    // Only Failed is touched; Unloaded/Loading are normal for a clip that streams.

    private static void GuardInvoke(string path, AudioClip clip) =>
        typeof(FailedAudioClipGuard).GetMethod("FilterFailed", Statics)!.Invoke(null, new object?[] { path, clip });

    private static int SuppressedCount() =>
        (int)typeof(FailedAudioClipGuard).GetField("Suppressed", Statics)!.GetValue(null)!;

    [Test]
    public void FailedClipIsReportedMissing()
    {
        ImageOptCompatMod.Settings.guardFailedAudioClips = true;
        GuardInvoke("Sound/Broken", new AudioClip { loadState = AudioDataLoadState.Failed });
        Assert.That(SuppressedCount(), Is.EqualTo(1));
    }

    [Test]
    public void LoaderFailureIsCaughtEvenWhenUnitySaysLoaded()
    {
        var clip = new AudioClip { loadState = AudioDataLoadState.Loaded };
        RuntimeAudioClipLoader.Manager.States[clip] = AudioDataLoadState.Failed;
        object?[] args = ["loader-failed", clip];
        Call(typeof(FailedAudioClipGuard), "FilterFailed", args);
        Assert.That(args[1], Is.Null);
    }

    [Test]
    public void ConstructorGuardReusesAValidSilentClip()
    {
        var failed = new AudioClip { loadState = AudioDataLoadState.Failed };
        object?[] first = [failed];
        Call(typeof(FailedAudioClipGuard), "Prefix", first);
        object?[] second = [failed];
        Call(typeof(FailedAudioClipGuard), "Prefix", second);
        Assert.That(first[0], Is.Not.Null.And.Not.SameAs(failed));
        Assert.That(first[0], Is.SameAs(second[0]));
        Assert.That(((AudioClip)first[0]!).loadState, Is.EqualTo(AudioDataLoadState.Loaded));
    }

    [Test]
    public void UnloadedAndLoadingClipsAreKept()
    {
        ImageOptCompatMod.Settings.guardFailedAudioClips = true;
        foreach (var state in new[] { AudioDataLoadState.Unloaded, AudioDataLoadState.Loading })
        {
            GuardInvoke("Sound/Good", new AudioClip { loadState = state });
            Assert.That(SuppressedCount(), Is.Zero);
        }
    }

    [Test]
    public void FailedClipGuardIsToggleable()
    {
        ImageOptCompatMod.Settings.guardFailedAudioClips = false;
        GuardInvoke("Sound/Broken", new AudioClip { loadState = AudioDataLoadState.Failed });
        Assert.That(SuppressedCount(), Is.Zero);
    }

    /// Was "returns a non-empty owner", which the live-boot misattribution passed. No def asked
    /// here, so the report must read the stack and name the mod code that asked - not the logging
    /// and lookup frames in between, and not this test.
    [Test]
    public void CodeDrivenMissingTextureNamesTheCallingMod()
    {
        OwnTestAssembly("Fixture Mod", "fixture.mod");
        ImageOptCompatMod.Settings.reportMissingTextures = true;
        MissingTextureReport.TryInstall(new());
        FixtureMod.Loader.LoadMissingIcon();
        Assert.That(MissingTextureReport.BuildReport(),
            Does.Contain("Fixture Mod (fixture.mod) at FixtureMod.Loader.LoadMissingIcon"));
    }

    /// The default level: problems reach the startup dialog exactly once, handled problems reach
    /// the log only, and what fixes did stays out of the log.
    [Test]
    public void ImportantShowsEachProblemOnceAndKeepsNoticesInTheLog()
    {
        Report.Write(ReportKind.Problem, "guard missing");
        Report.Write(ReportKind.Problem, "guard missing");
        Report.Write(ReportKind.Notice, "untested version");
        Report.Write(ReportKind.Info, "5 textures converted");

        Assert.That(Report.TakePending(), Is.EqualTo(new[] { "guard missing" }));
        Assert.That(Log.Warnings, Has.Count.EqualTo(3));
        Assert.That(Log.Warnings, Has.All.StartWith(ModInfo.Tag));
        Assert.That(Log.Messages, Is.Empty);
    }

    [Test]
    public void QuietLogsOnlyGameBreakingProblemsAndShowsNothing()
    {
        ImageOptCompatMod.Settings.reportLevel = ReportLevel.Quiet;
        Report.Write(ReportKind.Breaking, "black screen risk");
        Report.Write(ReportKind.Problem, "guard missing");
        Report.Write(ReportKind.Notice, "untested version");

        Assert.That(Log.Errors, Has.Count.EqualTo(1));
        Assert.That(Log.Warnings, Is.Empty);
        Assert.That(Report.TakePending(), Is.Empty);
    }

    [Test]
    public void EverythingAlsoLogsWhatEachFixDid()
    {
        ImageOptCompatMod.Settings.reportLevel = ReportLevel.Everything;
        Report.Write(ReportKind.Info, "5 textures converted");
        Assert.That(Log.Messages, Has.Count.EqualTo(1));
        Assert.That(Report.TakePending(), Is.Empty);
    }

    /// Once the main menu is up, problems go straight to the in-game display, still once each.
    [Test]
    public void AfterLoadingProblemsGoToTheLiveDisplay()
    {
        var shown = new List<string>();
        Report.LiveDisplay = shown.Add;
        Report.Write(ReportKind.Problem, "late problem");
        Report.Write(ReportKind.Problem, "late problem");

        Assert.That(shown, Is.EqualTo(new[] { "late problem" }));
        Assert.That(Report.TakePending(), Is.Empty);
        Assert.That(Report.ShownCount, Is.EqualTo(1));
    }

    /// The live display draws UI. If it throws, the problem must still be in the log, and whatever
    /// reported it, which can be a pixel read inside another mod's call, must not get the exception.
    [Test]
    public void AFailingLiveDisplayDoesNotThrowIntoTheReporter()
    {
        Report.LiveDisplay = _ => throw new InvalidOperationException("no message drawer yet");

        Assert.DoesNotThrow(() => Report.Write(ReportKind.Problem, "late problem"));
        Assert.That(Log.Warnings.Single(), Does.Contain("late problem"));
        Assert.That(Report.ShownCount, Is.EqualTo(1));
    }

    private static Exception CrashInFixtureMod()
    {
        try { FixtureMod.Loader.Crash(null); }
        catch (NullReferenceException e) { return e; }
        throw new InvalidOperationException("the fixture did not throw");
    }

    /// Boot 2 logged the same NullReferenceException 4,478 times with no stack trace. The finder
    /// reads each exception object itself, so it names the mod even then. Every repeat is a new
    /// exception, as it is in the game.
    [Test]
    public void ARepeatingErrorIsTracedToTheModThatThrowsIt()
    {
        OwnTestAssembly("Fixture Mod", "fixture.mod");
        for (var i = 1; i < RepeatedErrorFinder.NoticeAt; i++) RepeatedErrorFinder.Observe(CrashInFixtureMod());
        Assert.That(Log.Warnings, Is.Empty, "quiet below the notice threshold");

        RepeatedErrorFinder.Observe(CrashInFixtureMod());
        Assert.That(Log.Warnings, Has.Count.EqualTo(1));
        Assert.That(Log.Warnings[0], Does.Contain("Fixture Mod (fixture.mod) has thrown the same NullReferenceException 100 times")
                                  .And.Contain("FixtureMod.Loader.Crash"));
        Assert.That(Report.TakePending(), Is.Empty, "a notice stays in the log");

        for (var i = RepeatedErrorFinder.NoticeAt; i < RepeatedErrorFinder.ProblemAt; i++) RepeatedErrorFinder.Observe(CrashInFixtureMod());
        Assert.That(Report.TakePending().Single(), Does.Contain("1000 times"), "shown on screen once at 1,000");
        Assert.That(RepeatedErrorFinder.Snapshot().Single().Count, Is.EqualTo(RepeatedErrorFinder.ProblemAt));
    }

    /// Unity's formatter reads an exception's text three times, and an outer exception's text
    /// includes its inner one's. However often it is read, one exception is one occurrence.
    [Test]
    public void OneExceptionIsCountedOnceHoweverOftenItIsRead()
    {
        OwnTestAssembly("Fixture Mod", "fixture.mod");
        var error = CrashInFixtureMod();
        for (var i = 0; i < 3; i++) RepeatedErrorFinder.Observe(error);
        RepeatedErrorFinder.Observe(new InvalidOperationException("wrapped", error));

        var row = RepeatedErrorFinder.Snapshot().Single();
        Assert.That(row.Count, Is.EqualTo(1));
        Assert.That(row.Exception, Is.EqualTo("NullReferenceException"), "counted as its innermost cause");
    }

    /// Codex's review 4: two mods failing in one shared helper were one error, blamed on whichever
    /// came first. ModA fails once and ModB a hundred times, so the notice must name ModB.
    [Test]
    public void TwoModsFailingInOneSharedHelperAreTwoErrors()
    {
        var modA = FixtureCaller("ModA");
        var modB = FixtureCaller("ModB");
        RepeatedErrorFinder.Observe(ExceptionFrom(modA));
        for (var i = 0; i < RepeatedErrorFinder.NoticeAt; i++) RepeatedErrorFinder.Observe(ExceptionFrom(modB));

        var rows = RepeatedErrorFinder.Snapshot();
        Assert.That(rows.Select(r => (r.Owner, r.Count)), Is.EqualTo(new[] { ("ModB (modb)", 100), ("ModA (moda)", 1) }));
        Assert.That(Log.Warnings.Single(), Does.Contain("ModB (modb) has thrown the same InvalidOperationException 100 times")
                                         .And.Contain("at ModB.Caller.Run, thrown in FixtureGame.Helper.Fail"));
    }

    /// A long load can log many one-off errors before play starts. When the list is full the
    /// rarest gives way, so a flood that starts later is still counted and named.
    [Test]
    public void OneOffErrorsGiveWayToALaterFlood()
    {
        for (var i = 0; i < RepeatedErrorFinder.MaxTracked; i++)
        {
            var site = "Loader.Step" + i;
            RepeatedErrorFinder.Record((typeof(InvalidOperationException), site, site),
                new RepeatedErrorFinder.Entry { Exception = "InvalidOperationException", Site = site, ThrownIn = site, Owner = "Some Mod (some.mod)" });
        }

        OwnTestAssembly("Fixture Mod", "fixture.mod");
        for (var i = 0; i < RepeatedErrorFinder.NoticeAt; i++) RepeatedErrorFinder.Observe(CrashInFixtureMod());

        var rows = RepeatedErrorFinder.Snapshot();
        Assert.That(rows, Has.Count.EqualTo(RepeatedErrorFinder.MaxTracked));
        Assert.That(rows[0].Count, Is.EqualTo(RepeatedErrorFinder.NoticeAt));
        Assert.That(Log.Warnings.Single(), Does.Contain("Fixture Mod (fixture.mod) has thrown"));
    }

    /// Once every tracked error had repeated, a newcomer was always the rarest. Two errors taking
    /// turns, as one mod failing in both its tick and its draw does, then evicted each other on
    /// every repeat and neither was ever named. A newcomer now starts from the weight it replaced.
    [Test]
    public void TwoErrorsTakingTurnsAreBothNamedWhenTheListIsFull()
    {
        for (var i = 0; i < RepeatedErrorFinder.MaxTracked; i++)
        {
            for (var repeat = 0; repeat < 2; repeat++) RecordAt("Loader.Step" + i, "Some Mod (some.mod)");
        }

        for (var i = 0; i < RepeatedErrorFinder.NoticeAt; i++)
        {
            RecordAt("Comp.Tick", "Tick Mod (tick.mod)");
            RecordAt("Comp.Draw", "Draw Mod (draw.mod)");
        }

        var rows = RepeatedErrorFinder.Snapshot();
        Assert.That(rows.Take(2).Select(r => (r.Site, r.Count)), Is.EquivalentTo(new[]
        {
            ("Comp.Tick", RepeatedErrorFinder.NoticeAt), ("Comp.Draw", RepeatedErrorFinder.NoticeAt),
        }));
        Assert.That(Log.Warnings, Has.Count.EqualTo(2));
        Assert.That(Log.Warnings, Has.Some.Contains("Tick Mod (tick.mod) has thrown the same"));
        Assert.That(Log.Warnings, Has.Some.Contains("Draw Mod (draw.mod) has thrown the same"));
    }

    /// The newcomer inherits the evicted entry's weight to hold its place, never its count: an
    /// error that replaces a flood must not be reported as having repeated.
    [Test]
    public void AnErrorThatReplacesAFloodIsNotReportedAsRepeating()
    {
        for (var i = 0; i < RepeatedErrorFinder.MaxTracked; i++)
        {
            for (var repeat = 0; repeat < RepeatedErrorFinder.NoticeAt; repeat++) RecordAt("Flood" + i, "Some Mod (some.mod)");
        }

        var notices = Log.Warnings.Count;
        RecordAt("Once", "Other Mod (other.mod)");

        Assert.That(Log.Warnings, Has.Count.EqualTo(notices), "no notice for an error seen once");
        Assert.That(RepeatedErrorFinder.Snapshot().Single(r => r.Site == "Once").Count, Is.EqualTo(1));
    }

    private static void RecordAt(string site, string owner) =>
        RepeatedErrorFinder.Record((typeof(InvalidOperationException), site, site),
            new RepeatedErrorFinder.Entry { Exception = "InvalidOperationException", Site = site, ThrownIn = site, Owner = owner });

    /// With no mod's own code on the stack, a patch is how a mod's change reached the failing game
    /// method. The notice lists who patched the methods the error passed through, and says it is
    /// that, not who is at fault.
    [Test]
    public void AnErrorWithNoModOnItsStackListsThePatchesItPassedThrough()
    {
        var patch = FixtureCaller("Patcher");
        var crash = typeof(FixtureMod.Loader).GetMethod(nameof(FixtureMod.Loader.Crash))!;
        HarmonyLib.Harmony.PatchInfo[crash] = new HarmonyLib.Patches
        {
            Prefixes = new(new List<HarmonyLib.Patch> { new() { owner = "patcher", PatchMethod = patch } }),
        };

        for (var i = 0; i < RepeatedErrorFinder.NoticeAt; i++) RepeatedErrorFinder.Observe(CrashInFixtureMod());

        Assert.That(Log.Warnings.Single(), Does.Contain("No single mod's code is on its stack")
                                         .And.Contain("carry patches from Patcher (patcher)"));
    }

    /// The log is not safe off the main thread, so a threshold crossed there waits for the next
    /// repeat on the main thread instead of being lost.
    [Test]
    public void AThresholdCrossedOnAWorkerIsReportedByTheNextMainThreadRepeat()
    {
        OwnTestAssembly("Fixture Mod", "fixture.mod");
        UnityData.IsInMainThread = false;
        for (var i = 0; i < RepeatedErrorFinder.NoticeAt; i++) RepeatedErrorFinder.Observe(CrashInFixtureMod());
        Assert.That(Log.Warnings, Is.Empty, "nothing logged off the main thread");

        UnityData.IsInMainThread = true;
        RepeatedErrorFinder.Observe(CrashInFixtureMod());
        Assert.That(Log.Warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void TheRepeatedErrorFinderCanBeSwitchedOff()
    {
        ImageOptCompatMod.Settings.findRepeatedErrors = false;
        Call(typeof(RepeatedErrorFinder), "Postfix", CrashInFixtureMod());
        Assert.That(RepeatedErrorFinder.Snapshot(), Is.Empty);

        ImageOptCompatMod.Settings.findRepeatedErrors = true;
        Call(typeof(RepeatedErrorFinder), "Postfix", CrashInFixtureMod());
        Assert.That(RepeatedErrorFinder.Snapshot(), Has.Count.EqualTo(1));
    }

    /// Environment.StackTrace asks for the current stack with no exception. That is not an error.
    [Test]
    public void AStackTraceWithNoExceptionIsNotAnError()
    {
        Call(typeof(RepeatedErrorFinder), "Postfix", new object?[] { null });
        Assert.That(RepeatedErrorFinder.Snapshot(), Is.Empty);
    }

    /// A mod of its own, in an assembly of its own, whose one method calls the shared game helper.
    private static MethodInfo FixtureCaller(string name)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule(name).DefineType(name + ".Caller", TypeAttributes.Public);
        var run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        run.SetImplementationFlags(MethodImplAttributes.NoInlining);
        var il = run.GetILGenerator();
        il.Emit(OpCodes.Call, typeof(FixtureGame.Helper).GetMethod(nameof(FixtureGame.Helper.Fail))!);
        il.Emit(OpCodes.Ret);
        var method = type.CreateType()!.GetMethod("Run")!;

        var pack = new ModContentPack { Name = name, PackageId = name.ToLowerInvariant() };
        pack.assemblies.loadedAssemblies.Add(method.DeclaringType!.Assembly);
        LoadedModManager.RunningMods.Add(pack);
        ModAttribution.Reset();
        return method;
    }

    private static Exception ExceptionFrom(MethodInfo method)
    {
        try { method.Invoke(null, null); }
        catch (TargetInvocationException e) { return e.InnerException!; }
        throw new InvalidOperationException("the fixture did not throw");
    }

    private static void OwnTestAssembly(string name, string packageId)
    {
        var pack = new ModContentPack { Name = name, PackageId = packageId };
        pack.assemblies.loadedAssemblies.Add(typeof(RuntimeLogicTests).Assembly);
        LoadedModManager.RunningMods.Add(pack);
    }
}
