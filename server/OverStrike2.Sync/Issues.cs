using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Sync;

/// <summary>One problem of a run for the mail: its key (a problem that persists keeps it, so it is mailed once a day at most), what went
/// wrong and what the sync did about it.</summary>
public sealed record Issue(string Key, string What, string Did);

/// <summary>THE PROBLEM MAIL: the problems of a run's report, and the mail that names them - for problems only, each at most once a
/// day while it persists.</summary>
public static partial class Issues
{
    public const double MailEvery = 20 * 3600;       // a problem that persists is mailed again once this long has passed

    [GeneratedRegex("HTTP (?:429|403|401)")] public static partial Regex PushbackRx();
    [GeneratedRegex("HTTP \\d+|server errors|network failures|cap of [\\d,]+")] private static partial Regex ShortRx();
    [GeneratedRegex("\\d+")] private static partial Regex DigitsRx();
    [GeneratedRegex("deadline|somebody was on|game was updated")] private static partial Regex RoutineStopRx();

    static string S(JsonNode? v) => Node.Str(v);
    static string Get(JsonObject o, string key, string fallback) => o.ContainsKey(key) ? Node.Str(o[key]) : fallback;

    public static string ShortStop(string? text)
    {
        var m = ShortRx().Match(text ?? "");
        return m.Success ? m.Value : Text.Cut(text ?? "", 60);
    }

