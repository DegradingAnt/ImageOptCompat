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
        ContentFinder<Texture2D>.Postprocess = Postfix;
        ContentFinderRequester.requester = null;
        Clear(typeof(MissingTextureReport), "Missing");
        Clear(typeof(NullTextureGuard), "ReportedSites");
        Clear(typeof(NullTextureGuard), "Tally");
        typeof(NullTextureGuard).GetField("sampleAttempts", Statics)?.SetValue(null, 0);
        NullTextureGuard.Substituted = 0;
        Event.current = new() { type = EventType.Repaint };
        BaseContent.BadTex = new();
        LoadedModManager.RunningMods.Clear();
        ModAttribution.Reset();
        Log.Warnings.Clear();
        LongEventHandler.Pending.Clear();
    }

    private static Texture2D? Postfix(string path, bool report, Texture2D? texture)
    {
        var method = typeof(MissingTextureReport).GetMethod("Postfix", Statics)!;
        // Also supports the old two-argument signature for before/after regression evidence.
        object?[] args = method.GetParameters().Length == 2 ? [path, texture] : [path, report, texture];
        method.Invoke(null, args);
        return (Texture2D?)args[^1];
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
}
