using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

/// GENERIC pixel-readback fix. Works for any mod reading any Image Opt texture - no per-mod list.
///
/// Image Opt creates textures with Texture2D.CreateExternalTexture over GPU memory its Rust side
/// owns. Every public Texture2D pixel READ begins `if (!isReadable) throw`, and isReadable is itself
/// native, so on these textures a read either throws or returns an empty CPU buffer.
///
/// Image Opt patches only GetPixels32(int). Its patches for the others sit inside a block comment
/// (Texture2DPatch.cs:39-81) citing "Unity's native functions cannot be patched". That is not true
/// of these entry points: they are MANAGED bodies (UnityEngine.CoreModule, RimWorld 1.6.4871). We
/// intercept BEFORE the isReadable throw and answer from a CPU-readable copy.
///
/// COVERAGE GAP, by necessity: GetPixels(int,int,int,int,int) - the 5-argument form every other
/// GetPixels overload forwards to - is `extern` and cannot be patched. Its three managed callers are
/// patched, so every standard call is caught; only a DIRECT call to the 5-argument form escapes.
/// GetPixels32 is skipped on purpose: Image Opt handles GetPixels32(int), and GetPixels32() forwards
/// to it.
///
/// Composes with VehicleReadback: textures it already swapped for readable copies are not in Image
/// Opt's native set, so they pass straight through here.
internal static class Texture2DReadPatches
{
    private static HashSet<int>? nativeIds;
    private static bool resolved;
    private static ConditionalWeakTable<Texture2D, Texture2D> Copies = new();
    // Unity native storage outlives managed GC. Own copies until content teardown even
    // when a weak source key is collected; never take ownership of Image Opt's sources.
    private static readonly HashSet<Texture2D> OwnedCopies = new();

    internal static int Served { get; private set; }

    /// Image Opt's own record of every texture it created natively (TextureLoadPatch.cs:81) - more
    /// reliable than Unity's native isReadable, which is exactly the value in question.
    private static HashSet<int>? NativeIds()
    {
        if (resolved) return nativeIds;
        resolved = true;
        var type = AccessTools.TypeByName("ImageOpt.Texture2DPatch");
        nativeIds = type == null ? null : AccessTools.Field(type, "NativeTextures")?.GetValue(null) as HashSet<int>;
        // Fail open, but NOT silently: a renamed field in a newer Image Opt would otherwise switch
        // this whole fix off with no sign anything had changed.
        if (nativeIds == null && ImageOptCompatMod.ImageOptActive)
            Report.Write(ReportKind.Problem, "Image Opt is active but ImageOpt.Texture2DPatch.NativeTextures was not found. "
                      + "The generic pixel-readback fix is disabled. Image Opt may have changed version.");
        return nativeIds;
    }

    /// A CPU-readable stand-in for an Image Opt native texture, or null to let the original run.
    internal static Texture2D? Readable(Texture2D tex)
    {
        // Returning null makes every one of the seven prefixes fall through to Unity's original
        // method, so the fix goes inert without unpatching. The patches stay installed: this is a
        // troubleshooting switch, and leaving the wiring in place is what makes it reversible
        // without a restart.
        if (!ImageOptCompatMod.Settings.genericPixelReadback) return null;

        if (tex == null || !UnityData.IsInMainThread) return null;   // Blit/ReadPixels are main-thread only
        var ids = NativeIds();
        if (ids == null || !ids.Contains(tex.GetInstanceID())) return null;
        if (Copies.TryGetValue(tex, out var cached))
        {
            if (cached != null) { Served++; return cached; }   // Include cached per-pixel reads.
            Copies.Remove(tex);

            // The two null tests above and below are DELIBERATELY different operators, and the
            // difference is the whole point. `!= null` is Unity's overload: false for a DESTROYED
            // texture as well as a null reference. `is not null` is a plain reference test, which
            // is still TRUE for a destroyed object - which is precisely the case that reaches here
            // and the entry we must drop from OwnedCopies.
            // CA1508 flags this as dead code because the analyzer models `!= null` as a reference
            // comparison and cannot see Unity's operator. Suppressed, not rewritten: rewriting it
            // to satisfy the analyzer would leak every destroyed copy.
#pragma warning disable CA1508
            if (cached is not null) OwnedCopies.Remove(cached);
#pragma warning restore CA1508
        }

        var copy = VehicleReadback.ToCpuReadable(tex);
        if (copy == null) return null;
        Copies.Add(tex, copy);
        OwnedCopies.Add(copy);
        Served++;
        return copy;
    }

