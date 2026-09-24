using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

/// Every patch in this mod is bound BY NAME: Harmony matches an injected parameter to the original
/// method's parameter of the same name, and a name that does not exist binds nothing and fails
/// SILENTLY - the patch reports installed and does nothing. That has already happened once here:
/// Unity names the mip argument `miplevel` on GetPixels but `mipLevel` on GetPixel and
/// GetPixelBilinear, and getting it wrong produced a patch that looked fine in the build log.
///
/// These tests read the real game assemblies as METADATA (never executing them, and never loading
/// net481 into this net9.0 host) and assert the names our patches depend on still exist.
///
/// They SKIP rather than fail when RimWorld is not installed, so the repo keeps its property of
/// building and testing on any machine. Point RIMWORLD_MANAGED at a Managed directory to run them
/// somewhere the default Steam path does not apply.
[TestFixture]
public class UnityApiContractTests
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

        // ONLY the game's own assemblies. The Managed directory ships Unity's Mono mscorlib, so the
        // set is self-contained; adding this host's framework directory too would supply a SECOND
        // mscorlib and MetadataLoadContext refuses that ("has already been loaded").
        // Core assembly is named explicitly: the host's default is System.Private.CoreLib, which
        // does not exist in a Mono profile.
        var assemblies = Directory.GetFiles(_managed, "*.dll");

        _context = new MetadataLoadContext(new PathAssemblyResolver(assemblies), "mscorlib");
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

    private static IEnumerable<MethodInfo> Overloads(Type type, string name) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                      | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => string.Equals(m.Name, name, StringComparison.Ordinal));

    // ---- NullTextureGuard's contract -------------------------------------------------------

    /// NullTextureGuard.SelectTargets only patches overloads with a parameter named "image". If
    /// Unity ever renames it, the guard silently stops intercepting and the log flood returns.
    [TestCase("DrawTexture")]
    [TestCase("DrawTextureWithTexCoords")]
    public void EveryGuiDrawOverloadNamesItsTextureImage(string method)
    {
        var gui = GetType("UnityEngine.IMGUIModule.dll", "UnityEngine.GUI");
        var overloads = Overloads(gui, method).ToArray();

        Assert.That(overloads, Is.Not.Empty, $"UnityEngine.GUI.{method} no longer exists.");

        foreach (var m in overloads)
        {
            var names = m.GetParameters().Select(p => p.Name).ToArray();
            Assert.That(names, Does.Contain("image"),
                $"GUI.{method}({string.Join(", ", names)}) has no parameter named 'image'. "
              + "Harmony binds by name, so NullTextureGuard would skip this overload.");
        }
    }

    /// NullTextureGuard patches only the TERMINAL overload - the longest one, which every public
    /// forwarder funnels into and which alone holds Unity's null check. These arities pin that
    /// shape: if Unity adds a longer overload or moves the check, this fails loudly rather than
    /// letting the guard patch a forwarder and quietly miss the draws that skip it.
    [TestCase("DrawTexture", 12)]
    [TestCase("DrawTextureWithTexCoords", 4)]
    public void TheTerminalDrawOverloadHasTheExpectedArity(string method, int expected)
    {
        var gui = GetType("UnityEngine.IMGUIModule.dll", "UnityEngine.GUI");
        var longest = Overloads(gui, method).Max(m => m.GetParameters().Length);

        Assert.That(longest, Is.EqualTo(expected),
            $"UnityEngine.GUI.{method}'s longest overload now takes {longest} parameters, not {expected}. "
          + "Re-check which overload holds the null check before trusting NullTextureGuard.");
    }

    // ---- MainMenuStatus's contract -----------------------------------------------------------

    /// The status line postfixes the main-menu screen. It used to postfix
    /// VersionControl.DrawInfoInCorner, the Harmony mod's hook, until the release boot found
    /// No Version In Pause Menu removing every mod's postfix there. MainMenuOnGUI calls
    /// DrawInfoInCorner first, so the line still draws at the same moment.
    [Test]
    public void TheMainMenuScreenIsWhereTheStatusLineHooks()
    {
        var drawer = GetType("Assembly-CSharp.dll", "RimWorld.MainMenuDrawer");
        var method = drawer.GetMethod("MainMenuOnGUI", BindingFlags.Public | BindingFlags.Static);

        Assert.That(method, Is.Not.Null, "RimWorld.MainMenuDrawer.MainMenuOnGUI is gone; the status line has no hook.");
        Assert.That(method!.GetParameters(), Is.Empty);
    }

    // ---- RepeatedErrorFinder's contract -------------------------------------------------------

    /// The finder postfixes the one method every exception passes through on its way to text: Unity's
    /// formatter and a RimWorld "Log.Error(... + ex)" both read ex.StackTrace, which calls it. It is
    /// internal to the game's own Mono corlib, so only this metadata can say it is still there, still
    /// static, and still names its exception "e", the name the postfix binds.
    [Test]
    public void TheRuntimeTurnsExceptionsIntoTextThroughTheMethodTheFinderPatches()
    {
        var environment = GetType("mscorlib.dll", "System.Environment");
        var method = Overloads(environment, "GetStackTrace").SingleOrDefault(m =>
            m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Exception", "System.Boolean" }));

        Assert.That(method, Is.Not.Null, "System.Environment.GetStackTrace(Exception, bool) is gone from the game's corlib.");
        Assert.That(method!.IsStatic, Is.True);
        Assert.That(method.GetParameters().Select(p => p.Name), Is.EqualTo(new[] { "e", "needFileInfo" }));

        var exception = GetType("mscorlib.dll", "System.Exception");
        Assert.That(exception.GetProperty("StackTrace"), Is.Not.Null);
        Assert.That(Overloads(exception, "GetStackTrace").Any(m => m.GetParameters().Length == 1), Is.True,
            "Exception.GetStackTrace(bool), the step between ex.StackTrace and the runtime method, is gone.");

        var mod = _context!.LoadFromAssemblyPath(typeof(WavHeaderFix).Assembly.Location);
        var postfix = mod.GetType("ImageOptCompat.RepeatedErrorFinder")!.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!;
        AudioApiContractTests.AssertBindsTo(postfix, method);
    }

    // ---- Texture2DReadPatches' contract ----------------------------------------------------

    /// The inconsistency that already caught us once: Unity spells it `miplevel` here...
    [Test]
    public void GetPixelsSpellsTheMipArgumentLowercase()
    {
        var tex = GetType("UnityEngine.CoreModule.dll", "UnityEngine.Texture2D");
        var single = Overloads(tex, "GetPixels").Single(m => m.GetParameters().Length == 1);

        Assert.That(single.GetParameters()[0].Name, Is.EqualTo("miplevel"),
            "Texture2D.GetPixels(int) renamed its argument. Texture2DReadPatches binds 'miplevel'.");
    }

    /// ...and `mipLevel` here. Same concept, different casing, in the same class.
    [TestCase("GetPixel", 3)]
    [TestCase("GetPixelBilinear", 3)]
    public void GetPixelAndBilinearSpellTheMipArgumentCamelCase(string method, int paramCount)
    {
        var tex = GetType("UnityEngine.CoreModule.dll", "UnityEngine.Texture2D");
        var overload = Overloads(tex, method).SingleOrDefault(m => m.GetParameters().Length == paramCount);

        Assert.That(overload, Is.Not.Null, $"Texture2D.{method} with {paramCount} parameters is gone.");
        Assert.That(overload!.GetParameters()[paramCount - 1].Name, Is.EqualTo("mipLevel"),
            $"Texture2D.{method} renamed its mip argument. Texture2DReadPatches binds 'mipLevel'.");
    }

    /// The five-argument GetPixels is extern (native). A prefix on it cannot read pixels back, so
    /// Texture2DReadPatches deliberately patches the four-argument managed one instead. If that
    /// ever changes, the reasoning behind the patch set changes with it.
    [Test]
    public void TheFiveArgumentGetPixelsIsStillNative()
    {
        var tex = GetType("UnityEngine.CoreModule.dll", "UnityEngine.Texture2D");
        var five = Overloads(tex, "GetPixels").SingleOrDefault(m => m.GetParameters().Length == 5);

        if (five == null) Assert.Ignore("Texture2D.GetPixels no longer has a five-argument overload.");
        Assert.That(five!.Attributes.HasFlag(MethodAttributes.PinvokeImpl)
                 || (five.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0,
            Is.True, "The five-argument GetPixels is no longer native; revisit which overloads are patched.");
    }

    // ---- RimWorld's own contract -----------------------------------------------------------

    /// NullTextureGuard substitutes this when it intercepts a null draw.
    [Test]
    public void BaseContentStillExposesBadTex()
    {
        var baseContent = GetType("Assembly-CSharp.dll", "Verse.BaseContent");
        var field = baseContent.GetField("BadTex", BindingFlags.Public | BindingFlags.Static);

        Assert.That(field, Is.Not.Null, "Verse.BaseContent.BadTex is gone; NullTextureGuard has nothing to substitute.");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("UnityEngine.Texture2D"));
    }

    /// Texture2DReadPatches hooks this to release cached copies on content teardown.
    [Test]
    public void PlayDataLoaderStillExposesClearAllPlayData()
    {
        var loader = GetType("Assembly-CSharp.dll", "Verse.PlayDataLoader");

        Assert.That(Overloads(loader, "ClearAllPlayData"), Is.Not.Empty,
            "Verse.PlayDataLoader.ClearAllPlayData is gone; cached texture copies would never be released.");
    }
}
