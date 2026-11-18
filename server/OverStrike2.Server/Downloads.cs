using System.Globalization;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>One event of the Steam client's workshop log: an item requested, its result, or a job's "Update canceled:" line (which comes
/// right before the result of the item that job was for).</summary>
public sealed record DownloadEvent(string Id, string Kind, string Text, string? Cancel = null);

public static partial class Downloads
{
    [GeneratedRegex(@"Download item (\d+) (?:(requested) by app|result : (.*?))\s*$")] private static partial Regex DownloadRx();
    [GeneratedRegex(@"\] Update canceled: (.*?)\s*$")] private static partial Regex CanceledRx();
    [GeneratedRegex("no connection|no download source", RegexOptions.IgnoreCase)] private static partial Regex StuckRx();

    public static List<DownloadEvent> Events(IEnumerable<string> lines)
    {
        var events = new List<DownloadEvent>();
        foreach (var line in lines)
        {
            var m = DownloadRx().Match(line);
            if (m.Success)
            {
                events.Add(new DownloadEvent(m.Groups[1].Value, m.Groups[2].Success ? "requested" : "result", m.Groups[3].Success ? m.Groups[3].Value : ""));
                continue;
            }
            m = CanceledRx().Match(line);
            if (m.Success)
                events.Add(new DownloadEvent("", "canceled", m.Groups[1].Value));
        }
        return events;
    }

    /// <summary>Does a failed download say the process has no download source left - for good? Its result ("No Connection") or the job's
    /// "Update canceled:" line: "Failed downloading 306 manifests (No connection)" (the source pool ran out during the manifest re-check),
    /// "... while downloading chunk ... "No Download Source"" (it ran out during the download), "... while starting download (No
    /// connection to content servers)" (it was empty already).</summary>
    public static bool Stuck(params string?[] texts) => texts.Any(t => StuckRx().IsMatch(t ?? ""));

    /// <summary>A download's two sizes as its line says them, "done of total": both in the whole's unit - MB below a gigabyte, GB from one, binary like SizeWords -
    /// so never "1020 MB of 1.1 GB" (87 % of a 1.14 GB map in the live lab's final round of release 218, review point 46) but "0.99 GB of 1.14 GB". The whole is
    /// rounded and the part rounded down, so the part never says more than has come: two decimals in GB, none in MB, and while the two would still read alike
    /// before the end ("1.14 GB of 1.14 GB" with 3 MB to go) one decimal more for both ("1.141 GB of 1.145 GB"); past that the part reads one step under the
    /// whole, so the words stay short in the window's clock. At the end, the part as large as the whole, both read the same. The whole is never under one step,
    /// as SizeWords' 1 MB.</summary>
    public static (string Done, string Total) SizePair(double done, double total)
    {
        var gb = total >= 1L << 30;
        var unit = (double)(gb ? 1L << 30 : 1L << 20);
        var (d, t) = (Math.Clamp(done, 0, total) / unit, total / unit);
        var first = gb ? 2 : 0;
        for (var places = first; ; places++)
        {
            var scale = Math.Pow(10, places);
            var whole = Math.Max(1, Math.Round(t * scale, MidpointRounding.ToEven));
            if (done >= total) return (Steps(whole, places, gb), Steps(whole, places, gb));
            var part = Math.Min(whole, Math.Floor(d * scale + 1e-9)); // the epsilon: 1.15 GB is 1.1499999... in a double
            if (part < whole) return (Steps(part, places, gb), Steps(whole, places, gb));
            if (places == first + 1) return (Steps(Math.Max(0, whole - 1), places, gb), Steps(whole, places, gb));
        }
    }

    /// <summary>A whole number of steps of 10^-places of the unit, in words: "0.99 GB", "155 MB".</summary>
    static string Steps(double steps, int places, bool gb) =>
        (steps / Math.Pow(10, places)).ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + (gb ? " GB" : " MB");

    /// <summary>A map's size as the HUD shows it (the lists' sizeMb): binary units, labelled MB and GB.</summary>
    public static string SizeWords(double bytes) => bytes >= 1L << 30
        ? (bytes / (1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : Math.Max(1, Math.Round(bytes / (1L << 20), MidpointRounding.ToEven)).ToString("0", CultureInfo.InvariantCulture) + " MB";
}


/// <summary>The background downloads and the download line, a state machine stepped once per watcher tick - never blocking it for longer
/// than one RCON command or one report to the bridge. And the download restart's mark: a process whose downloads say it has no download
/// source left is broken for good.</summary>
public sealed class WorkshopJobs
{
    public Watch Watch { get; }
    public DownloadLine Line { get; }
    public ReadyMaps Ready { get; }
    public Tail Tail { get; }
    public (double Since, string Why)? Broken { get; set; }    // downloads are failing in this engine process: a restart is due
    int? pidSeen;
    string? cancel;                                            // the last "Update canceled:" reason, until its result line

    public WorkshopJobs(Watch watch)
    {
        Watch = watch;
        Line = new DownloadLine(watch.Config, watch.Engine, watch.Log) { Staged = Staged };
        Ready = new ReadyMaps(this);
        Tail = new Tail(watch.Config.Paths.WorkshopLog);
    }

    /// <summary>Bytes of this item in the cache's staging folders.</summary>
    long Staged(string id)
    {
        long total = 0;
        foreach (var stage in new[] { "downloads", "temp" })
        {
            var root = Path.Combine(Watch.Config.Paths.WorkshopDir, stage);
            if (!Directory.Exists(root))
                continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    var rel = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                    if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(id))
                        try
                        {
                            total += new FileInfo(file).Length;
                        }
                        catch (IOException)
                        {
                        }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return total;
    }

    /// <summary>The downloads nobody waits for: the background download - unless the engine itself waits for it (a pick of the very map it was fetching:
    /// then it is the player's download, and the line shows it).</summary>
    public HashSet<string> Own()
    {
        var own = new HashSet<string>();
        if (Ready.Current is { } c && !(Line.ActiveDownloads.TryGetValue(c.Id, out var e) && e.Engine))
            own.Add(c.Id);
        return own;
    }

    public void MarkBroken(double now, string why)
    {
        if (Broken is not null)
            return;
        Broken = (now, why);
        Watch.Log.Line($"WARNING: workshop downloads are broken in this engine process ({why}) - a restart is due as soon as nobody is on (at most one per {Words.Hm(Rules.WorkshopRestartGap)}, {Rules.WorkshopRestartMax} per 24 h)");
        Snapshots.SteamLogs(Watch, why, Watch.ArmedPid);   // before any restart: a recreate would lose them
    }

    public void Step(double now)
    {
        var events = new List<DownloadEvent>();
        foreach (var e in Downloads.Events(Tail.Lines()))
        {
            if (e.Kind == "canceled")
                cancel = e.Text;                     // the job's reason, attached to the result that follows it
            else if (e.Kind == "result")
            {
                events.Add(e with { Cancel = cancel });
                cancel = null;
            }
            else
                events.Add(e);
        }
        if (Watch.ArmedPid is { } pid && pid != pidSeen)
        {
            pidSeen = pid;
            if (Broken is not null)
                Watch.Log.Line($"workshop downloads: engine pid {pid} is a new process - the broken flag is cleared");
            Broken = null;
            Line.Reset();
            Ready.Reset();
        }
        if (Broken is not null)
            foreach (var e in events)
                if (e.Kind == "result" && e.Text.Trim().ToUpperInvariant() == "OK")
                {
                    Watch.Log.Line($"workshop downloads work again (item {e.Id} OK) - the download restart is no longer due");
                    Broken = null;
                    break;
                }
        StepPlayers(now, events);
        Ready.Step(now, events);
    }

    /// <summary>Every download that is not a background download: the download line, and the download restart's mark for it.</summary>
    void StepPlayers(double now, List<DownloadEvent> events)
    {
        var own = Own();
        foreach (var e in events)
        {
            if (e.Id.Length == 0 || own.Contains(e.Id))
                continue;
            if (e.Kind == "requested")
                Line.Started(e.Id, now);
            else if (e.Text.Trim().ToUpperInvariant() == "OK")
                Line.Finished(e.Id);
            else
            {
                Line.Failed(e.Id, e.Text);
                if (Downloads.Stuck(e.Text, e.Cancel))
                    MarkBroken(now, $"a download for players ({e.Id}) came back '{e.Text}'");
            }
        }
        Line.Step(now, Watch.A2sInfo?.Humans ?? 0, own);
    }
}
