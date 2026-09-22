using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// Pins the API that FailedAudioClipGuard binds to.
///
/// That guard exists to stop a hard crash: a sound file the engine cannot decode reaches
/// ResolvedGrain_Clip's constructor, which reads AudioClip.length. That property is extern, so the
/// failure is a native access violation and no catch block anywhere can intercept it. Measured on
/// this install: two consecutive runs died on exactly that stack.
///
/// A guard against a CRASH is the worst possible place for a silent no-op. Harmony binds by name,
/// so a renamed method or argument would leave the patch reporting itself installed while doing
/// nothing, and the next boot would crash again with no clue why. These read the installed game
/// and Unity assemblies as metadata and fail loudly if any of those names move.
///
/// Skips when RimWorld is not installed, so the repo still builds anywhere. Set RIMWORLD_MANAGED
/// to point at a Managed directory elsewhere.
[TestFixture]
public class AudioApiContractTests
{
    private MetadataLoadContext? _context;
    private string? _managed;

    private static readonly string[] CandidateManagedDirs =
    {
        @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed",
        @"C:\Program Files\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed",
    };

    [OneTimeSetUp]
    public void Load()
    {
        _managed = Environment.GetEnvironmentVariable("RIMWORLD_MANAGED");
        if (string.IsNullOrWhiteSpace(_managed) || !Directory.Exists(_managed))
            _managed = CandidateManagedDirs.FirstOrDefault(Directory.Exists);

        if (_managed == null) return;

        // Only the game's own assemblies. The Managed folder ships Unity's Mono mscorlib, so the
        // set is self-contained; adding this host's framework directory supplies a SECOND mscorlib
        // and MetadataLoadContext refuses that.
        _context = new MetadataLoadContext(
            new PathAssemblyResolver(Directory.GetFiles(_managed, "*.dll")), "mscorlib");
    }

    [OneTimeTearDown]
    public void Unload() => _context?.Dispose();

    private Type GetType(string assemblyFile, string typeName)
    {
        if (_managed == null || _context == null)
            Assert.Ignore("RimWorld is not installed here. Set RIMWORLD_MANAGED to run the API contract tests.");

        var asm = _context!.LoadFromAssemblyPath(Path.Combine(_managed!, assemblyFile));
        var type = asm.GetType(typeName);
        Assert.That(type, Is.Not.Null, $"{typeName} is missing from {assemblyFile}. The game's API has moved.");
        return type!;
    }

    private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.Static
                                           | BindingFlags.DeclaredOnly;

    // ---- the patch target -------------------------------------------------------------------

    /// FailedAudioClipGuard postfixes this exact overload. ContentFinder is generic, and the guard
    /// patches the AudioClip instantiation of it.
    [Test]
    public void ContentFinderHasTheGetOverloadTheGuardPatches()
    {
        var finder = GetType("Assembly-CSharp.dll", "Verse.ContentFinder`1");

        var get = finder.GetMethods(AllDeclared)
            .Where(m => string.Equals(m.Name, "Get", StringComparison.Ordinal))
            .FirstOrDefault(m => m.GetParameters().Length == 2
                              && m.GetParameters()[0].ParameterType.FullName == "System.String");

        Assert.That(get, Is.Not.Null,
            "Verse.ContentFinder<T>.Get(string, bool) is gone. FailedAudioClipGuard would patch nothing.");
    }

    /// Harmony binds injected parameters BY NAME. The postfix declares itemPath, so the original
    /// must still call it that or the guard binds nothing and the crash returns.
    [Test]
    public void TheGetOverloadStillNamesItsFirstArgumentItemPath()
    {
        var finder = GetType("Assembly-CSharp.dll", "Verse.ContentFinder`1");

        var get = finder.GetMethods(AllDeclared)
            .Where(m => string.Equals(m.Name, "Get", StringComparison.Ordinal))
            .First(m => m.GetParameters().Length == 2);

        Assert.That(get.GetParameters()[0].Name, Is.EqualTo("itemPath"),
            "ContentFinder.Get renamed its path argument; FailedAudioClipGuard binds 'itemPath'.");
    }