    /// <summary>THE MAIL'S PROBLEMS (the mail only for issues and errors) of a run's report. What counts: a run stopped early or a step that
    /// errored, Steam pushing back (HTTP 429, 403 or 401; a SteamCMD rate-limit answer or two Access Denied in a row; 3 failed downloads in a
    /// row), SteamCMD stalling twice in a run or on the same map twice in a day (one stall is routine), the test engine failing to start, a
    /// safety check refusing (the game server's deletion, the check's 10% rule, the publish's), the disk running low, a hand-over that
    /// failed, the check's verdicts not reaching the bridge, and any other error. The key groups a problem that persists.</summary>
    public static List<Issue> Of(JsonObject r)
    {
        var out_ = new List<Issue>();
        void Add(string key, string what, string did) => out_.Add(new Issue(key, what, did));
        if (r["api_stop"] is { } apiStop && Node.Truthy(apiStop))
        {
            var api = Node.Str(apiStop);
            Add("steam-webapi", $"Steam's Web API pushed back: {Text.Clean(api, 220)}.",
                $"Every Web API step stopped for the rest of the day{(PushbackRx().IsMatch(api) ? ", and the downloads with them" : "")}; the next day's runs try again by themselves.");
        }
        JsonObject dl = Node.Obj(r["downloads"]), sv = Node.Obj(r["serve"]), t = Node.Obj(r["tests"]);
        foreach (var (name, part) in new[] { ("the test downloads", dl), ("the hand-over downloads", sv) })
        {
            var stop = Node.Truthy(part["stop"]) ? Node.Str(part["stop"]) : "";
            if (stop.Contains("SteamCMD answered", StringComparison.Ordinal) || stop.Contains("no more downloads today", StringComparison.Ordinal))
                Add("steamcmd-pushback", $"SteamCMD pushed back during {name}: {Text.Clean(stop, 220)}.", "No more downloads today; they resume tomorrow by themselves.");
            else if (stop.Contains("in a row failed", StringComparison.Ordinal))
                Add("downloads-in-a-row", $"3 downloads in a row failed during {name}.",
                    "This run downloaded nothing more; the next run tries again, and a map is tried 3 times at most.");
            else if (stop.Contains("did not log in", StringComparison.Ordinal) || stop.Contains("stopped answering", StringComparison.Ordinal))
                Add("steamcmd", $"SteamCMD failed during {name}: {Text.Clean(stop, 220)}.", "This run's downloads stopped; the next run starts SteamCMD again.");
            else if (stop.Contains("hand-overs wait", StringComparison.Ordinal))
                Add("handovers-waiting", $"The game server has not taken in its hand-overs: {Text.Clean(stop, 160)}.",
                    "No more hand-overs until it does; its next engine start takes them in.");
        }
        var stalls = Node.Arr(r["stalls"]).OfType<JsonArray>().Where(s => s.Count == 2).Select(s => (Id: Node.Str(s[0]), N: Node.Float(s[1]), Text: Node.Str(s[1]))).ToList();
        var again = stalls.Where(s => s.N >= 2).ToList();
        if (stalls.Count >= 2 || again.Count > 0)      // one stall is routine: a fresh session took the next map
            Add("steamcmd-stalls", $"SteamCMD stalled {(stalls.Count >= 2 ? "twice in this run" : "on the same map twice today")}: {string.Join(", ", stalls.Take(6).Select(s => $"{s.Id} ({s.Text} today)"))}.",
                stalls.Count >= 2 ? "This run's downloads stopped at the second stall; a map is tried 3 times at most, and the next run starts SteamCMD afresh."
                    : "The next map got a fresh session; that map is tried again later, 3 times at most.");
        if (Node.Truthy(t["engine_failed"]))
            Add("test-engine", $"The test engine did not start: {Text.Clean(Node.Str(t["engine_failed"]), 220)}.",
                "This run's tests stopped; the downloaded maps wait in the staging folder for the next run.");
        else if (Node.Truthy(t["stop"]) && !RoutineStopRx().IsMatch(Node.Str(t["stop"])))
            Add("tests-stopped", $"The tests stopped early: {Text.Clean(Node.Str(t["stop"]), 220)}.", "The maps without a verdict wait for the next run.");
        foreach (var name in new[] { "discovery", "ratings" })
        {
            var skipped = Node.Truthy(Node.Get(r[name], "skipped")) ? Node.Str(Node.Get(r[name], "skipped")) : "";
            if (skipped.StartsWith("failed", StringComparison.Ordinal))
                Add($"{name}-failed", $"The {name} step failed: {Text.Clean(skipped, 200)}.", "The run went on without it; the next one tries again.");
        }
        var verdicts = Node.Truthy(Node.Get(r["published"], "verdicts")) ? Node.Str(Node.Get(r["published"], "verdicts")) : "";
        if (verdicts.StartsWith("the report failed", StringComparison.Ordinal) || verdicts.StartsWith("not reported", StringComparison.Ordinal))
            Add("verdicts-not-reported", $"The Workshop check's verdicts did not reach the bridge: {Text.Clean(verdicts, 220)}.",
                "Maps gone from the Workshop are not blocked until a check reaches it; the next check (every sync.apiEveryHours) reports again.");
        if (Node.Truthy(Node.Get(r["check"], "suspicious")))
            Add("check-not-believed", "More than 10% of the listed maps came back gone from the Workshop - not believed.",
                "Nothing was marked removed; the bridge's own rule refuses it too.");
        var failedHandovers = Node.Arr(sv["handover_failed"]);
        if (failedHandovers.Count > 0)
            Add("handover-failed", $"{failedHandovers.Count} hand-over(s) to the game server failed: "
                + $"{Text.Clean(string.Join("; ", failedHandovers.Take(5).Select(f => $"{S(Node.Arr(f).ElementAtOrDefault(0))} {S(Node.Arr(f).ElementAtOrDefault(1))}")), 300)}.",
                "Each map stays staged; the next run hands it over, or downloads it again.");
        if (Node.Truthy(Node.Get(r["disk"], "low")))
            Add("disk-low", $"The disk is running low: {S(Node.Get(r["disk"], "low"))}.", "Nothing yet - the sync keeps working; make room, or raise the dataset's quota.");
        var wk = Node.Obj(r["watcher_keep"]);
        if (Node.Truthy(wk["problems"]))
        {
            var problems = Node.StrList(wk["problems"]);
            Add($"deletion-refused:{Text.Cut(DigitsRx().Replace(problems[0], "#"), 60)}",
                $"The game server's deletion at its engine start of {Get(wk, "computed", "?")} refused: {Text.Clean(string.Join("; ", problems.Take(3)), 300)}.",
                "It deleted nothing and kept every map; it tries again at its next engine start.");
        }
        foreach (var e in Node.StrList(r["errors"]))
            Add($"error:{Text.Cut(DigitsRx().Replace(e, "#"), 80)}", $"An error: {Text.Clean(e, 300)}.", "The run went on with its other steps.");
        return out_;
    }

    /// <summary>(subject, body) of a problem mail: what went wrong, what the sync did, the dashboard, the run's report.</summary>
    public static (string Subject, string Body) MailText(List<Issue> issues, List<Issue> ongoing, JsonObject r, SyncConfig cfg)
    {
        var when = $"{Get(r, "day", "?")} {Get(r, "started_hm", "?")}-{Get(r, "ended_hm", "?")}";
        var subject = $"{cfg.ServerName} Workshop sync: {Text.Cut(issues[0].What, 90).TrimEnd('.')}{(issues.Count > 1 ? $" (and {issues.Count - 1} more)" : "")}";
        var lines = new List<string>
        {
            $"The Workshop sync of {cfg.ServerName} ran into {(issues.Count == 1 ? "a problem" : $"{issues.Count} problems")} in its {(Node.Truthy(r["manual"]) ? "manual " : "")}run of {when}.", "",
        };
        foreach (var i in issues)
            lines.AddRange([$"What went wrong: {i.What}", $"What the sync did: {i.Did}", ""]);
        if (ongoing.Count > 0)
            lines.AddRange([$"Still going on, mailed already in the last day: {string.Join(" | ", ongoing.Select(i => i.What))}", ""]);
        lines.Add($"The dashboard: {(cfg.DashboardUrl.Length > 0 ? cfg.DashboardUrl : "the HUD's Sync tab")}");
        var report = Node.Truthy(r["report_path"]) ? Node.Str(r["report_path"]) : null;
        lines.Add($"This run's full report: {(report is not null ? report[..^5] + ".txt" : "?")}");
        lines.Add("Runs without a problem send no mail; their reports stay in the reports folder and on the dashboard.");
        return (subject, string.Join("\n", lines));
    }

    /// <summary>Mails what is new among `issues`, once a day at most per problem (&lt;state&gt;/mail-sent.json keeps when each was last sent).
    /// Returns the outcome for the report: "sent", or why not.</summary>
    public static string Mail(SyncConfig cfg, SyncClock clock, List<Issue> issues, JsonObject r, Func<SyncConfig, string, string, string?> send, Action<string> log)
    {
        if (issues.Count == 0)
            return "NOT sent: no problem in this run (the mail is for problems only)";
        var path = Path.Combine(cfg.StateDir, "mail-sent.json");
        var sent = Node.ReadJson(path) as JsonObject ?? [];
        var now = clock.Now();
        var fresh = issues.Where(i => now - Node.Float(sent[i.Key]) >= MailEvery).ToList();
        if (fresh.Count == 0)
            return $"NOT sent: every problem of this run was mailed in the last {Text.Hms(MailEvery)}";
        var ongoing = issues.Where(i => !fresh.Contains(i)).ToList();
        var (subject, body) = MailText(fresh, ongoing, r, cfg);
        var why = send(cfg, subject, body);
        if (why is not null)
            return $"NOT sent: {why}";
        foreach (var i in fresh)
            sent[i.Key] = now;
        foreach (var key in sent.Where(kv => now - Node.Float(kv.Value) > 14 * 86400).Select(kv => kv.Key).ToList())
            sent.Remove(key);
        try
        {
            OverStrike2.Core.Files.WriteAtomic(path, Node.File(sent, 1), FileModes.Private);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"mail: {path} not written ({e.Message})");
        }
        return $"sent: {subject}";
    }

    /// <summary>A run that ended in an exception has no report: its problem is mailed from here, once a day at most.</summary>
    public static string MailCrash(SyncConfig cfg, SyncClock clock, Exception error, bool manual, Func<SyncConfig, string, string, string?> send, Action<string> log)
    {
        var r = new JsonObject { ["day"] = clock.Day(), ["manual"] = manual, ["started_hm"] = "?", ["ended_hm"] = clock.Hm() };
        return Mail(cfg, clock, [new Issue($"run-failed:{error.GetType().Name}", $"The run failed: {Text.Clean(Text.Of(error), 300)}.",
                                                 $"The service went on; it starts the next run after {Text.Hms(Service.IdleWait)}.")], r, send, log);
    }
}
