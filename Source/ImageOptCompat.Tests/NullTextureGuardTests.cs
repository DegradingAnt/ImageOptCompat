using System.Linq;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

[TestFixture]
public class NullTextureGuardTests
{
    /// Stands in for UnityEngine.Texture, so the selection rule can be exercised with no Unity
    /// runtime - the same approach VehicleReadbackTests and OrphanPathsTests take.
    private sealed class FakeTexture;

    private sealed class UnrelatedType;

    /// Shaped like the real UnityEngine.GUI: a short forwarder, a longer terminal, and a method
    /// that is not a draw call at all.
    private static class FakeGui
    {
        public static void DrawTexture(int position, FakeTexture image) { }
        public static void DrawTexture(int position, FakeTexture image, int scaleMode) { }
        public static void DrawTextureWithTexCoords(int position, FakeTexture image, int texCoords) { }
        public static void DrawMesh(int position, FakeTexture image) { }
    }

    /// The regression this fixture exists for. The NAME matches, so only the parameter rule can
    /// reject it - Harmony binds injected parameters BY NAME, and an overload whose texture is not
    /// called "image" would bind nothing while still reporting itself installed.
    private static class FakeGuiWithRenamedArgument
    {
        public static void DrawTexture(int position, FakeTexture img) { }
    }

    /// Right name on the parameter, wrong type.
    private static class FakeGuiWithWrongTextureType
    {
        public static void DrawTexture(int position, UnrelatedType image) { }
    }

    /// Instance methods are not patch targets; Unity's draw calls are all static.
    private static class FakeGuiWithInstanceMethodHolder
    {
        public sealed class Instance
        {
            public void DrawTexture(int position, FakeTexture image) { }
        }
    }

    private static string[] SelectedFrom(System.Type source) => NullTextureGuard
        .SelectTargets(source, typeof(FakeTexture))
        .Select(m => m.Name)
        .ToArray();

    private static string[] Selected() => SelectedFrom(typeof(FakeGui));

    // ---- terminal-only selection -----------------------------------------------------------

    /// Unity's public overloads are pure forwarders into one terminal that holds the null check.
    /// Patching every link would run the prefix three or four times per draw for no benefit, so
    /// exactly ONE DrawTexture must be selected.
    [Test]
    public void SelectsExactlyOneDrawTextureOverload() =>
        Assert.That(Selected().Count(n => n == "DrawTexture"), Is.EqualTo(1));

    /// And it must be the terminal - the one with the most parameters, which every forwarder
    /// eventually calls. Picking a forwarder would miss any caller that skips it.
    [Test]
    public void SelectsTheLongestDrawTextureOverload()
    {
        var chosen = NullTextureGuard.SelectTargets(typeof(FakeGui), typeof(FakeTexture))
            .Single(m => m.Name == "DrawTexture");

        Assert.That(chosen.GetParameters(), Has.Length.EqualTo(3));
    }

    [Test]
    public void SelectsDrawTextureWithTexCoords() =>
        Assert.That(Selected(), Does.Contain("DrawTextureWithTexCoords"));

    [Test]
    public void RejectsMethodsThatAreNotDrawCalls() =>
        Assert.That(Selected(), Does.Not.Contain("DrawMesh"));

    // ---- the name-binding rule itself ------------------------------------------------------

    /// Selecting this would patch the method while binding nothing: InstalledCount would report
    /// success and not one null would ever be intercepted.
    [Test]
    public void RejectsAnOverloadWhoseTextureArgumentIsRenamed() =>
        Assert.That(SelectedFrom(typeof(FakeGuiWithRenamedArgument)), Is.Empty);

    [Test]
    public void RejectsTheRightParameterNameOnTheWrongType() =>
        Assert.That(SelectedFrom(typeof(FakeGuiWithWrongTextureType)), Is.Empty);

    [Test]
    public void RejectsInstanceMethods() =>
        Assert.That(SelectedFrom(typeof(FakeGuiWithInstanceMethodHolder.Instance)), Is.Empty);

    // ---- pure predicates -------------------------------------------------------------------

    [TestCase("DrawTexture")]
    [TestCase("DrawTextureWithTexCoords")]
    public void DrawMethodNamesAreRecognized(string name) =>
        Assert.That(NullTextureGuard.IsDrawMethodName(name), Is.True);

    [TestCase("DrawTextureFitted")]     // RimWorld's own wrapper - it calls GUI.DrawTexture itself
    [TestCase("drawtexture")]           // matching must stay case-sensitive and exact
    [TestCase("DrawTextureWithTexCoordsExtra")]
    [TestCase("")]
    public void OtherNamesAreNot(string name) =>
        Assert.That(NullTextureGuard.IsDrawMethodName(name), Is.False);

    [TestCase("UnityEngine.GUI")]
    [TestCase("HarmonyLib.MethodPatcher")]
    [TestCase("ImageOptCompat.NullTextureGuard")]
    [TestCase("System.Collections.Generic.List`1")]
    public void PlumbingFramesAreSkipped(string type) =>
        Assert.That(NullTextureGuard.IsPlumbingFrame(type), Is.True);

    /// These are the frames worth naming: whichever mod actually asked to draw the missing texture.
    [TestCase("Verse.Widgets")]
    [TestCase("RimWorld.MainTabWindow_Inspect")]
    [TestCase("UsefulMarks.MarkOverlay")]
    [TestCase("SmashPhil.VehicleFramework.VehicleTurret")]
    public void ModFramesAreReported(string type) =>
        Assert.That(NullTextureGuard.IsPlumbingFrame(type), Is.False);

    /// "UnityEngineExtras" is not UnityEngine - the prefix test must not swallow a mod whose
    /// namespace merely starts with the same letters.
    [Test]
    public void ASimilarlyNamedModNamespaceIsNotSkipped() =>
        Assert.That(NullTextureGuard.IsPlumbingFrame("UnityEngineExtras.Draw"), Is.False);
}
