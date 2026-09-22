using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

/// The on-screen side of Report, done the way the Harmony mod does it.
///
/// Harmony postfixes VersionControl.DrawInfoInCorner, draws a faded one-line status ("Harmony
/// v2.4.2.0") in the main-menu corner with a tooltip, and shows a problem found at startup ONCE as
/// a Dialog_MessageBox from that same postfix. This copies the pattern rather than inventing one:
/// players already know where that corner is. The line sits one row below Harmony's.
internal static class MainMenuStatus
{
    /// Harmony draws at y=58. One small-font row below it keeps both readable.
    private const float Top = 80f;
    private const float Left = 10f;

    private static bool dialogShown;

    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(VersionControl), nameof(VersionControl.DrawInfoInCorner));
            if (target == null)
            {
                Report.Write(ReportKind.Notice, "VersionControl.DrawInfoInCorner was not found, so the main-menu "
                                              + "status line is off. Problems are still written to the log.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(MainMenuStatus), nameof(Postfix))));
        }
        catch (Exception e)
        {
            Report.Write(ReportKind.Notice, $"main-menu status could not be installed: {e.Message}");
        }
    }

    private static void Postfix()
    {
        try
        {
            ShowStartupDialogOnce();
            if (Report.CurrentLevel == ReportLevel.Quiet) return;
            DrawStatusLine();
        }
        catch (Exception)
        {
            // Runs every frame on the main menu. A failure here must never take the menu down.
        }
    }

    /// Everything queued during loading, in one dialog, once. After that, new problems go straight
    /// to in-game messages, since the game can now show them itself.
    private static void ShowStartupDialogOnce()
    {
        if (dialogShown) return;
        dialogShown = true;
        Report.LiveDisplay = ShowLive;

        var pending = Report.TakePending();
        if (pending.Count == 0) return;

        var text = new StringBuilder();
        text.AppendLine(pending.Count == 1
            ? "This needs your attention:"
            : $"These {pending.Count} things need your attention:");
        foreach (var line in pending)
        {
            text.AppendLine();
            text.Append("- ").AppendLine(line);
        }

        text.AppendLine();
        text.Append("How much this mod reports is set by \"Report level\" in its settings.");
        Find.WindowStack.Add(new Dialog_MessageBox(text.ToString(), "OK", title: ModInfo.Name));
    }

    private static void ShowLive(string text) =>
        Messages.Message(ModInfo.Tag + " " + text, MessageTypeDefOf.NegativeEvent, historical: false);

    private static string? cachedLabel;
    private static Vector2 cachedSize;
    private static int cachedProblems = -1;
    private static IReadOnlyList<StartupCheck.Result>? cachedResults;

    private static void DrawStatusLine()
    {
        // This runs every frame the menu is open. The text only changes when a problem is reported
        // or the checks rerun, so it is rebuilt then and not every frame.
        var problems = Report.ShownCount;
        var results = StartupCheck.Results;
        var rebuild = cachedLabel == null || problems != cachedProblems || !ReferenceEquals(results, cachedResults);
        if (rebuild)
        {
            cachedLabel = problems == 0
                ? $"{ModInfo.Name}: {StartupCheck.Summary()}"
                : $"{ModInfo.Name}: {problems} problem(s) - hover for details";
            cachedProblems = problems;
            cachedResults = results;
        }

        var oldFont = Text.Font;
        var oldColor = GUI.color;
        Text.Font = GameFont.Small;
        GUI.color = (problems == 0 ? Color.white : new Color(1f, 0.8f, 0.4f)).ToTransparent(0.5f);

        if (rebuild) cachedSize = Text.CalcSize(cachedLabel);
        var rect = new Rect(Left, Top, cachedSize.x, cachedSize.y);
        Widgets.Label(rect, cachedLabel);

        GUI.color = oldColor;
        Text.Font = oldFont;

        if (!Mouse.IsOver(rect)) return;
        Widgets.DrawHighlight(rect);
        TooltipHandler.TipRegion(rect, Tooltip());
    }

    /// Every startup check with its outcome, then any problem reported since loading.
    private static string Tooltip()
    {
        var text = new StringBuilder();
        text.AppendLine("Startup checks:");
        foreach (var result in StartupCheck.Results) text.AppendLine(result.ToString());

        if (Report.ShownCount > 0)
        {
            text.AppendLine();
            text.AppendLine("Problems this session:");
            text.Append(Report.ShownSummary());
        }

        return text.ToString().TrimEnd();
    }
}
