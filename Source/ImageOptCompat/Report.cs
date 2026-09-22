using System;
using System.Collections.Generic;
using Verse;

namespace ImageOptCompat;

/// Every message this mod writes goes through here, so a single setting decides how much the
/// player sees. Before this, each file chose for itself, and "verbose" was the only control.
///
/// Deliberately free of UI types, so the test projects can link it. ImageOptCompatMod supplies the
/// on-screen side: one dialog after loading, then in-game messages.
internal static class Report
{
    /// Whether a message of this kind is written to the log at this level.
    internal static bool Logs(ReportLevel level, ReportKind kind) => kind switch
    {
        ReportKind.Breaking => true,
        ReportKind.Problem or ReportKind.Notice or ReportKind.Hint => level >= ReportLevel.Important,
        _ => level >= ReportLevel.Everything,
    };

    /// Whether it is also shown on screen. Only things the player must act on qualify, and Quiet
    /// shows nothing at all.
    internal static bool OnScreen(ReportLevel level, ReportKind kind) =>
        level >= ReportLevel.Important && kind is ReportKind.Breaking or ReportKind.Problem;

    /// 0.2.0 had one "verbose" switch. A player who turned it on keeps seeing everything; a level
    /// chosen since then wins, because "verbose" is never written again.
    internal static ReportLevel Migrate(ReportLevel saved, bool legacyVerbose) =>
        legacyVerbose && saved == ReportLevel.Important ? ReportLevel.Everything : saved;

    private static readonly List<string> Pending = new();
    private static readonly HashSet<string> Shown = new(StringComparer.Ordinal);
    private static readonly List<string> ShownInOrder = new();

    /// How many distinct on-screen problems this session has produced, for the status line.
    internal static int ShownCount
    {
        get { lock (Pending) return ShownInOrder.Count; }
    }

    /// Those problems in the order they happened, for the status line's tooltip.
    internal static string ShownSummary()
    {
        lock (Pending)
        {
            return ShownInOrder.Count == 0
                ? "No game-breaking or fixable problems were found this session."
                : string.Join("\n\n", ShownInOrder);
        }
    }

    /// Set once loading has finished and the game can show messages itself. Until then on-screen
    /// messages are collected, and shown together in one dialog.
    internal static Action<string>? LiveDisplay;

    internal static ReportLevel CurrentLevel =>
        ImageOptCompatMod.Settings?.reportLevel ?? ReportLevel.Important;

    /// Cheap check for callers that would otherwise build an expensive string for nothing.
    internal static bool Logs(ReportKind kind) => Logs(CurrentLevel, kind);

    internal static void Write(ReportKind kind, string text)
    {
        var level = CurrentLevel;

        if (Logs(level, kind))
        {
            // Severity follows the Harmony mod's convention: a problem that can break loading is a
            // Log.Error, which also opens the debug log in dev mode. Everything else stays quieter.
            var line = ModInfo.Tag + " " + text;
            switch (kind)
            {
                case ReportKind.Breaking: Log.Error(line); break;
                case ReportKind.Problem or ReportKind.Notice: Log.Warning(line); break;
                default: Log.Message(line); break;
            }
        }

        if (!OnScreen(level, kind)) return;

        lock (Pending)
        {
            // Once per distinct message: a problem that recurs every frame must not recur on screen.
            if (!Shown.Add(text)) return;
            ShownInOrder.Add(text);

            // The live display draws UI, which is main-thread only. Off-thread, the log line stands.
            if (LiveDisplay == null) Pending.Add(text);
            else if (UnityData.IsInMainThread) LiveDisplay(text);
        }
    }

    /// Output the player asked for by pressing a button. Always written, whatever the level.
    internal static void Requested(string text) => Log.Message(ModInfo.Tag + " " + text);

    /// The on-screen messages collected so far, for the dialog shown after loading.
    internal static List<string> TakePending()
    {
        lock (Pending)
        {
            var copy = new List<string>(Pending);
            Pending.Clear();
            return copy;
        }
    }

    /// Lets a test start from nothing.
    internal static void Reset()
    {
        lock (Pending)
        {
            Pending.Clear();
            Shown.Clear();
            ShownInOrder.Clear();
            LiveDisplay = null;
        }
    }
}
