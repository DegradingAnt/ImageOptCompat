// Test doubles only. No game, Unity GPU, Steam, or real Harmony execution.
using System.Reflection;
namespace HarmonyLib {
 [AttributeUsage(AttributeTargets.Class)]
 public sealed class HarmonyPatch(Type type, string method, params Type[] parameters) : Attribute {}
 public sealed class HarmonyMethod { public HarmonyMethod(Type type, string method) {} public HarmonyMethod(MethodInfo? method) {} }
 public sealed class Harmony {
  public void Patch(MethodBase method, HarmonyMethod? prefix=null, HarmonyMethod? postfix=null) {}
  public void Unpatch(MethodBase original, MethodInfo patch) {}
  // No detours exist on the test host, so every frame is its own original. The real mapping is
  // exercised on the game's Mono by ImageOptCompat.MonoTests.
  public static MethodBase? GetOriginalMethodFromStackframe(System.Diagnostics.StackFrame frame) => frame.GetMethod();
 }
 public static class AccessTools {
  public static bool HideImageOpt;
  public static Type? TypeByName(string name) => HideImageOpt && name == "ImageOpt.Texture2DPatch" ? null : typeof(AccessTools).Assembly.GetType(name);
  public static FieldInfo? Field(Type type, string name) => type.GetField(name, BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance);
  public static MethodInfo? Method(Type type, string name) => type.GetMethod(name, BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance);
  public static MethodInfo? Method(Type type, string name, Type[] args) => type.GetMethod(name,args);
  public static ConstructorInfo? Constructor(Type type, Type[] args) => type.GetConstructor(args);
 }
}
namespace UnityEngine {
 public class Object {
  static int next;
  readonly int id = ++next;
  public bool destroyed;
  public int GetInstanceID() => id;
  public static readonly HashSet<int> NativeAllocations = new();
  public static int DestroyCalls;
  public static void Destroy(Object value) => DestroyImmediate(value);
  public static void DestroyImmediate(Object value) { value.destroyed = true; NativeAllocations.Remove(value.id); DestroyCalls++; }
  public static bool operator ==(Object? a, Object? b) => ReferenceEquals(a,b) || ((a is null || a.destroyed) && (b is null || b.destroyed));
  public static bool operator !=(Object? a, Object? b) => !(a == b);
  public override bool Equals(object? obj) => ReferenceEquals(this,obj);
  public override int GetHashCode() => id;
 }
 public enum TextureFormat { Alpha8, RGBA32, DXT1, DXT1Crunched, DXT5, DXT5Crunched, BC4, BC5, BC6H, BC7 }
 public enum FilterMode { Point, Bilinear, Trilinear }
 public enum TextureWrapMode { Repeat, Clamp }
 public enum RenderTextureFormat { Default }
 public enum RenderTextureReadWrite { Default }
 // Audio stand-ins for the failed-audio crash guard. A decode-failed clip stays live but Unity
 // dereferences its absent sample data on clip.length (extern) -> access violation -> hard crash.
 public enum AudioDataLoadState { Unloaded, Loaded, Loading, Failed }
 public sealed class AudioClip : Object {
  public AudioDataLoadState loadState;
  public string name = "fixture";
  public static AudioClip Create(string name, int samples, int channels, int frequency, bool stream) => new() { name=name, loadState=AudioDataLoadState.Loaded };
 }
 public class ResourcesAPI {
  public static Func<string,Type,Object?,Object?>? Postprocess;
  public static readonly Dictionary<string,Object> Assets = new();
  public Object? Load(string path, Type systemTypeInstance) {
   Assets.TryGetValue(path, out var result);
   return Postprocess == null ? result : Postprocess(path,systemTypeInstance,result);
  }
 }
 public readonly record struct Color(float r, float g, float b, float a);
 public readonly record struct Rect(float x, float y, float width, float height);
 public class Texture : Object {}
 public static class GUI { public static void DrawTexture(Rect position, Texture image) {} }
 public enum EventType { Layout, Repaint, MouseDown }
 public sealed class Event {
  private static Event? value;
  public static bool RejectWorkerReads;
  public static Event? current {
   get { if(RejectWorkerReads && !Verse.UnityData.IsInMainThread) throw new InvalidOperationException("IMGUI accessed off main thread"); return value; }
   set => Event.value=value;
  }
  public EventType type;
 }
 public sealed class Texture2D : Texture {
  public static string? FailAt;
  public static string LastRead = "";
  public static int CompressCalls;
  public bool ThrowName, ThrowFilter;
  string textureName = "fixture";
  public string name { get { if (ThrowName) throw new InvalidOperationException("name getter"); return textureName; } set => textureName=value; }
  public FilterMode filterMode { get { if (ThrowFilter) throw new InvalidOperationException("filter getter"); return FilterMode.Point; } set {} }
  public TextureWrapMode wrapMode { get; set; }
  public int anisoLevel { get; set; }
  public int width { get; }
  public int height { get; }
  public int mipmapCount { get; }
  public TextureFormat format { get; }
  public Texture2D(int w=8, int h=8, TextureFormat f=TextureFormat.RGBA32, bool mipChain=false) {
   width=w; height=h; format=f; mipmapCount=mipChain?4:1; NativeAllocations.Add(GetInstanceID());
  }
  public void SetPixel(int x,int y,Color value) {}
  public void ReadPixels(Rect area,int x,int y) { Fail("ReadPixels"); }
  public void Apply(bool updateMipmaps,bool makeNoLongerReadable) { Fail("Apply"); }
  public void Compress(bool highQuality) { CompressCalls++; Fail("Compress"); }
  static void Fail(string stage) { if (FailAt == stage) throw new InvalidOperationException("injected "+stage); }
  Color[] Read(string which) { LastRead=which; return new[]{new Color(1,0,0,1)}; }
  Color Pixel(string which) { LastRead=which; return new Color(1,0,0,1); }
  public Color[] GetPixels() => Read("all");
  public Color[] GetPixels(int miplevel) => Read("mip:"+miplevel);
  public Color[] GetPixels(int x,int y,int blockWidth,int blockHeight) => Read($"block:{x},{y},{blockWidth},{blockHeight}");
  public Color GetPixel(int x,int y) => Pixel($"pixel:{x},{y}");
  public Color GetPixel(int x,int y,int mipLevel) => Pixel($"pixel:{x},{y},{mipLevel}");
  public Color GetPixelBilinear(float u,float v) => Pixel($"bilinear:{u},{v}");
  public Color GetPixelBilinear(float u,float v,int mipLevel) => Pixel($"bilinear:{u},{v},{mipLevel}");
 }
 public sealed class RenderTexture : Object {
  public static RenderTexture? active;
  public static int Outstanding;
  public int width=8,height=8;
  public static RenderTexture GetTemporary(int w,int h,int depth,RenderTextureFormat format,RenderTextureReadWrite rw) { Outstanding++; return new(){width=w,height=h}; }
  public static void ReleaseTemporary(RenderTexture tex) { Outstanding--; }
 }
 public static class Graphics {
  // OnBlit fires once per texture conversion, from INSIDE ConvertHolder's loop. That is the exact
  // point a reentrant caller could insert into the collection. This tests reentrancy only; the
  // actual boot failure was Mono invalidating enumeration on our own existing-key overwrite.
  public static Action? OnBlit;
  public static void Blit(Texture2D src,RenderTexture rt) { OnBlit?.Invoke(); if(Texture2D.FailAt=="Blit") throw new InvalidOperationException("injected Blit"); }
 }
}
namespace Verse {
 public static class UnityData {
  private static bool main=true;
  public static bool MeasureDiagnosticChecks;
  public static int DiagnosticChecks;
  public static bool IsInMainThread {
   [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
   get {
    // Test-only observation of actual diagnostic entries; production has no instrumentation.
    if (MeasureDiagnosticChecks && new System.Diagnostics.StackTrace(false).GetFrames()
        .Any(frame => frame.GetMethod()?.Name == "ReportCallSite")) DiagnosticChecks++;
    return main;
   }
   set => main=value;
  }
 }
 public sealed class ModContentPack { public string PackageId="smashphil.vehicleframework"; public string Name="Fixture mod"; public string RootDir=""; public ModAssemblies assemblies=new(); public List<string> foldersToLoadDescendingOrder=new(); public bool AnyContentLoaded() => true; }
 public sealed class ModAssemblies { public List<Assembly> loadedAssemblies=new(); }
 public class Def { public string defName="FixtureDef"; public ModContentPack? modContentPack; }
 public static class ContentFinderRequester { public static Def? requester; }
 public static class BaseContent { public static UnityEngine.Texture2D? BadTex=new(); }
 public static class PlayDataLoader { public static void ClearAllPlayData() {} }
 public static class LongEventHandler { public static void QueueLongEvent(Action action,string? text,bool async,object? handler) { Pending.Add(action); } public static List<Action> Pending=new(); }
 public static class ContentFinder<T> where T:class {
  public static readonly Dictionary<string,T> Assets=new();
  public static readonly Dictionary<string,T> Bundles=new();
  public static readonly List<string> Requests=new();
  public static T? TryFindAssetInModBundles(string path) => Bundles.TryGetValue(path,out var result) ? result : null;
  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  public static T? Get(string itemPath,bool reportFailure=true) {
   Requests.Add(itemPath);
   if (!UnityData.IsInMainThread) return null;
   Assets.TryGetValue(itemPath,out var result);
   if(result != null) return result;
   result = new UnityEngine.ResourcesAPI().Load((typeof(T)==typeof(UnityEngine.Texture2D) ? "Textures/" : "Sounds/")+itemPath,typeof(T)) as T;
   result ??= TryFindAssetInModBundles(itemPath);
   if(result == null && reportFailure) Log.Error("Could not load " + typeof(T).Name + " at '" + itemPath + "'" + (ContentFinderRequester.requester == null ? "" : " for def '"+ContentFinderRequester.requester.defName+"'") + " in any active mod or in base resources.");
   return result;
  }
 }
 public sealed class ModContentHolder<T> { public Dictionary<string,T> contentList=new(); }
 public static class LoadedModManager { public static List<ModContentPack> RunningMods=new(); }
 public static class GenFilePaths { public const string TexturesFolder="Textures"; }
 public static class Log {
  public static Action<string>? ErrorObserver;
  public static List<string> Errors=new();
  public static void Error(string text) { Errors.Add(text); ErrorObserver?.Invoke(text); }
  public static List<string> Warnings=new();
  public static void Warning(string message) => Warnings.Add(message);
  public static List<string> Messages=new();
  public static void Message(string message) => Messages.Add(message);
 }
}
namespace RuntimeAudioClipLoader {
 public static class Manager {
  public static readonly Dictionary<UnityEngine.AudioClip,UnityEngine.AudioDataLoadState> States = new();
  public static UnityEngine.AudioDataLoadState GetAudioClipLoadState(UnityEngine.AudioClip clip) => States.TryGetValue(clip,out var state) ? state : UnityEngine.AudioDataLoadState.Unloaded;
 }
}
namespace Verse.Sound {
 public class ResolvedGrain_Clip { public ResolvedGrain_Clip(UnityEngine.AudioClip clip) {} }
}
namespace ImageOptCompat {
 public sealed class Settings { public bool findRepeatedErrors=true,vehicleReadback=true,recompressCopies=true,genericPixelReadback=true,nullTextureGuard=true,fixDoubleExtensionPaths=true,guardFailedAudioClips=true; public ReportLevel reportLevel = ReportLevel.Important; public bool destroyOriginalTexture,nullTextureShowPlaceholder,nullTextureDeepDiagnostic,reportMissingTextures; }
 public static class ImageOptCompatMod { public static Settings Settings=new(); public static bool ImageOptActive=true; }
}
namespace ImageOpt { public static class Texture2DPatch { public static HashSet<int> NativeTextures=new(); } }
namespace VEF.Sounds {
 public static class Restart { public static object? VFE_Dev_Restart; }
 public static class VanillaExpandedFramework_DebugWindowsOpener_DevToolStarterOnGUI_Patch { public static void Prefix() {} }
}
namespace Worldbuilder {
 public static class WorldbuilderMod { public static object? settings; }
 public static class Rand_EnsureStateStackEmpty_Patch { public static bool Prefix() => true; }
}

namespace UnityEngine {
 // Unity formats every logged exception here; the repeated-error finder postfixes it.
 public static class StackTraceUtility {
  internal static void ExtractStringFromExceptionInternal(object exceptiono, out string message, out string stackTrace) { message = ""; stackTrace = ""; }
 }
}