    // ---- the state the guard reads -----------------------------------------------------------

    /// The whole fix rests on this property. A file-exists or header check would pass these clips:
    /// the measured ones have valid RIFF/WAVE headers and still fail to decode. Only loadState
    /// distinguishes them.
    [Test]
    public void AudioClipStillExposesLoadState()
    {
        var clip = GetType("UnityEngine.AudioModule.dll", "UnityEngine.AudioClip");
        var loadState = clip.GetProperty("loadState", BindingFlags.Public | BindingFlags.Instance);

        Assert.That(loadState, Is.Not.Null,
            "UnityEngine.AudioClip.loadState is gone; the failed-audio guard has nothing to test.");
        Assert.That(loadState!.PropertyType.Name, Is.EqualTo("AudioDataLoadState"));
    }

    [Test]
    public void AudioDataLoadStateStillHasFailed()
    {
        var state = GetType("UnityEngine.AudioModule.dll", "UnityEngine.AudioDataLoadState");

        Assert.That(state.IsEnum, Is.True);
        Assert.That(Enum.GetNames(state), Does.Contain("Failed"),
            "AudioDataLoadState.Failed is gone; the guard cannot recognise a decode failure.");
    }

    /// Unloaded and Loading are normal states for a streaming clip. The guard treats ONLY Failed
    /// as unusable, so those two must keep existing or the distinction is meaningless.
    [TestCase("Unloaded")]
    [TestCase("Loading")]
    [TestCase("Loaded")]
    public void AudioDataLoadStateKeepsTheStatesTheGuardMustNotTouch(string name)
    {
        var state = GetType("UnityEngine.AudioModule.dll", "UnityEngine.AudioDataLoadState");
        Assert.That(Enum.GetNames(state), Does.Contain(name));
    }

    // ---- the crash path itself ----------------------------------------------------------------

    /// This is the constructor that crashed. It reads clip.length, and length is extern, which is
    /// why the failure is a native access violation rather than a catchable exception.
    [Test]
    public void ResolvedGrainClipStillTakesAnAudioClip()
    {
        var grain = GetType("Assembly-CSharp.dll", "Verse.Sound.ResolvedGrain_Clip");

        var ctor = grain.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 1
                              && c.GetParameters()[0].ParameterType.Name == "AudioClip");

        Assert.That(ctor, Is.Not.Null,
            "ResolvedGrain_Clip(AudioClip) is gone; re-check where the crash path now runs.");
    }

    [Test]
    public void AudioClipLengthIsStillExtern()
    {
        var clip = GetType("UnityEngine.AudioModule.dll", "UnityEngine.AudioClip");
        var length = clip.GetProperty("length", BindingFlags.Public | BindingFlags.Instance);

        Assert.That(length, Is.Not.Null, "UnityEngine.AudioClip.length is gone.");

        var getter = length!.GetGetMethod();
        Assert.That(getter, Is.Not.Null);
        Assert.That((getter!.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0
                 || getter.Attributes.HasFlag(MethodAttributes.PinvokeImpl), Is.True,
            "AudioClip.length is no longer native. If it became managed it would throw catchably, "
          + "and the reasoning behind the failed-audio guard should be revisited.");
    }

    /// RimWorld's own null guard, which fired 625 times in the crash run. The fix depends on it:
    /// the guard reports a decode-failed clip as missing so THIS check handles it.
    [Test]
    public void AudioGrainClipStillGuardsAgainstAMissingClip()
    {
        var grain = GetType("Assembly-CSharp.dll", "Verse.Sound.AudioGrain_Clip");

        Assert.That(grain.GetMethods(AllDeclared).Any(m =>
                string.Equals(m.Name, "GetResolvedGrains", StringComparison.Ordinal)), Is.True,
            "AudioGrain_Clip.GetResolvedGrains is gone; the vanilla guard the fix relies on may have moved.");
    }
}
