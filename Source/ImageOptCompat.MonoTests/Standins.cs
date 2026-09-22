// Native Unity operations are stand-ins. Harmony and Mono are REAL; production patch code is linked.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    public class Object { public string name = "fixture"; }
    public sealed class Texture2D : Object { }
    public enum AudioDataLoadState { Unloaded, Loaded, Loading, Failed }
    public sealed class AudioClip : Object
    {
        public AudioDataLoadState loadState = AudioDataLoadState.Loaded;
        public int LengthReads;
        private float seconds = 1;
        public float length
        {
            get
            {
                LengthReads++;
                if (loadState == AudioDataLoadState.Failed
                    || RuntimeAudioClipLoader.Manager.GetAudioClipLoadState(this) == AudioDataLoadState.Failed)
                    throw new InvalidOperationException("Attempt to read a failed clip's native length");
                return seconds;
            }
        }
        public static AudioClip Create(string name, int samples, int channels, int frequency, bool stream) => new() { name = name, seconds = (float)samples / frequency };
    }
    public static class GUI
    {
        public static int Draws;
        // Patched by the attribution check exactly as production patches the real one.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void DrawTexture(Texture2D? image) { Draws++; }
    }
    public class ResourcesAPI
    {
        public static readonly ResourcesAPI Instance = new();
        public static readonly Dictionary<string, Object> Assets = new();
        // Deliberately no NoInlining here: this has the same small virtual shape as Unity's API.
        protected internal virtual Object? Load(string path, Type systemTypeInstance) =>
            Assets.TryGetValue(path, out var value) && systemTypeInstance.IsInstanceOfType(value) ? value : null;
    }
}
namespace RuntimeAudioClipLoader
{
    public static class Manager
    {
        public static readonly Dictionary<UnityEngine.AudioClip, UnityEngine.AudioDataLoadState> States = new();
        public static UnityEngine.AudioDataLoadState GetAudioClipLoadState(UnityEngine.AudioClip clip) =>
            States.TryGetValue(clip, out var value) ? value : UnityEngine.AudioDataLoadState.Unloaded;
    }
}
namespace Verse
{
    public static class UnityData { public static bool IsInMainThread = true; }
    public sealed class ModAssemblies { public List<System.Reflection.Assembly> loadedAssemblies = new(); }
    public sealed class ModContentPack { public string Name = "mod"; public string PackageId = "test.mod"; public ModAssemblies assemblies = new(); }
    public static class LoadedModManager { public static readonly List<ModContentPack> RunningMods = new(); }
    public class Def { public string defName = "FixtureDef"; public ModContentPack? modContentPack; }
    public static class ContentFinderRequester { public static Def? requester; }
    public static class ContentFinder<T> where T : class
    {
        public static readonly Dictionary<string, T> Assets = new();
        public static readonly Dictionary<string, T> Bundles = new();
        public static readonly List<string> Requests = new();
        public static T? TryFindAssetInModBundles(string path) => Bundles.TryGetValue(path, out var value) ? value : null;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T? Get(string itemPath, bool reportFailure = true)
        {
            Requests.Add(itemPath);
            if (!UnityData.IsInMainThread) return null;
            if (Assets.TryGetValue(itemPath, out var value)) return value;
            var prefix = typeof(T) == typeof(UnityEngine.Texture2D) ? "Textures/" : "Sounds/";
            value = ResourcesAPIValue(prefix + itemPath) ?? TryFindAssetInModBundles(itemPath);
            if (value == null && reportFailure)
                Log.Error("Could not load " + typeof(T).Name + " at '" + itemPath + "'"
                    + (ContentFinderRequester.requester == null ? "" : " for def '" + ContentFinderRequester.requester.defName + "'")
                    + " in any active mod or in base resources.");
            return value;
        }
        private static T? ResourcesAPIValue(string path) => UnityEngine.ResourcesAPI.Instance.Load(path, typeof(T)) as T;
    }
    public static class Log
    {
        public static readonly List<string> Errors = new();
        public static readonly List<string> Warnings = new();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Error(string text) { Errors.Add(text); }
        public static void Warning(string text) { Warnings.Add(text); Console.WriteLine(text); }
        public static void Message(string text) { Console.WriteLine(text); }
    }
}
namespace Verse.Sound
{
    public sealed class ResolvedGrain_Clip
    {
        public UnityEngine.AudioClip clip;
        public float duration;
        public ResolvedGrain_Clip(UnityEngine.AudioClip clip) { this.clip = clip; duration = clip.length; }
    }
}
namespace ImageOptCompat
{
    public sealed class Settings
    {
        public bool guardFailedAudioClips = true, fixDoubleExtensionPaths = true, reportMissingTextures = true, findRepeatedErrors = true;
        public ReportLevel reportLevel = ReportLevel.Important;
    }
    public static class ImageOptCompatMod
    {
        public static Settings Settings = new();
        public static bool ImageOptActive = true;
    }
}
// Plays the part of another mod's code, calling through the same kind of patched methods as in
// the live boot. NoInlining keeps each as its own frame; attribution reads frames.
namespace FixtureMod
{
    public static class Window
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void DoContents() => UnityEngine.GUI.DrawTexture(null);
    }
    public static class Loader
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void LoadIcon() { _ = Verse.ContentFinder<UnityEngine.Texture2D>.Get("FixtureMod/MissingIcon"); }
    }

    /// A patch this mod puts on a game method, for the finder to name when an error passes through it.
    public static class Patches
    {
        public static void ToInt32Prefix() { }
    }
}
