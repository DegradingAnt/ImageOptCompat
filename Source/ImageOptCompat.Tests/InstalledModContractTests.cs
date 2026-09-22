using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace ImageOptCompat.Tests;

// Metadata only. Resolving a method is not proof that Harmony detoured it in a live game.
[TestFixture]
public sealed class InstalledModContractTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly string Managed = Environment.GetEnvironmentVariable("RIMWORLD_MANAGED")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed";
    private static readonly string Workshop = Environment.GetEnvironmentVariable("RIMWORLD_WORKSHOP")
        ?? @"C:\Program Files (x86)\Steam\steamapps\workshop\content\294100";

    private static void Inspect(string relative, Action<Assembly> inspect)
    {
        var path = Path.Combine(Workshop, relative);
        if (!Directory.Exists(Managed) || !File.Exists(path)) Assert.Ignore("Installed mod unavailable: " + path);
        var paths = Directory.GetFiles(Managed, "*.dll").Append(path);
        using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
        inspect(context.LoadFromAssemblyPath(path));
    }

    [Test]
    public void FasterGameLoadingCoordinatorAndAllFiveSettingsResolve() => Inspect(
        "3797541348/Assemblies/FasterGameLoading.dll", assembly =>
        {
            Assert.That(assembly.GetType("FasterGameLoading.ImageOptEarlyLoadCoordinator"), Is.Not.Null);
            var settings = assembly.GetType("FasterGameLoading.FasterGameLoadingSettings")!;
            Assert.That(settings.GetField("earlyModContentLoading", All)?.FieldType.FullName, Is.EqualTo("System.Boolean"));
            foreach (var name in new[] { "EnableMultiThreading", "XPathCaching", "DelayGraphicLoading", "StaticAtlasesBaking" })
            {
                var property = settings.GetProperty(name, All);
                Assert.That(property?.PropertyType.FullName, Is.EqualTo("System.Boolean"), name);
                Assert.That(property?.GetMethod?.IsStatic, Is.True, name);
            }
        });

    [Test]
    public void ImageOptNativeTextureRegistryAndLoadCoordinatorFieldResolve() => Inspect(
        "3543873568/Assemblies/ImageOpt.dll", assembly =>
        {
            var registry = assembly.GetType("ImageOpt.Texture2DPatch")!.GetField("NativeTextures", All)!;
            Assert.That(registry.IsStatic, Is.True);
            Assert.That(registry.FieldType.GetGenericTypeDefinition().FullName, Is.EqualTo("System.Collections.Generic.HashSet`1"));
            Assert.That(registry.FieldType.GetGenericArguments()[0].FullName, Is.EqualTo("System.Int32"));
            Assert.That(assembly.GetType("ImageOpt.TextureLoadPatch")!.GetField("Started", All)?.FieldType.FullName,
                Is.EqualTo("System.Boolean"));
        });

    [Test]
    public void VefGuardMatchesInstalledPrefixAndKeybinding() => Inspect(
        "2023507013/1.6/Assemblies/VEF.dll", assembly =>
        {
            var prefix = assembly.GetType("VEF.Sounds.VanillaExpandedFramework_DebugWindowsOpener_DevToolStarterOnGUI_Patch")!.GetMethod("Prefix", All)!;
            Assert.That(prefix.IsStatic, Is.True);
            Assert.That(prefix.ReturnType.FullName, Is.EqualTo("System.Void"));
            Assert.That(prefix.GetParameters(), Is.Empty);
            var field = assembly.GetType("VEF.Sounds.Restart")!.GetField("VFE_Dev_Restart", All)!;
            Assert.That(field.IsStatic, Is.True);
            Assert.That(field.FieldType.FullName, Is.EqualTo("Verse.KeyBindingDef"));
        });

    [Test]
    public void WorldbuilderGuardMatchesInstalledPrefixAndSettings() => Inspect(
        "3522102833/1.6/Assemblies/Worldbuilder.dll", assembly =>
        {
            var prefix = assembly.GetType("Worldbuilder.Rand_EnsureStateStackEmpty_Patch")!.GetMethod("Prefix", All)!;
            Assert.That(prefix.IsStatic, Is.True);
            Assert.That(prefix.ReturnType.FullName, Is.EqualTo("System.Boolean"));
            Assert.That(prefix.GetParameters(), Is.Empty);
            Assert.That(assembly.GetType("Worldbuilder.WorldbuilderMod")!.GetField("settings", All)?.IsStatic, Is.True);
        });

    [Test]
    public void MissingTextureHooksUseNonGenericMethodsWithExplicitType()
    {
        if (!Directory.Exists(Managed)) Assert.Ignore("Installed RimWorld unavailable.");
        using var context = new MetadataLoadContext(new PathAssemblyResolver(Directory.GetFiles(Managed, "*.dll")), "mscorlib");
        var game = context.LoadFromAssemblyPath(Path.Combine(Managed, "Assembly-CSharp.dll"));
        var unity = context.LoadFromAssemblyPath(Path.Combine(Managed, "UnityEngine.CoreModule.dll"));
        var target = unity.GetType("UnityEngine.ResourcesAPI")!.GetMethod("Load", All)!;
        Assert.That(target.DeclaringType!.IsGenericType, Is.False);
        Assert.That(target.GetMethodBody(), Is.Not.Null);
        Assert.That(target.GetParameters().Select(p => p.Name), Is.EqualTo(new[] { "path", "systemTypeInstance" }));
        Assert.That(target.GetParameters()[1].ParameterType.FullName, Is.EqualTo("System.Type"));
        Assert.That(target.ReturnType.FullName, Is.EqualTo("UnityEngine.Object"));
        var error = game.GetType("Verse.Log")!.GetMethods(All).Single(m => m.Name == "Error");
        Assert.That(error.GetParameters().Select(p => p.Name), Is.EqualTo(new[] { "text" }));
    }
}
