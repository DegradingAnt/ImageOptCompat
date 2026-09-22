using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace ImageOptCompat;

/// Finds which installed mod mentions an asset path that the game could not load.
///
/// WHY THIS EXISTS. RimWorld's error line is "Could not load Texture2D at 'X' in any active mod or
/// in base resources." It appends "for def 'Y'" only when a def triggered the lookup. Code that
/// calls ContentFinder directly leaves that blank, so the line names no owner. Measured in one
/// session: 2,625 such lines, every one of them ownerless.
///
/// MissingTextureReport reads the call stack, which names the assembly that made the call. That is
/// not always the mod at fault: a shared UI helper shows up as the caller for everyone who uses it.
/// This scan answers the other half of the question by asking which mod's FILES contain the string.
/// Between them the culprit is usually obvious.
///
/// It runs in game rather than as a separate script on purpose. The game already knows where every
/// active mod lives, so there is no Steam path to configure and it works on any install and any
/// operating system.
///
/// COST. This reads every XML and every assembly of every active mod. The reads run in parallel,
/// because they touch no Unity or Verse API, but the CALLING thread still waits for them, so the
/// game is unresponsive while a scan runs. It is a button, never automatic.
internal static class AssetRequesterScan
{
    /// Assemblies are read whole. This skips anything implausibly large rather than risk a
    /// multi-hundred-megabyte read on a broken install.
    private const long MaxFileBytes = 64L * 1024 * 1024;

    /// A scan reports at most this many owners per asset. More than a handful means the string is
    /// too common to be diagnostic.
    private const int MaxOwnersPerAsset = 6;

    internal sealed class Owner
    {
        internal string Mod = "unknown mod";
        internal string File = string.Empty;
        internal bool InCode;
    }