    internal static void ClearCopies()
    {
        if (!UnityData.IsInMainThread)
        {
            // ExecuteWhenFinished can run immediately on its caller's thread.
            LongEventHandler.QueueLongEvent(ClearCopies, null, false, null);
            return;
        }

        foreach (var copy in OwnedCopies)
        {
            try { if (copy != null) UnityEngine.Object.DestroyImmediate(copy); }
            catch (Exception e) { Report.Write(ReportKind.Notice, $"cached texture cleanup failed: {e.Message}"); }
        }
        OwnedCopies.Clear();
        Copies = new ConditionalWeakTable<Texture2D, Texture2D>();
        nativeIds = null;
        resolved = false;
        Served = 0;
    }

    [HarmonyPatch(typeof(PlayDataLoader), nameof(PlayDataLoader.ClearAllPlayData))]
    internal static class ClearPlayData
    {
        public static void Prefix() => ClearCopies();
    }

    // Parameter names below MUST match UnityEngine's exactly - Harmony binds by name. Note the
    // inconsistency in Unity itself: GetPixels uses `miplevel`, GetPixel/GetPixelBilinear `mipLevel`.

    [HarmonyPatch(typeof(Texture2D), nameof(Texture2D.GetPixels), new Type[] { })]
    internal static class GetPixels0
    {
        public static bool Prefix(Texture2D __instance, ref Color[] __result)
        {
            var r = Readable(__instance); if (r == null) return true;
            __result = r.GetPixels(); return false;
        }
    }

    [HarmonyPatch(typeof(Texture2D), nameof(Texture2D.GetPixels), typeof(int))]
    internal static class GetPixelsMip
    {
        public static bool Prefix(Texture2D __instance, int miplevel, ref Color[] __result)
        {
            var r = Readable(__instance); if (r == null) return true;
            __result = r.GetPixels(miplevel); return false;
        }
    }

    [HarmonyPatch(typeof(Texture2D), nameof(Texture2D.GetPixels), typeof(int), typeof(int), typeof(int), typeof(int))]
    internal static class GetPixelsBlock
    {
        public static bool Prefix(Texture2D __instance, int x, int y, int blockWidth, int blockHeight, ref Color[] __result)
        {
            var r = Readable(__instance); if (r == null) return true;
            __result = r.GetPixels(x, y, blockWidth, blockHeight); return false;
        }
    }

    [HarmonyPatch(typeof(Texture2D), nameof(Texture2D.GetPixel), typeof(int), typeof(int))]
    internal static class GetPixel2
    {
        public static bool Prefix(Texture2D __instance, int x, int y, ref Color __result)
        {
            var r = Readable(__instance); if (r == null) return true;
            __result = r.GetPixel(x, y); return false;
        }
    }

    [HarmonyPatch(typeof(Texture2D), nameof(Texture2D.GetPixel), typeof(int), typeof(int), typeof(int))]
    internal static class GetPixel3
    {
        public static bool Prefix(Texture2D __instance, int x, int y, int mipLevel, ref Color __result)
        {
            var r = Readable(__instance); if (r == null) return true;
            __result = r.GetPixel(x, y, mipLevel); return false;
        }
    }

    [HarmonyPatch(typeof(Texture2D), nameof(Texture2D.GetPixelBilinear), typeof(float), typeof(float))]
    internal static class GetPixelBilinear2
    {
        public static bool Prefix(Texture2D __instance, float u, float v, ref Color __result)
        {
            var r = Readable(__instance); if (r == null) return true;
            __result = r.GetPixelBilinear(u, v); return false;
        }
    }

    [HarmonyPatch(typeof(Texture2D), nameof(Texture2D.GetPixelBilinear), typeof(float), typeof(float), typeof(int))]
    internal static class GetPixelBilinear3
    {
        public static bool Prefix(Texture2D __instance, float u, float v, int mipLevel, ref Color __result)
        {
            var r = Readable(__instance); if (r == null) return true;
            __result = r.GetPixelBilinear(u, v, mipLevel); return false;
        }
    }
}
