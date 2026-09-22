using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOptCompat;

/// Names the mod behind an error that keeps repeating.
///
/// WHY. Boot 2 logged the same NullReferenceException 4,478 times, and not once with a stack trace:
/// the Harmony mod caches traces and prints "[Ref AB2188D6] Duplicate stacktrace" after the first,
/// and the first never reached the log. Each repeat still costs a throw, a trace render and a log
/// write, on the thread that ticks the game. No mod in the pack says whose code it is.
///
/// HOW. Unity turns every exception it logs into text in StackTraceUtility.
/// ExtractStringFromExceptionInternal - the method whose ex.StackTrace read the Harmony mod's
/// cache answers. A postfix there still receives the exception object itself, with its real
/// frames, so the thrower can be named even when the log cannot show it. Errors are fingerprinted
/// by type and throw site; the stack is walked only the first time a fingerprint is seen, so a
/// flood costs one dictionary lookup per repeat.
///
/// It only runs when an error is being logged, which is already far costlier, and it has its own
/// switch. It reports through Report: a notice in the log at 100 repeats, and once on screen at 1,000.
internal static class RepeatedErrorFinder
{
    internal const int NoticeAt = 100;
    internal const int ProblemAt = 1000;

    /// Distinct errors tracked. A session with more is broken in ways a list cannot help with.
    private const int MaxTracked = 64;

    internal sealed class Entry
    {
        internal string Exception = string.Empty;
        internal string Site = string.Empty;
        internal string Owner = string.Empty;
        internal int Count;
        internal bool NoticeSent;
        internal bool ProblemSent;
    }

    private static readonly Dictionary<(Type Type, MethodBase? Site), Entry> Seen = new();
    private static readonly string[] NoExtraSkips = Array.Empty<string>();

    internal static bool Installed { get; private set; }

    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(StackTraceUtility), "ExtractStringFromExceptionInternal");
            if (target == null)
            {
                Report.Write(ReportKind.Notice, "Unity's exception formatter was not found, so the repeated-error "
                                              + "finder is off. Errors are still logged as normal.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(RepeatedErrorFinder), nameof(Postfix))));
            Installed = true;
        }
        catch (Exception e)
        {
            Report.Write(ReportKind.Notice, $"the repeated-error finder could not be installed: {e.Message}");
        }
    }

    /// Unity names this parameter "exceptiono", and Harmony binds by name.
    private static void Postfix(object exceptiono)
    {
        if (!ImageOptCompatMod.Settings.findRepeatedErrors || exceptiono is not Exception exception) return;

        try
        {
            Observe(exception);
        }
        catch (Exception)
        {
            // This runs while an error is being logged. It must never become a second error.
        }
    }

    internal static void Observe(Exception exception)
    {
        // The innermost exception is where the fault actually happened.
        var thrown = exception;
        while (thrown.InnerException != null) thrown = thrown.InnerException;

        Entry entry;
        bool notice, problem;
        lock (Seen)
        {
            var key = (thrown.GetType(), thrown.TargetSite);
            if (!Seen.TryGetValue(key, out entry!))
            {
                if (Seen.Count >= MaxTracked) return;
                var (site, owner) = ModAttribution.FindCaller(new StackTrace(thrown, false), NoExtraSkips);
                entry = new Entry { Exception = thrown.GetType().Name, Site = site, Owner = owner };
                Seen[key] = entry;
            }

            entry.Count++;

            // The log is not safe off the main thread, so a threshold crossed on a worker is reported
            // by the next repeat that happens on the main thread.
            var main = UnityData.IsInMainThread;
            notice = main && !entry.NoticeSent && entry.Count >= NoticeAt;
            problem = main && !entry.ProblemSent && entry.Count >= ProblemAt;
            if (notice) entry.NoticeSent = true;
            if (problem) entry.ProblemSent = true;
        }

        if (problem) Report.Write(ReportKind.Problem, Describe(entry, ProblemAt));
        else if (notice) Report.Write(ReportKind.Notice, Describe(entry, NoticeAt));
    }

    private static string Describe(Entry entry, int count)
    {
        var who = ModAttribution.NamesOneMod(entry.Owner)
            ? $"{entry.Owner} has thrown the same {entry.Exception} {count} times this session, at {entry.Site}."
            : $"The same {entry.Exception} has been thrown {count} times this session, at {entry.Site} "
              + $"({entry.Owner}). No single mod's code is on its stack.";
        return who + " Each repeat costs frame time. The mod's author can fix it at that method; the full list is "
                   + "in this patch's diagnostic report.";
    }

    /// Worst first, for the diagnostic report and the settings page.
    internal static List<Entry> Snapshot()
    {
        lock (Seen)
        {
            return Seen.Values.OrderByDescending(e => e.Count).ToList();
        }
    }

    internal static string BuildReport()
    {
        var rows = Snapshot();
        var text = new StringBuilder();
        text.AppendLine("Repeated errors - the code that keeps throwing, worst first");
        if (!Installed || !ImageOptCompatMod.Settings.findRepeatedErrors)
        {
            text.AppendLine("The repeated-error finder is off or not installed.");
            return text.ToString();
        }

        if (rows.Count == 0)
        {
            text.AppendLine("No errors were logged this session.");
            return text.ToString();
        }

        foreach (var row in rows)
            text.AppendLine($"  {row.Count,7:N0}  {row.Exception} at {row.Site} - {row.Owner}");
        if (rows.Count >= MaxTracked) text.AppendLine($"  (list capped at {MaxTracked} distinct errors)");
        return text.ToString();
    }

    /// Lets a test start from nothing.
    internal static void Reset()
    {
        lock (Seen) Seen.Clear();
    }
}
