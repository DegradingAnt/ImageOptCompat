using System.Collections;
using System.Reflection;
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

    [Test]
    public void RepeatedCallerExhaustsNormalSamplingBudget()
    {
        UnityData.DiagnosticChecks = 0;
        UnityData.MeasureDiagnosticChecks = true;
        for (var i = 0; i < 1000; i++) { Texture? texture = null; Draw(ref texture); }
        Assert.That(UnityData.DiagnosticChecks, Is.EqualTo(8));
        Assert.That(Log.Warnings, Has.Count.EqualTo(1));
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
        ImageOptCompatMod.Settings.nullTextureDeepDiagnostic = true;
        for (var i = 0; i < 20; i++) { Texture? texture = null; Draw(ref texture); }
        var tally = (IDictionary)typeof(NullTextureGuard).GetField("Tally", Statics)!.GetValue(null)!;
        Assert.That(tally.Values.Cast<int>().Sum(), Is.EqualTo(20));
        Assert.That(Log.Warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void AssemblyAttributionFindsOwningMod()
    {
        var pack = new ModContentPack { Name = "Owned", PackageId = "owned.mod" };
        pack.assemblies.loadedAssemblies.Add(typeof(RuntimeLogicTests).Assembly);
        LoadedModManager.RunningMods.Add(pack);
        Assert.That(ModAttribution.Describe(typeof(RuntimeLogicTests)), Is.EqualTo("Owned (owned.mod)"));
        Assert.That(ModAttribution.Describe(null), Is.EqualTo("unknown mod"));
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
        ImageOptCompatMod.ImageOptActive = false;
        File.WriteAllText(Path.Combine(textures, "inactive.dds.zstd"), "fixture");
        OrphanSweep.Run(force: true);
        Assert.That(File.Exists(Path.Combine(textures, "inactive.dds.zstd")), Is.True);
        Assert.That(OrphanSweep.LastDeleted, Is.Zero);
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

    [Test]
    public void DescribeCallerReturnsANonEmptyOwner()
    {
        Assert.That(ModAttribution.DescribeCaller(), Is.Not.Empty);
    }
}
