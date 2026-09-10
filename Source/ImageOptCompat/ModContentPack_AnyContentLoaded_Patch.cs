using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

/// Image Opt prefixes ModContentPack.AnyContentLoaded and fills the private `textures` holder with
/// GPU-resident textures created by Texture2D.CreateExternalTexture. We postfix the SAME vanilla
/// method, so we run after its prefix and depend on nothing internal to it.
[HarmonyPatch(typeof(ModContentPack), "AnyContentLoaded")]
public static class ModContentPack_AnyContentLoaded_Patch
{
    /// CODEX P2-1: keyed on the HOLDER INSTANCE, not the packageId.
    /// Changing the game language runs PlayDataLoader.ClearAllPlayData() then LoadAllPlayData(),
    /// which destroys every texture holder and builds new ones. A packageId-keyed set still said
    /// "done", so the readback fix silently stopped applying for the rest of the process and the
    /// counter went on describing destroyed textures. A ConditionalWeakTable keyed on the holder
    /// tracks the real lifetime: a new holder is a new key, and dead entries are collected with it
    /// rather than needing an explicit teardown hook we would have to find and maintain.
    private static readonly ConditionalWeakTable<object, object> HandledHolders = new();
    private static readonly object Marker = new();

    public static void Postfix(ModContentPack __instance, ModContentHolder<Texture2D> ___textures)
    {
        if (!ImageOptCompatMod.Settings.vehicleReadback) return;
        if (__instance == null || ___textures?.contentList == null) return;

        var id = __instance.PackageId;
        if (string.IsNullOrEmpty(id) || !VehicleReadback.NeedsCpuReadback(id)) return;

        // RenderTexture/Blit/ReadPixels/new Texture2D are main-thread only. FGL loads mod content
        // off-thread, so without this every call throws and is swallowed into one warning PER
        // TEXTURE. Checked BEFORE the latch so a later main-thread call still runs.
        if (!UnityData.IsInMainThread) return;

        // AnyContentLoaded is queried repeatedly and the first call can land before Image Opt has
        // populated contentList. Latching on that empty pass permanently disabled the mod.
        if (___textures.contentList.Count == 0) return;

        lock (HandledHolders)
        {
            if (HandledHolders.TryGetValue(___textures, out _)) return;
            HandledHolders.Add(___textures, Marker);
        }

        VehicleReadback.ConvertHolder(___textures, id);
    }
}
