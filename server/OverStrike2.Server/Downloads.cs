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