    /// Searches every active mod for ALL the given asset paths in ONE pass.
    ///
    /// The single pass is the whole design. Searching per path would re-read the entire mod tree
    /// once per path, and the recorder holds up to 256 of them: on a large list that is hours of
    /// disk reads with the UI frozen. Reading each file once and testing every path against its
    /// contents keeps the cost proportional to the mods installed, not to the paths recorded.
    ///
    /// Two encodings, and the second is the one that is easy to get wrong. A .NET assembly stores
    /// type and member names as UTF-8 in the #Strings heap, but STRING LITERALS as UTF-16 in the
    /// #US heap. Searching a DLL as UTF-8 therefore finds nothing and reads as a clean negative.
    /// That mistake cost a wrong answer earlier in this project, so both encodings are checked.
    internal static Dictionary<string, List<Owner>> FindAll(IReadOnlyCollection<string> assetPaths)
    {
        var results = new Dictionary<string, List<Owner>>(StringComparer.Ordinal);
        foreach (var p in assetPaths)
        {
            if (!string.IsNullOrEmpty(p)) results[p] = new List<Owner>();
        }

        if (results.Count == 0) return results;

        var work = CollectSearchableFiles();
        var needles = new List<string>(results.Keys);
        var found = new ConcurrentBag<(string Path, Owner Owner)>();

        // Reading and decoding every assembly is the expensive part and it parallelises cleanly.
        // Degree is capped: this is disk and memory bound, and saturating every core on a machine
        // that is also running the game buys nothing.
        var degree = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));

        try
        {
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = degree }, item =>
            {
                var haystacks = ReadHaystacks(item.File, item.IsCode);
                if (haystacks == null) return;

                foreach (var needle in needles)
                {
                    if (!Mentions(haystacks, needle, item.IsCode)) continue;

                    found.Add((needle, new Owner { Mod = item.Label, File = item.File, InCode = item.IsCode }));
                }
            });
        }
        catch (AggregateException e)
        {
            // One unreadable file must not lose the whole scan. Report what was gathered.
            Log.Warning($"[ImageOptCompat] the asset scan hit {e.InnerExceptions.Count} error(s); "
                      + "results below may be incomplete.");
        }

        // Merging on one thread keeps the per-asset cap deterministic, which a racing early-exit
        // inside the loop would not be.
        foreach (var hit in found)
        {
            var list = results[hit.Path];
            if (list.Count < MaxOwnersPerAsset) list.Add(hit.Owner);
        }

        return results;
    }

    /// Reads the mod list on the CALLING thread, because LoadedModManager is game state. Every
    /// step after this is file I/O and string comparison with no Unity or Verse call in it, which
    /// is the only reason the search itself can safely leave the main thread.
    private static List<(string File, string Label, bool IsCode)> CollectSearchableFiles()
    {
        var work = new List<(string File, string Label, bool IsCode)>();

        foreach (var mod in LoadedModManager.RunningMods)
        {
            if (mod?.RootDir == null || !Directory.Exists(mod.RootDir)) continue;

            var label = string.IsNullOrEmpty(mod.PackageId) ? mod.Name : $"{mod.Name} ({mod.PackageId})";

            foreach (var file in SafeFiles(mod.RootDir))
            {
                var isCode = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                var isXml = file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
                if (isCode || isXml) work.Add((file, label, isCode));
            }
        }

        return work;
    }

    private static IEnumerable<string> SafeFiles(string root)
    {
        string[] files;

        try
        {
            files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            // An unreadable mod folder is that mod's problem. Skip it rather than abort the scan.
            yield break;
        }

        foreach (var f in files) yield return f;
    }

    /// Decodes a file ONCE into the forms that need searching, so the per-path test below is a
    /// plain string comparison rather than another read.
    private static string[]? ReadHaystacks(string file, bool isCode)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileBytes) return null;

            // XML is text. Read it as text.
            if (!isCode) return new[] { File.ReadAllText(file) };

            var bytes = File.ReadAllBytes(file);

            // Literals are UTF-16, decoded at both byte alignments because a literal's offset is
            // not guaranteed to be even relative to the start of the file. The UTF-8 form catches
            // a path that appears as a type or member name instead.
            return new[]
            {
                Decoded(bytes, 0),
                bytes.Length > 1 ? Decoded(bytes, 1) : string.Empty,
                Encoding.UTF8.GetString(bytes),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Mentions(string[] haystacks, string needle, bool isCode)
    {
        // XML paths are matched loosely because modders vary the casing. Code literals are matched
        // exactly, since that is what ContentFinder received.
        var comparison = isCode ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var hay in haystacks)
        {
            if (hay.Length != 0 && hay.Contains(needle, comparison)) return true;
        }

        return false;
    }

    private static string Decoded(byte[] bytes, int offset)
    {
        var length = bytes.Length - offset;
        if (length < 2) return string.Empty;

        // Unicode here means UTF-16LE, which is how the #US heap stores literals.
        return Encoding.Unicode.GetString(bytes, offset, length - (length % 2));
    }

    /// A report for the paths the missing-texture recorder has already collected.
    internal static string Report(IEnumerable<string> paths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Which mod mentions each missing asset");
        sb.AppendLine();

        var list = new List<string>(paths);
        if (list.Count == 0)
        {
            sb.AppendLine("Nothing to scan. Turn on \"Report missing textures\" and load a save first.");
            return sb.ToString();
        }

        // ONE pass over every mod file, all paths tested together.
        var found = FindAll(list);

        foreach (var path in list)
        {
            sb.AppendLine(path);

            var owners = found.TryGetValue(path, out var hits) ? hits : new List<Owner>();
            if (owners.Count == 0)
            {
                sb.AppendLine("    No mod file contains this string.");
                sb.AppendLine("    The path is built at runtime, for example by joining a folder and a file name.");
                sb.AppendLine("    The call stack in the report above is the better clue for this one.");
            }
            else
            {
                foreach (var o in owners)
                    sb.AppendLine($"    {(o.InCode ? "IN CODE" : "IN XML ")}  {o.Mod}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
