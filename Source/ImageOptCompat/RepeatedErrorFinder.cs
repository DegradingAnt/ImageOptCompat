using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Verse;

namespace ImageOptCompat;

/// Names the mod behind an error that keeps repeating.
///
/// WHY. Boot 2 logged the same NullReferenceException 4,478 times, and not once with a stack trace:
/// the Harmony mod caches traces and prints "[Ref AB2188D6] Duplicate stacktrace" after the first,
/// and the first never reached the log. Each repeat still costs a throw, a trace render and a log
/// write, on the thread that ticks the game. No mod in the pack says whose code it is.
///
/// HOW. Every exception that becomes text passes through one runtime method,
/// System.Environment.GetStackTrace. Unity's formatter reads ex.StackTrace for each exception it
/// logs, and RimWorld's own catch blocks write Log.Error("..." + ex), whose ToString reads the same
/// property. It is the method the Harmony mod patches to enhance and cache traces, for that reason.
/// A postfix there receives the exception object itself, with its real frames, so the thrower can
/// be named even when the log cannot show it. The first version hooked Unity's formatter alone, and
/// Codex's review 4 showed 1,000 errors logged the RimWorld way leaving no trace in it.
///
/// One exception's text is read several times: Unity's formatter reads it three times, and an
/// outer exception's text includes its inner one's. So each exception is counted once, as its
/// innermost cause. An error is identified by its type, the method that raised it and the nearest
/// mod on its stack, so two mods failing in one shared helper are two errors, each named for its
/// own mod. The first version keyed on the throwing method alone and blamed both on whichever came
/// first.
///
/// It has its own switch, and runs only while an exception is being turned into text, which is
/// already far costlier. It reports through Report: a notice in the log at 100 repeats, and once on
/// screen at 1,000.
internal static class RepeatedErrorFinder
{
    internal const int NoticeAt = 100;
    internal const int ProblemAt = 1000;

    /// Distinct errors kept. When the list is full the rarest gives way, so one-off errors while
    /// loading cannot crowd out a flood that starts later in play.
    internal const int MaxTracked = 64;

    /// Mods named when an error passed only through patched game code.
    private const int MaxPatchOwners = 3;

    internal sealed class Entry
    {
        internal string Exception = string.Empty;
        internal string Site = string.Empty;
        internal string ThrownIn = string.Empty;
        internal string Owner = string.Empty;

        /// The first occurrence's frames, kept so a report can list the patches they pass through.
        internal StackTrace? Trace;
        internal int Count;
        internal long LastSeen;
        internal bool NoticeSent;
        internal bool ProblemSent;
    }

    private static readonly Dictionary<(Type Type, string ThrownIn, string Site), Entry> Seen = new();

    /// Exceptions already counted. The keys are weak: an exception is forgotten once it is collected.
    private static ConditionalWeakTable<Exception, object> counted = new();
    private static readonly object CountedMark = new();
    private static long sequence;
    private static readonly string[] NoExtraSkips = Array.Empty<string>();

    /// Set while this thread is inside the finder, so nothing it does can re-enter it.
    [ThreadStatic] private static bool observing;

