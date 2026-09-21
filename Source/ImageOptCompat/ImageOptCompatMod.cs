using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
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
    private Vector2 settingsScrollPosition;
    private float settingsContentHeight = 1200f;

    public ImageOptCompatMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<ImageOptCompatSettings>();

        try { ImageOptActive = ModsConfig.IsActive("dev.soeur.imageopt"); }
        catch (Exception e) { ImageOptActive = false; Log.Warning($"[ImageOptCompat] could not query Image Opt: {e.Message}"); }

        var harmony = new Harmony("degradingant.imageoptcompat");

        // Installed FIRST and UNCONDITIONALLY: these guard other mods' pre-load NullReferenceExceptions,
        // which are not Image Opt's doing. Anything that lengthens the load can trigger them.
        if (Settings.earlyUiGuards) EarlyUiGuards.TryInstall(harmony);

        // Also unconditional, and for the same reason: the null-texture log flood is a RimWorld-wide
        // problem reported against several unrelated UI mods, not an Image Opt one, and it costs
        // frame time whether or not Image Opt is loaded.
        if (Settings.nullTextureGuard) NullTextureGuard.TryInstall(harmony);

        // One postfix serves both the double-extension fix and the optional recording, so the
        // hot path is paid for once. Installed early, because most texture lookups happen during
        // loading. It returns immediately unless the lookup already failed.
        if ((ImageOptActive && Settings.fixDoubleExtensionPaths) || Settings.reportMissingTextures)
            MissingTextureReport.TryInstall(harmony);

        if (!ImageOptActive)
        {
            Log.Message("[ImageOptCompat] Image Opt is not active - Image Opt features stay off; "
                      + "early-UI and null-texture guards remain.");
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
        FglUntestedSettings = FglSettingsCheck.Differences(
            AccessTools.TypeByName("FasterGameLoading.FasterGameLoadingSettings"));
        if (FglUntestedSettings == null) return;

        Log.Warning($"[ImageOptCompat] Faster Game Loading settings differ from the tested configuration or could not be checked "
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
        var viewRect = new Rect(0f, 0f, inRect.width - 20f, settingsContentHeight);
        Widgets.BeginScrollView(inRect, ref settingsScrollPosition, viewRect);
        // Listing otherwise starts a new column outside the clipped window when it fills up.
        var l = new Listing_Standard { maxOneColumn = true };
        l.Begin(viewRect);
        l.Label(ImageOptActive
            ? "Image Opt detected - fixes are live."
            : "Image Opt is NOT active. Texture fixes and sweep are off; "
            + "early-UI and null-texture guards remain active.");
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
        DrawFixToggles(l);

        l.GapLine();
        DrawSessionCounters(l);

        settingsContentHeight = l.CurHeight + 12f;
        l.End();
        Widgets.EndScrollView();
    }

    /// The controls. Split from DoSettingsWindowContents so neither half grows past readable.
    private static void DrawFixToggles(Listing_Standard l)
    {
        l.Label("Fixes");
        l.Gap(6f);

        l.CheckboxLabeled("Generic pixel readback", ref Settings.genericPixelReadback,
            "Image Opt keeps textures on the GPU, where a mod reading their pixels gets an error or blank "
          + "data. This answers those reads from a CPU-readable copy, for any mod. Switching it off makes "
          + "the fix inert immediately, without a restart, so it can be ruled in or out while the game runs.");
        l.Gap();

        l.CheckboxLabeled("Early-load guards", ref Settings.earlyUiGuards,
            "Vanilla Expanded Framework and Worldbuilder read data before it exists on the loading screen, "
          + "which throws once per frame and can leave the screen black. Both are held back until the data "
          + "is ready. Not an Image Opt fault - anything that lengthens loading can trigger it. "
          + "Requires a restart.");
        l.Gap();

        l.CheckboxLabeled("Vehicle readback fix", ref Settings.vehicleReadback,
            "Give vehicle mods CPU-readable texture copies so Vehicle Framework can build liveries. "
          + "Without this, turrets can render black or with colour masks overlaid. Requires a restart.");
        l.Gap();

        // Sub-option of the readback fix: only meaningful when copies are actually being made.
        if (Settings.vehicleReadback)
        {
            l.CheckboxLabeled("    Recompress readback copies", ref Settings.recompressCopies,
                "ReadPixels always produces uncompressed RGBA32. When the source was block-compressed, "
              + "keeping RGBA32 costs several times the VRAM - the opposite of what Image Opt is for. "
              + "This recompresses the copy, which stays CPU-readable. Only applies to block-compressed "
              + "sources whose width and height are both multiples of 4. Requires a restart.");
            l.Gap();

            l.CheckboxLabeled("    Destroy the original texture (advanced)", ref Settings.destroyOriginalTexture,
                "OFF by default, and deliberately so. Once a CPU-readable copy replaces a texture, the "
              + "original is leaked unless destroyed - but Image Opt created it with CreateExternalTexture "
              + "wrapping memory its Rust side owns and still tracks. Destroying it can free memory that is "
              + "still referenced natively. A leak is survivable; a native double-free is not. Turn this on "
              + "only if VRAM proves to be a problem. Requires a restart.");
            l.Gap();
        }

        DrawTextureFixToggles(l);
    }

    private static void DrawTextureFixToggles(Listing_Standard l)
    {
        l.CheckboxLabeled("Fix double-extension texture paths", ref Settings.fixDoubleExtensionPaths,
            "Image Opt writes its cache as 'name.dds.zstd'. A mod that builds texture paths by scanning its "
          + "own folder gets 'name.dds' back, because stripping one extension is not enough, and then asks "
          + "the game for a file that does not exist. The texture comes back empty and Unity logs a warning "
          + "every frame it is drawn. This retries the correct path. It only runs after a lookup has already "
          + "failed, so it cannot change a result that worked. Requires a restart.");
        l.Gap();

        l.CheckboxLabeled("Null-texture guard", ref Settings.nullTextureGuard,
            "Unity logs \"null texture passed to GUI.DrawTexture\" once per call, with no deduplication - "
          + "measured at 222,128 in a single session, 94% of the whole log. Drawing and ticking share one "
          + "thread, so that cost comes out of tick throughput and shows up as stutter above 1x speed. "
          + "This skips the invalid draw and samples the first eight null draws for diagnostics. "
          + "Enable the placeholder below to make the missing texture visible. Requires a restart to enable.");
        l.Gap();

        if (Settings.nullTextureGuard)
        {
            l.CheckboxLabeled("    Show a placeholder instead of nothing", ref Settings.nullTextureShowPlaceholder,
                "OFF by default. A null texture currently draws nothing, so the guard skips the draw. "
              + "Turn this on to draw the game's magenta "
              + "missing-texture square instead, which makes every affected element obvious on screen - useful "
              + "for finding the mod at fault, but visually noisy while it is on.");
            l.Gap();
        }

        DrawSweepAndLoggingToggles(l);
    }

    private static void DrawSweepAndLoggingToggles(Listing_Standard l)
    {
        l.CheckboxLabeled("Sweep orphaned .dds.zstd", ref Settings.sweepOrphanZstd,
            "Delete Image Opt .dds.zstd files whose source image no longer exists. These are served as "
          + "stale textures otherwise. Plain .dds is never touched - mods legitimately ship those.");
        l.Gap();

        l.CheckboxLabeled("Verbose logging", ref Settings.verbose,
            "Log each fix even when it changed nothing. Useful when checking whether a fix is running at "
          + "all; noisy otherwise.");
        l.GapLine();
        DrawDiagnosticToggles(l);
    }

    /// Both cost measurable frame time, so both are off by default and say so.
    private static void DrawDiagnosticToggles(Listing_Standard l)
    {
        l.Label("Diagnostics - for finding the mod at fault");
        l.Gap(6f);

        l.CheckboxLabeled("Report missing textures", ref Settings.reportMissingTextures,
            "OFF by default. The path repair shares this patch, but recording has its own switch. "
          + "Turn it on to record failed required texture lookups, with the def "
          + "and the mod that shipped it. That is the actual cause of most null-texture spam, and the "
          + "report below is what an author needs to fix it. Requires a restart.");
        l.Gap();

        l.CheckboxLabeled("Deep null-texture diagnostic", ref Settings.nullTextureDeepDiagnostic,
            "OFF by default, and genuinely slow: it reads the call stack on EVERY null draw rather than "
          + "the first few, to build an exact per-mod count. Turn it on only while hunting a fault.");
    }

    /// Split out so DoSettingsWindowContents stays readable; these are read-outs, not controls.
    private static void DrawSessionCounters(Listing_Standard l)
    {
        l.Label("This session");
        l.Gap(6f);

        l.Label($"Vehicle textures replaced: {VehicleReadback.Replaced}");
        l.Label($"Early-UI guards: {EarlyUiGuards.InstalledCount} installed, "
              + $"{EarlyUiGuards.VefSkips} VEF + {EarlyUiGuards.WorldbuilderSkips} Worldbuilder skips.");
        l.Label(NullTextureGuard.InstalledCount == 0
            ? "Null-texture guard: NOT installed - the log flood is not being stopped."
            : $"Null-texture guard: {NullTextureGuard.InstalledCount} draw method(s) hooked, "
            + $"{NullTextureGuard.Substituted} null draw(s) intercepted.");
        l.Label($"Last sweep: {OrphanSweep.LastDeleted} orphan(s) removed, {OrphanSweep.LastScanned} file(s) scanned.");
        l.Label(MissingTextureReport.Installed && Settings.reportMissingTextures
            ? $"Missing textures recorded: {MissingTextureReport.DistinctPaths} distinct path(s)."
            : "Missing-texture reporting is off, so nothing is being recorded.");
        if (l.ButtonText("Sweep now")) OrphanSweep.Run(force: true);
        l.Gap();

        // Copied rather than only logged: a report an author can paste into a bug thread is far
        // more use than one buried in a 200,000-line Player.log.
        if (l.ButtonText("Copy diagnostic report to clipboard"))
        {
            var report = NullTextureGuard.BuildReport()
                       + Environment.NewLine
                       + MissingTextureReport.BuildReport();
            GUIUtility.systemCopyBuffer = report;
            Messages.Message("[ImageOptCompat] diagnostic report copied to the clipboard.",
                MessageTypeDefOf.TaskCompletion, historical: false);
        }
    }
}
