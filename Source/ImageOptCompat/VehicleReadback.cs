using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

public static class VehicleReadback
{
    public static int Replaced { get; private set; }

    /// Mods whose textures are read back by the CPU. Vehicle Framework's livery/colour-mask system
    /// is the confirmed case; the rest ship vehicle textures it processes.
    private static readonly HashSet<string> CpuReadbackMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "smashphil.vehicleframework",
        "oels.vehiclemapframework",
        "oskarpotocki.vanillavehiclesexpanded",
        "oskarpotocki.vanillavehiclesexpandedtier3",
        "oskarpotocki.vanillavehiclesexpandedupgrades",
        "atomicrobot.extravehiclepatterns",
        "inoshishi3.smallvehicleaddons",
        "kupie.vfeallvehiclesrenamable",
        "mo.technicalmapvehicles",
        "spacemoth.vehicleneedsfix",
    };

    public static bool NeedsCpuReadback(string packageId) => CpuReadbackMods.Contains(packageId);

    public static void ConvertHolder(ModContentHolder<Texture2D> holder, string packageId)
    {
        if (holder?.contentList == null) return;

        var converted = 0;
        // Required even with one writer: RimWorld's Mono mscorlib increments Dictionary._version
        // at entry to TryInsert, INCLUDING OverwriteExisting. Replacing our own values therefore
        // invalidates a live Keys enumerator. The net9 test runtime behaves differently.
        // Verified with the installed Mono runtime; see the Mono regression probe. This is not
        // synchronization against external writers: Image Opt's prefix completes before us.
        foreach (var key in holder.contentList.Keys.ToList())
        {
            var src = holder.contentList[key];
            if (src == null) continue;
            var copy = ToCpuReadable(src);
            if (copy == null) continue;
            holder.contentList[key] = copy;
            converted++;

            // BUG 4: the original is leaked unless we destroy it. OPT-IN and default OFF: Image Opt
            // created it via Texture2D.CreateExternalTexture wrapping a pointer its Rust side owns,
            // so destroying it may free memory still referenced natively. A leak is survivable; a
            // native double-free is not. Enable only if VRAM proves to be a problem.
            // NOTE the deliberate asymmetry with ToCpuReadable's DestroyImmediate: `dst` is ours and
            // unreferenced, but this ORIGINAL is a CreateExternalTexture wrapper over memory Image
            // Opt's Rust side owns and still tracks in its NativeTextures set. Deferring to
            // end-of-frame gives the native side room; immediate could free under it. Opt-in, OFF.
            if (ImageOptCompatMod.Settings.destroyOriginalTexture)
                UnityEngine.Object.Destroy(src);
        }

        Replaced += converted;
        if (converted > 0 || ImageOptCompatMod.Settings.verbose)
            Log.Message($"{ModInfo.Tag} {packageId}: {converted} texture(s) made CPU-readable.");
    }

    /// ReadPixels lands in RGBA32 (uncompressed). If the source was block-compressed, keeping
    /// RGBA32 costs several times its VRAM - the opposite of what Image Opt is for. Compress()
    /// keeps the texture CPU-readable; BC needs dimensions that are multiples of 4.
    private static void RecompressIfWorthwhile(Texture2D src, Texture2D dst, bool mip)
    {
        if (!ImageOptCompatMod.Settings.recompressCopies) return;
        if (!IsBlockCompressed(src.format)) return;
        if (src.width % 4 != 0 || src.height % 4 != 0) return;

        dst.Compress(highQuality: false);
        dst.Apply(mip, false);
    }

    /// src may be an invalid Unity object by the time we need its name for a log line.
    private static string SafeName(Texture2D src)
    {
        try { return src == null ? "<null>" : src.name; }
        catch { return "<unreadable>"; }
    }

    private static bool IsBlockCompressed(TextureFormat f) => f switch
    {
        TextureFormat.DXT1 or TextureFormat.DXT1Crunched or
        TextureFormat.DXT5 or TextureFormat.DXT5Crunched or
        TextureFormat.BC4 or TextureFormat.BC5 or TextureFormat.BC6H or TextureFormat.BC7 => true,
        _ => false,
    };

    /// Blit through a RenderTexture and ReadPixels back. Unlike Graphics.CopyTexture (a GPU-side
    /// copy that leaves the destination's CPU buffer empty) this actually populates it.
    /// Returns null on failure so the caller keeps the original.
    internal static Texture2D? ToCpuReadable(Texture2D src)
    {
        // REVIEW #2: captured BEFORE the try. If the exception came from `src` being an invalid
        // Unity object, reading src.name inside the catch throws again and the caller gets an
        // exception instead of the null this method promises.
        var srcName = SafeName(src);
        RenderTexture? rt = null;
        // CODEX P2-2: declared OUTSIDE the try so the failure path can destroy it. Previously an
        // exception from ReadPixels/Apply/Compress returned null while leaving this Unity texture
        // allocated, and the finally released only the RenderTexture. Distinct from the deliberate
        // choice to retain Image Opt's ORIGINAL native textures - this one is ours to free.
        Texture2D? dst = null;
        var previous = RenderTexture.active;
        var handedOff = false;
        try
        {
            var mip = src.mipmapCount > 1;
            rt = RenderTexture.GetTemporary(src.width, src.height, 0,
                RenderTextureFormat.Default, RenderTextureReadWrite.Default);
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            // Assign ownership before reading any Unity properties, which may throw.
            dst = new Texture2D(src.width, src.height, TextureFormat.RGBA32, mip);
            dst.name = src.name;
            dst.filterMode = src.filterMode;
            dst.wrapMode = src.wrapMode;
            dst.anisoLevel = src.anisoLevel;
            dst.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            dst.Apply(mip, false);   // makeNoLongerReadable:false is the entire point

            RecompressIfWorthwhile(src, dst, mip);
            handedOff = true;   // ownership passes to the caller only once we actually return it
            return dst;
        }
        catch (Exception e)
        {
            Log.Warning($"{ModInfo.Tag} CPU-readable copy failed for '{srcName}': {e.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            // REVIEW #1: DestroyImmediate, not Destroy. Destroy() defers to end-of-frame; this runs
            // inside ModContentPack.AnyContentLoaded during content loading. Image Opt itself makes
            // exactly this distinction in the SAME method -- TextureLoadPatch.cs:107 and :138 use
            // DestroyImmediate in this prefix, while Texture2DPatch.cs:35,112 use Destroy in the
            // runtime GetPixels32 shim. We own `dst` and nothing references it, so immediate is safe.
            if (!handedOff && dst != null) UnityEngine.Object.DestroyImmediate(dst);
        }
    }
}