    internal static bool Installed { get; private set; }

    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(Environment), "GetStackTrace", new[] { typeof(Exception), typeof(bool) });
            if (target == null)
            {
                Report.Write(ReportKind.Notice, "the runtime's stack-trace method was not found, so the repeated-error "
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

    /// Mono's corlib names this parameter "e", and Harmony binds by name. It is null when a caller
    /// asked for the current thread's stack, which is not an error.
    private static void Postfix(Exception? e)
    {
        if (e == null || observing || !ImageOptCompatMod.Settings.findRepeatedErrors) return;

        observing = true;
        try
        {
            Observe(e);
        }
        catch (Exception)
        {
            // This runs while an error is being turned into text. It must never become a second error.
        }
        finally
        {
            observing = false;
        }
    }

    internal static void Observe(Exception exception)
    {
        // The innermost exception is where the fault actually happened.
        var thrown = exception;
        while (thrown.InnerException != null) thrown = thrown.InnerException;

        if (!FirstSight(thrown)) return;

        var trace = new StackTrace(thrown, false);
        var (site, owner) = ModAttribution.FindCaller(trace, NoExtraSkips);
        var thrownIn = ThrowSite(trace);
        Record((thrown.GetType(), thrownIn, site), new Entry
        {
            Exception = thrown.GetType().Name,
            Site = site,
            ThrownIn = thrownIn,
            Owner = owner,
            Trace = trace,
        });
    }

    private static bool FirstSight(Exception thrown)
    {
        lock (CountedMark)
        {
            if (counted.TryGetValue(thrown, out _)) return false;
            counted.Add(thrown, CountedMark);
            return true;
        }
    }

    /// The method the exception was raised in, seen through Harmony like every other frame.
    private static string ThrowSite(StackTrace trace)
    {
        var method = trace.FrameCount == 0 ? null : ModAttribution.FrameMethod(trace.GetFrame(0));
        return method?.DeclaringType == null
            ? ModAttribution.UnidentifiedSite
            : $"{method.DeclaringType.FullName}.{method.Name}";
    }

    /// Counts one occurrence, adding the error on first sight, and reports a threshold crossed.
    internal static void Record((Type Type, string ThrownIn, string Site) key, Entry candidate)
    {
        Entry entry;
        bool notice, problem;
        lock (Seen)
        {
            if (!Seen.TryGetValue(key, out entry!))
            {
                if (Seen.Count >= MaxTracked) EvictRarest();
                entry = candidate;
                Seen[key] = entry;
            }

            entry.Count++;
            entry.LastSeen = ++sequence;

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

    /// The least-seen error goes, and of those the one seen longest ago. Called with the lock held.
    private static void EvictRarest()
    {
        var rarest = Seen.OrderBy(pair => pair.Value.Count).ThenBy(pair => pair.Value.LastSeen).First().Key;
        Seen.Remove(rarest);
    }

    private static string Describe(Entry entry, int count)
    {
        var text = new StringBuilder();
        if (ModAttribution.NamesOneMod(entry.Owner))
        {
            text.Append($"{entry.Owner} has thrown the same {entry.Exception} {count} times this session, at {Where(entry)}. ")
                .Append("The mod's author can fix it at that method.");
        }
        else
        {
            text.Append($"The same {entry.Exception} has been thrown {count} times this session, at {Where(entry)} "
                      + $"({entry.Owner}). No single mod's code is on its stack.");
            var patchedBy = PatchOwners(entry.Trace);
            if (patchedBy.Length > 0) text.Append($" The methods it passed through carry patches from {patchedBy}.");
        }

        return text.Append(" Each repeat costs frame time; the full list is in this patch's diagnostic report.").ToString();
    }

    /// The nearest mod frame, and where the exception was raised when that is somewhere else.
    private static string Where(Entry entry)
    {
        if (string.Equals(entry.ThrownIn, entry.Site, StringComparison.Ordinal)
         || string.Equals(entry.ThrownIn, ModAttribution.UnidentifiedSite, StringComparison.Ordinal)) return entry.Site;
        if (string.Equals(entry.Site, ModAttribution.UnidentifiedSite, StringComparison.Ordinal)) return entry.ThrownIn;
        return $"{entry.Site}, thrown in {entry.ThrownIn}";
    }

    /// Mods whose Harmony patches sit on the methods an error passed through, nearest first, as the
    /// Harmony mod lists them under each frame of its traces. For errors with no mod's own code on
    /// the stack: a prefix or a transpiler changes a game method without leaving a frame of its
    /// own. It says who patched, not who is at fault, and the report words it that way.
    internal static string PatchOwners(StackTrace? trace)
    {
        if (trace == null) return string.Empty;

        var owners = new List<string>();
        try
        {
            for (var i = 0; i < trace.FrameCount; i++)
            {
                var method = ModAttribution.FrameMethod(trace.GetFrame(i));
                var patches = method == null ? null : Harmony.GetPatchInfo(method);
                if (patches == null) continue;

                foreach (var patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers))
                {
                    var owner = ModAttribution.Describe(patch.PatchMethod?.DeclaringType);
                    if (ModAttribution.NamesOneMod(owner) && !owners.Contains(owner)) owners.Add(owner);
                }
            }
        }
        catch (Exception)
        {
            // A convenience inside a diagnostic; never worth a second error.
        }

        return owners.Count <= MaxPatchOwners
            ? string.Join(", ", owners)
            : string.Join(", ", owners.Take(MaxPatchOwners)) + $" and {owners.Count - MaxPatchOwners} more";
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
            text.AppendLine("No errors were recorded this session.");
            return text.ToString();
        }

        foreach (var row in rows)
        {
            text.AppendLine($"  {row.Count,7:N0}  {row.Exception} at {Where(row)} - {row.Owner}");
            if (ModAttribution.NamesOneMod(row.Owner)) continue;

            var patchedBy = PatchOwners(row.Trace);
            if (patchedBy.Length > 0) text.AppendLine($"           passed through methods patched by {patchedBy}");
        }

        if (rows.Count >= MaxTracked)
            text.AppendLine($"  (the list keeps {MaxTracked} distinct errors; rarer ones give way to new ones)");
        return text.ToString();
    }

    /// Lets a test start from nothing.
    internal static void Reset()
    {
        lock (Seen) Seen.Clear();
        lock (CountedMark) counted = new ConditionalWeakTable<Exception, object>();
    }
}
