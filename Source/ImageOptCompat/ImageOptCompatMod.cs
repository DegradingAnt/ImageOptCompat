using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

public sealed class ImageOptCompatMod : Mod
{
    public static ImageOptCompatSettings Settings { get; private set; } = null!;
    public static bool ImageOptActive { get; private set; }

    public ImageOptCompatMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<ImageOptCompatSettings>();

        try { ImageOptActive = ModsConfig.IsActive("dev.soeur.imageopt"); }
        catch (Exception e) { ImageOptActive = false; Log.Warning($"[ImageOptCompat] could not query Image Opt: {e.Message}"); }

        var harmony = new Harmony("degradingant.imageoptcompat");

        // Installed FIRST and UNCONDITIONALLY: these guard other mods' pre-load NullReferenceExceptions,
        // which are not Image Opt's doing. Anything that lengthens the load can trigger them.
        EarlyUiGuards.TryInstall(harmony);

        if (!ImageOptActive)
        {
            Log.Message("[ImageOptCompat] Image Opt is not active - Image Opt features stay off; early-UI guards remain.");
            return;
        }

        harmony.PatchAll();
        Log.Message("[ImageOptCompat] active alongside Image Opt.");

        // Sweep before textures are requested, so a stale file is never served.
        if (Settings.sweepOrphanZstd) OrphanSweep.Run();
    }

    public override string SettingsCategory() => "ImageOptCompat";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var l = new Listing_Standard();
        l.Begin(inRect);
        l.Label(ImageOptActive
            ? "Image Opt detected - fixes are live."
            : "Image Opt is NOT active. Nothing here does anything.");
        l.GapLine();
        l.CheckboxLabeled("Vehicle readback fix", ref Settings.vehicleReadback,
            "Give vehicle mods CPU-readable texture copies so Vehicle Framework can build liveries. "
          + "Without this, turrets can render black or with colour masks overlaid. Requires a restart.");
        l.Gap();
        l.CheckboxLabeled("Sweep orphaned .dds.zstd", ref Settings.sweepOrphanZstd,
            "Delete Image Opt .dds.zstd files whose source image no longer exists. These are served as "
          + "stale textures otherwise. Plain .dds is never touched - mods legitimately ship those.");
        l.Gap();
        l.CheckboxLabeled("Verbose logging", ref Settings.verbose, null);
        l.Gap();
        l.Label($"Last sweep: {OrphanSweep.LastDeleted} orphan(s) removed, {OrphanSweep.LastScanned} file(s) scanned.");
        if (l.ButtonText("Sweep now")) OrphanSweep.Run(force: true);
        l.Gap();
        l.Label($"Vehicle textures replaced this session: {VehicleReadback.Replaced}");
        l.Label($"Early-UI guards: {EarlyUiGuards.InstalledCount} installed, "
              + $"{EarlyUiGuards.VefSkips} VEF + {EarlyUiGuards.WorldbuilderSkips} Worldbuilder skips.");
        l.End();
    }
}
