using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

public sealed class ImageOptCompatMod : Mod
{
    public static ImageOptCompatSettings Settings { get; private set; } = null!;
    public static bool ImageOptActive { get; private set; }

    /// null = Faster Game Loading is not active, so Image Opt's early-load race cannot happen.
    /// true / false = FGL is active and does / does not carry an Image Opt compatibility layer.
    public static bool? FglHasImageOptSupport { get; private set; }

    /// null when FGL's settings match the tested configuration; otherwise the changed ones.
    public static string? FglUntestedSettings { get; private set; }

    /// FGL's defaults are the configuration that reached the main menu with Image Opt enabled on a
    /// 1,478-mod list. Field names, not Scribe keys: these are read straight from the static fields
    /// of FasterGameLoading.FasterGameLoadingSettings. verboseLogging is omitted - it only logs.
    private static readonly (string Field, bool Tested)[] FglTestedConfig =
    {
        ("earlyModContentLoading", true),
        ("enableMultiThreading", true),
        ("xPathCaching", true),
        ("delayGraphicLoading", false),
        ("staticAtlasesBaking", false),
    };

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
        CheckFasterGameLoading();
        if (FglHasImageOptSupport == true) CheckFglSettings();
        CheckVersions();
        Log.Message("[ImageOptCompat] active alongside Image Opt.");

        // Sweep before textures are requested, so a stale file is never served.
        if (Settings.sweepOrphanZstd) OrphanSweep.Run();
    }

    /// Both the official and Preview builds of Faster Game Loading share the packageId
    /// Taranchuk.FasterGameLoading, so About.xml cannot express "requires the Preview": no
    /// dependency or incompatibility entry can tell them apart. Check the CAPABILITY instead.
    /// Without FGL's ImageOptEarlyLoadCoordinator, FGL's early content loading closes Image
    /// Opt's texture channel early and loading can black-screen - which would look like our bug.
    private static void CheckFasterGameLoading()
    {
        bool fglActive;
        try { fglActive = ModsConfig.IsActive("Taranchuk.FasterGameLoading"); }
        catch (Exception e) { Log.Warning($"[ImageOptCompat] could not query Faster Game Loading: {e.Message}"); return; }

        if (!fglActive) { FglHasImageOptSupport = null; return; }

        FglHasImageOptSupport = AccessTools.TypeByName("FasterGameLoading.ImageOptEarlyLoadCoordinator") != null;
        if (FglHasImageOptSupport == true) return;

        Log.Warning("[ImageOptCompat] Faster Game Loading is active but has no Image Opt compatibility layer. "
                  + "Use 'Faster Game Loading - Continued (Preview)', or disable Faster Game Loading. "
                  + "Without it, Image Opt can black-screen during loading.");
    }

    /// Warns - never overrides - when FGL is configured differently from what was tested. The
    /// claim is deliberately "untested", not "broken": no non-default setting has been shown to
    /// break Image Opt; they simply have not been tried with it.
    private static void CheckFglSettings()
    {
        var type = AccessTools.TypeByName("FasterGameLoading.FasterGameLoadingSettings");
        if (type == null) return;

        var changed = new List<string>();
        foreach (var (field, tested) in FglTestedConfig)
        {
            if (AccessTools.Field(type, field)?.GetValue(null) is bool value && value != tested)
                changed.Add($"{field}={value}");
        }

        FglUntestedSettings = changed.Count == 0 ? null : string.Join(", ", changed);
        if (FglUntestedSettings == null) return;

        Log.Warning($"[ImageOptCompat] Faster Game Loading settings differ from the tested configuration "
                  + $"({FglUntestedSettings}). This combination has not been tested with Image Opt. "
                  + "If loading misbehaves, reset Faster Game Loading's settings to default first.");
    }

    /// The versions this build was tested against. Reflection into Image Opt and FGL internals means a
    /// renamed field in a newer version makes a feature silently no-op; this is the early signal.
    /// modVersion from About.xml, NOT the assembly version: both authors leave that at a placeholder
    /// (ImageOpt.dll 0.0.0.0, FasterGameLoading.dll 1.0.0.0), so it identifies nothing.
    private const string TestedImageOpt = "0.1.13";
    private const string TestedFgl = "2026.09.07.1";

    public static string? UntestedVersions { get; private set; }

    private static void CheckVersions()
    {
        var notes = new List<string>();
        Compare("dev.soeur.imageopt", "Image Opt", TestedImageOpt, notes);
        Compare("Taranchuk.FasterGameLoading", "Faster Game Loading", TestedFgl, notes);
        UntestedVersions = notes.Count == 0 ? null : string.Join("; ", notes);
    }

    private static void Compare(string packageId, string label, string tested, List<string> notes)
    {
        var version = ModLister.GetActiveModWithIdentifier(packageId, ignorePostfix: true)?.ModVersion;
        if (string.IsNullOrEmpty(version) || string.Equals(version, tested, StringComparison.Ordinal)) return;
        notes.Add($"{label} {version} (tested {tested})");
        Log.Warning($"[ImageOptCompat] {label} is version {version}; this patch was tested with {tested}. "
                  + "It may still work, but an untested version can silently disable parts of this patch.");
    }

    public override string SettingsCategory() => "ImageOptCompat";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var l = new Listing_Standard();
        l.Begin(inRect);
        l.Label(ImageOptActive
            ? "Image Opt detected - fixes are live."
            : "Image Opt is NOT active. Nothing here does anything.");
        l.Label(FglHasImageOptSupport switch
        {
            null  => "Faster Game Loading: not active (fine).",
            true  => "Faster Game Loading: compatible build detected.",
            false => "WARNING: Faster Game Loading lacks Image Opt support - use the Preview build.",
        });
        if (UntestedVersions != null)
            l.Label($"Untested versions: {UntestedVersions}");
        if (FglUntestedSettings != null)
            l.Label($"Faster Game Loading settings differ from the tested defaults: {FglUntestedSettings}");
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
