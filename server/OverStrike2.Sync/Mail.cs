using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Sync;

/// <summary>One problem of a run for the mail: its key (a problem that persists keeps it, so it is mailed once a day at most), what went
/// wrong and what the sync did about it.</summary>
public sealed record Issue(string Key, string What, string Did);

/// <summary>5b. THE PROBLEM MAIL: for problems only. A run's summary (its report's text), the problems of a report, the mail that names them,
/// and a plain SMTP client (STARTTLS when the relay offers it).</summary>
public static partial class Mail
{
    public const int MailRows = 40;                  // past this many lines of a kind the mail gives counts; the report keeps every line
    public const double MailEvery = 20 * 3600;       // a problem that persists is mailed again once this long has passed

    [GeneratedRegex("HTTP (?:429|403|401)")] public static partial Regex PushbackRx();
    [GeneratedRegex("HTTP \\d+|server errors|network failures|cap of [\\d,]+")] private static partial Regex ShortRx();
    [GeneratedRegex("\\d+")] private static partial Regex DigitsRx();
    [GeneratedRegex("deadline|somebody was on|game was updated")] private static partial Regex RoutineStopRx();

    public static string ShortStop(string? text)
    {
        var m = ShortRx().Match(text ?? "");
        return m.Success ? m.Value : Sync.Text.Cut(text ?? "", 60);
    }

    static string S(JsonNode? v) => Node.Str(v);
    static long I(JsonNode? v) => Node.Int(v);
    static string Or(JsonNode? v, string fallback) => Node.Truthy(v) ? Node.Str(v) : fallback;
    static string Get(JsonObject o, string key, string fallback) => o.ContainsKey(key) ? Node.Str(o[key]) : fallback;
    static long N(JsonObject o, string key) => o.ContainsKey(key) ? Node.Int(o[key]) : 0;

    /// <summary>A run's publish steps as one: what each batch's publish reported, added up, for the report and the mail.</summary>
    public static JsonObject? MergePublished(JsonObject? before, JsonObject? now)
    {
        if (before is null || before.Count == 0)
            return now;
        var out_ = (JsonObject)before.DeepClone();
        foreach (var (key, value) in now ?? [])
        {
            if (value is JsonArray list)
            {
                var have = Node.Arr(out_[key]).Select(x => x?.DeepClone()).ToList();
                if (key == "files")
                    have.AddRange(list.Where(x => !have.Any(h => Node.Eq(h, x))).Select(x => x?.DeepClone()));
                else
                    have.AddRange(list.Select(x => x?.DeepClone()));
                out_[key] = new JsonArray(have.ToArray());
            }
            else if (key == "modes" && value is JsonObject modes)
            {
                var merged = Node.Obj(out_["modes"]?.DeepClone());
                foreach (var (mode, rows) in modes)
                    merged[mode] = new JsonArray(Node.Arr(merged[mode]).Select(x => x?.DeepClone()).Concat(Node.Arr(rows).Select(x => x?.DeepClone())).ToArray());
                out_["modes"] = merged;
            }
            else if (key == "backfill" && value is JsonObject fill)
            {
                var merged = Node.Obj(out_["backfill"]?.DeepClone());
                foreach (var (mode, n) in fill)
                    merged[mode] = Node.Int(merged[mode]) + Node.Int(n);
                out_["backfill"] = merged;
            }
            else if (!(value is null || (Node.IsString(value) && value.GetValue<string>().Length == 0) || (value is JsonObject o && o.Count == 0)))
                out_[key] = value.DeepClone();
        }
        return out_;
    }

    /// <summary>A run's summary (its report's text, and the body of the run's report file), from its report `r`.</summary>
    public static (string Subject, string Body) Text(JsonObject r, SyncConfig cfg)
    {
        var lines = new List<string>();
        void W(string s) => lines.Add(s);
        var stop = r["api_stop"] is { } st && Node.Truthy(st) ? Node.Str(st) : null;
        var head = new List<string>();
        var pub = Node.Obj(r["published"]);
        var listedRows = Node.Arr(pub["listed"]);
        if (listedRows.Count > 0)
            head.Add($"{listedRows.Count} new Arms Race map{(listedRows.Count == 1 ? "" : "s")} listed");
        foreach (var mode in new[] { "deathmatch", "wingman" })
        {
            var n = Node.Arr(Node.Get(pub["modes"], mode)).Count;
            if (n > 0)
                head.Add($"{n} new {char.ToUpperInvariant(mode[0])}{mode[1..]} map{(n == 1 ? "" : "s")}");
        }
        var nb = Node.Arr(pub["broken"]).Count + Node.Arr(pub["packs"]).Count;
        if (nb > 0)
            head.Add($"{nb} on broken.json");
        if (Node.Arr(pub["removed"]).Count > 0)
            head.Add($"{Node.Arr(pub["removed"]).Count} gone from the Workshop");
        var manual = Node.Truthy(r["manual"]);
        var subject = $"{cfg.ServerName} Workshop sync {S(r["day"])}{(manual ? " (manual run)" : $" (the run of {Get(r, "started_hm", "?")})")}: "
                      + $"{(head.Count > 0 ? string.Join(", ", head) : "nothing new")}{(stop is not null ? $" - STOPPED: {ShortStop(stop)}" : "")}";
        W($"{cfg.ServerName} - the Workshop sync, a run of {S(r["day"])} ({Get(r, "started_hm", "?")} to {Get(r, "ended_hm", "?")}). It runs whenever nobody plays; one mail a day, the reports of every run in");
        W("its reports folder.");
        W("");
        if (stop is not null)
        {
            W($"STOPPED: {stop}");
            W("Every Web API step stopped for the rest of the day at that moment (any 429, 403 or burst of");
            W("server errors). The next day starts again from the ledger's counts.");
            W("");
        }
        foreach (var line in Node.Arr(r["notes"]))
            W($"NOTE: {S(line)}");
        if (Node.Truthy(r["notes"]))
            W("");
        var d = Node.Obj(r["discovery"]);
        W("1. Find new maps (IPublishedFileService/QueryFiles, the Web API key):");
        if (Node.Truthy(d["skipped"]))
            W($"   skipped: {S(d["skipped"])}");
        else
            W($"   {N(d, "pages")} page(s), {N(d, "seen")} item(s) seen, {Node.Arr(d["new"]).Count} new ({Or(d["stop"], "-")}); the Workshop holds {Or(d["total"], "?")} items with these tags.");
        var c = Node.Obj(r["check"]);
        W("2. Check the listed maps (ISteamRemoteStorage/GetPublishedFileDetails, no key):");
        if (Node.Truthy(c["skipped"]))
            W($"   skipped: {S(c["skipped"])}");
        else
        {
            W($"   {N(c, "asked")} id(s) asked in {N(c, "calls")} call(s): {Node.Arr(c["removed"]).Count} gone from the Workshop, {Node.Arr(c["changed"]).Count} changed since last seen"
              + $"{(Node.Truthy(c["left"]) ? $", {S(c["left"])} not answered" : "")}.");
            if (Node.Truthy(c["suspicious"]))
                W("   NOT BELIEVED: more than 10% of the maps came back gone - nothing was marked removed in this run.");
        }
        var rt = Node.Obj(r["ratings"]);
        W("2b. Ratings (IPublishedFileService/GetDetails with vote_data, the Web API key; the keep set's top lists):");
        if (Node.Truthy(rt["skipped"]))
            W($"   {S(rt["skipped"])}.");
        else if (rt.Count > 0)
            W($"   {N(rt, "scored")} of {N(rt, "asked")} scored in {N(rt, "calls")} call(s): {N(rt, "listed")} listed, {N(rt, "waiting")} waiting for their test"
              + $"{(Node.Truthy(rt["complete"]) ? "" : $" - INCOMPLETE ({(Node.Truthy(rt["stop"]) ? S(rt["stop"]) : $"{N(rt, "failed")} failed")}), the old scores' time kept")}.");
        var sv = Node.Obj(r["serve"]);
        W("2c. For the game server (the keep set's maps it does not store, downloaded and handed over; it takes them in at its next start):");
        if (Node.Truthy(sv["skipped"]))
            W($"   skipped: {S(sv["skipped"])}");
        else if (sv.Count > 0)
            W($"   {N(sv, "missing")} missing of the keep set, {N(sv, "ok")} handed over ({Sync.Text.Mb(N(sv, "bytes"))}), {N(sv, "tested")} tested in this run and handed over, "
              + $"{Node.Arr(sv["failed"]).Count} failed, {N(sv, "left")} still missing{(Node.Truthy(sv["stop"]) ? $"; stopped: {S(sv["stop"])}" : "")}.");
        if (Node.Truthy(r["requeued"]))
            W($"   {S(r["requeued"])} map(s) skipped as too big for an older download cap are queued again (they fit today's).");
        var dl = Node.Obj(r["downloads"]);
        W("3. Downloads (SteamCMD, anonymous login):");
        if (Node.Truthy(dl["skipped"]))
            W($"   skipped: {S(dl["skipped"])}");
        else
        {
            var failed = Node.Arr(dl["failed"]);
            var firstFails = string.Join("; ", failed.Take(5).Select(f => $"{S(Node.Arr(f).ElementAtOrDefault(0))}: {S(Node.Arr(f).ElementAtOrDefault(1))}"));
            W($"   {N(dl, "ok")} downloaded ({Sync.Text.Mb(N(dl, "bytes"))}), {failed.Count} failed{(failed.Count > 0 ? $" ({firstFails})" : "")}; {N(dl, "queued_left")} still queued for later runs.");
            if (Node.Truthy(dl["stop"]))
                W($"   stopped: {S(dl["stop"])}");
        }
        var t = Node.Obj(r["tests"]);
        W("4. Scan and load test (a test copy of the server, no plugins, its own anonymous logon):");
        if (Node.Truthy(t["skipped"]))
            W($"   skipped: {S(t["skipped"])}");
        var rows = Node.StrList(t["rows"]);
        var passes = rows.Where(x => x.StartsWith("PASS", StringComparison.Ordinal)).ToList();
        if (rows.Count > MailRows)                   // a long run: the counts, what did not pass, a taste of what did; the report has them all
        {
            var counts = new List<(string Word, int N)>();
            foreach (var row in rows)
            {
                var word = OverStrike2.Core.SearchText.Split(row).FirstOrDefault() ?? "";
                var at = counts.FindIndex(x => x.Word == word);
                if (at >= 0)
                    counts[at] = (word, counts[at].N + 1);
                else
                    counts.Add((word, 1));
            }
            W($"   {string.Join(", ", counts.OrderBy(x => -x.N).Select(x => $"{x.N} {x.Word}"))}.");
            foreach (var row in rows.Where(x => !x.StartsWith("PASS", StringComparison.Ordinal)).Take(MailRows))
                W($"   {row}");
            foreach (var row in passes.Take(10))
                W($"   {row}");
            if (passes.Count > 10)
                W($"   ... and {passes.Count - 10} more passes (all in the report).");
        }
        else
            foreach (var row in rows)
                W($"   {row}");
        if (Node.Truthy(t["stop"]))
            W($"   stopped: {S(t["stop"])}");
        W("5. Published into the bridge's data:");
        if (Node.Truthy(pub["skipped"]))
            W($"   skipped: {S(pub["skipped"])}");
        else if (!Node.Truthy(pub["files"]) && !Node.Truthy(pub["verdicts"]))
            W("   nothing to publish.");
        else
        {
            foreach (var row in listedRows.Take(MailRows).Select(Node.Arr))
                W($"   maps.json  #{S(row.ElementAtOrDefault(1))} {S(row.ElementAtOrDefault(2))} ({S(row.ElementAtOrDefault(0))}, {I(row.ElementAtOrDefault(3))} MB)");
            if (listedRows.Count > MailRows)
                W($"   maps.json  ... and {listedRows.Count - MailRows} more");
            foreach (var (mode, modeRows) in Node.Obj(pub["modes"]).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var fname = mode == "deathmatch" ? "maps-deathmatch.json" : "maps-wingman.json";
                var list = Node.Arr(modeRows);
                if (list.Count > MailRows / 2)
                {
                    W($"   {fname}  {list.Count} new maps (all in the report)");
                    continue;
                }
                foreach (var row in list.Select(Node.Arr))
                    W($"   {fname}  {S(row.ElementAtOrDefault(1))} ({S(row.ElementAtOrDefault(0))})");
            }
            foreach (var (mode, n) in Node.Obj(pub["backfill"]).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                W($"   {(mode == "deathmatch" ? "maps-deathmatch.json" : "maps-wingman.json")}: {I(n)} listed Arms Race map(s) that carry the {char.ToUpperInvariant(mode[0])}{mode[1..]} tag");
            foreach (var row in Node.Arr(pub["broken"]).Select(Node.Arr))
                W($"   broken.json maps   {S(row.ElementAtOrDefault(1))} ({S(row.ElementAtOrDefault(0))}): {S(row.ElementAtOrDefault(3))}");
            var packs = Node.Arr(pub["packs"]);
            foreach (var row in packs.Take(MailRows).Select(Node.Arr))
                W($"   broken.json packs  {S(row.ElementAtOrDefault(1))} ({S(row.ElementAtOrDefault(0))}): {S(row.ElementAtOrDefault(2))}");
            if (packs.Count > MailRows)
                W($"   broken.json packs  ... and {packs.Count - MailRows} more");
            foreach (var row in Node.Arr(pub["removed"]).Select(Node.Arr))
                W($"   gone from the Workshop  #{S(row.ElementAtOrDefault(1))} {S(row.ElementAtOrDefault(2))} ({S(row.ElementAtOrDefault(0))}): {S(row.ElementAtOrDefault(3))}");
            if (Node.Truthy(pub["verdicts"]))
                W($"   the check's verdicts, handed to the bridge (POST /removed): {S(pub["verdicts"])}");
            W($"   files: {string.Join(", ", Node.StrList(pub["files"]))}; bridge: {Or(pub["reload"], "not asked")}");
            if (listedRows.Count > 0)
            {
                W("   The new maps have no size class yet (sizes.json is measured on the PC) and no picture until the next HUD");
                W("   content update.");
            }
        }
        var lim = Node.Obj(r["limits"]);
        W("");
        W($"Limits: Web API calls this run {Get(lim, "api_run", "None")} of {Get(lim, "api_run_cap", "None")}, last 24 h {Get(lim, "api_24h", "None")} of {Get(lim, "api_day_cap", "None")} "
          + $"(10% of Valve's 100,000), at least {Sync.Text.Fixed(lim.ContainsKey("api_gap") ? Node.Float(lim["api_gap"]) : 2, 0)} s apart;");
        W($"downloads today {Get(lim, "dl_items", "None")}, {Sync.Text.Mb(Node.Int(lim["dl_bytes"]))}{(Node.Truthy(lim["dl_engine"]) ? $" (with {I(lim["dl_engine"])} copies the test engine fetched itself)" : "")} "
          + $"- no daily cap: only Steam's push-back stops them; test-engine starts {Get(lim, "engine_starts", "None")} of {Get(lim, "engine_starts_max", "None")}.");
        var bl = Node.Obj(r["backlog"]);
        if (Node.Truthy(bl["items"]))
        {
            var eta = Or(r["backlog_until"], "");
            var byMode = Node.Obj(bl["by_mode"]);
            W($"Backlog: {I(bl["items"])} item(s) waiting, {Sync.Text.Mb(Node.Int(bl["bytes"]))} (Deathmatch {N(byMode, "deathmatch")}, Wingman {N(byMode, "wingman")}, Arms Race {N(byMode, "armsrace")} - one item can carry several tags), taken");
            W($"the ones the keep set wants first, new uploads next, then strictly by subscribers. About {N(bl, "days")} more day(s){eta}: {Sync.Text.Fixed(bl.ContainsKey("pace") ? Node.Float(bl["pace"]) : Verdicts.PaceDefault, 0)} s a map");
            W($"(download and test), about {Sync.Text.Fixed(Node.Float(bl["hours"]), 0)} h a day while nobody plays (no time windows), so about {(Node.Integer(bl["per_day"]) is { } pd ? pd : 0)} a day; every hour somebody");
            W("plays comes off it. No daily download cap: only time and the test speed set it.");
        }
        if (Node.Truthy(r["waited"]))
            W($"Waited: {string.Join("; ", Node.StrList(r["waited"]))}.");
        if (Node.Truthy(r["errors"]))
        {
            W("");
            foreach (var e in Node.StrList(r["errors"]))
                W($"ERROR: {e}");
        }
        W("");
        W($"The full report: {Get(r, "report_path", "?")}");
        return (subject, string.Join("\n", lines));
    }

    /// <summary>THE MAIL'S PROBLEMS (the mail only for issues and errors) of a run's report. What counts: a run stopped early or a step that
    /// errored, Steam pushing back (HTTP 429, 403 or 401; a SteamCMD rate-limit answer or two Access Denied in a row; 3 failed downloads in a
    /// row), SteamCMD stalling twice in a run or on the same map twice in a day (one stall is routine), the test engine failing to start, a
    /// safety check refusing (the game server's deletion, the check's 10% rule, the publish's), the disk running low, a hand-over that
    /// failed, the check's verdicts not reaching the bridge, and any other error. The key groups a problem that persists.</summary>
    public static List<Issue> IssuesOf(JsonObject r)
    {
        var out_ = new List<Issue>();
        void Add(string key, string what, string did) => out_.Add(new Issue(key, what, did));
        if (r["api_stop"] is { } apiStop && Node.Truthy(apiStop))
        {
            var api = Node.Str(apiStop);
            Add("steam-webapi", $"Steam's Web API pushed back: {Sync.Text.Clean(api, 220)}.",
                $"Every Web API step stopped for the rest of the day{(PushbackRx().IsMatch(api) ? ", and the downloads with them" : "")}; the next day's runs try again by themselves.");
        }
        JsonObject dl = Node.Obj(r["downloads"]), sv = Node.Obj(r["serve"]), t = Node.Obj(r["tests"]);
        foreach (var (name, part) in new[] { ("the test downloads", dl), ("the hand-over downloads", sv) })
        {
            var stop = Node.Truthy(part["stop"]) ? Node.Str(part["stop"]) : "";
            if (stop.Contains("SteamCMD answered", StringComparison.Ordinal) || stop.Contains("no more downloads today", StringComparison.Ordinal))
                Add("steamcmd-pushback", $"SteamCMD pushed back during {name}: {Sync.Text.Clean(stop, 220)}.", "No more downloads today; they resume tomorrow by themselves.");
            else if (stop.Contains("in a row failed", StringComparison.Ordinal))
                Add("downloads-in-a-row", $"3 downloads in a row failed during {name}.",
                    "This run downloaded nothing more; the next run tries again, and a map is tried 3 times at most.");
            else if (stop.Contains("did not log in", StringComparison.Ordinal) || stop.Contains("stopped answering", StringComparison.Ordinal))
                Add("steamcmd", $"SteamCMD failed during {name}: {Sync.Text.Clean(stop, 220)}.", "This run's downloads stopped; the next run starts SteamCMD again.");
            else if (stop.Contains("hand-overs wait", StringComparison.Ordinal))
                Add("handovers-waiting", $"The game server has not taken in its hand-overs: {Sync.Text.Clean(stop, 160)}.",
                    "No more hand-overs until it does; its next engine start takes them in.");
        }
        var stalls = Node.Arr(r["stalls"]).OfType<JsonArray>().Where(s => s.Count == 2).Select(s => (Id: Node.Str(s[0]), N: Node.Float(s[1]), Text: Node.Str(s[1]))).ToList();
        var again = stalls.Where(s => s.N >= 2).ToList();
        if (stalls.Count >= 2 || again.Count > 0)      // one stall is routine: a fresh session took the next map
            Add("steamcmd-stalls", $"SteamCMD stalled {(stalls.Count >= 2 ? "twice in this run" : "on the same map twice today")}: {string.Join(", ", stalls.Take(6).Select(s => $"{s.Id} ({s.Text} today)"))}.",
                stalls.Count >= 2 ? "This run's downloads stopped at the second stall; a map is tried 3 times at most, and the next run starts SteamCMD afresh."
                    : "The next map got a fresh session; that map is tried again later, 3 times at most.");
        if (Node.Truthy(t["engine_failed"]))
            Add("test-engine", $"The test engine did not start: {Sync.Text.Clean(Node.Str(t["engine_failed"]), 220)}.",
                "This run's tests stopped; the downloaded maps wait in the staging folder for the next run.");
        else if (Node.Truthy(t["stop"]) && !RoutineStopRx().IsMatch(Node.Str(t["stop"])))
            Add("tests-stopped", $"The tests stopped early: {Sync.Text.Clean(Node.Str(t["stop"]), 220)}.", "The maps without a verdict wait for the next run.");
        foreach (var name in new[] { "discovery", "ratings" })
        {
            var skipped = Node.Truthy(Node.Get(r[name], "skipped")) ? Node.Str(Node.Get(r[name], "skipped")) : "";
            if (skipped.StartsWith("failed", StringComparison.Ordinal))
                Add($"{name}-failed", $"The {name} step failed: {Sync.Text.Clean(skipped, 200)}.", "The run went on without it; the next one tries again.");
        }
        var verdicts = Node.Truthy(Node.Get(r["published"], "verdicts")) ? Node.Str(Node.Get(r["published"], "verdicts")) : "";
        if (verdicts.StartsWith("the report failed", StringComparison.Ordinal) || verdicts.StartsWith("not reported", StringComparison.Ordinal))
            Add("verdicts-not-reported", $"The Workshop check's verdicts did not reach the bridge: {Sync.Text.Clean(verdicts, 220)}.",
                "Maps gone from the Workshop are not blocked until a check reaches it; the next check (every sync.apiEveryHours) reports again.");
        if (Node.Truthy(Node.Get(r["check"], "suspicious")))
            Add("check-not-believed", "More than 10% of the listed maps came back gone from the Workshop - not believed.",
                "Nothing was marked removed; the bridge's own rule refuses it too.");
        var failedHandovers = Node.Arr(sv["handover_failed"]);
        if (failedHandovers.Count > 0)
            Add("handover-failed", $"{failedHandovers.Count} hand-over(s) to the game server failed: "
                + $"{Sync.Text.Clean(string.Join("; ", failedHandovers.Take(5).Select(f => $"{S(Node.Arr(f).ElementAtOrDefault(0))} {S(Node.Arr(f).ElementAtOrDefault(1))}")), 300)}.",
                "Each map stays staged; the next run hands it over, or downloads it again.");
        if (Node.Truthy(Node.Get(r["disk"], "low")))
            Add("disk-low", $"The disk is running low: {S(Node.Get(r["disk"], "low"))}.", "Nothing yet - the sync keeps working; make room, or raise the dataset's quota.");
        var wk = Node.Obj(r["watcher_keep"]);
        if (Node.Truthy(wk["problems"]))
        {
            var problems = Node.StrList(wk["problems"]);
            Add($"deletion-refused:{Sync.Text.Cut(DigitsRx().Replace(problems[0], "#"), 60)}",
                $"The game server's deletion at its engine start of {Get(wk, "computed", "?")} refused: {Sync.Text.Clean(string.Join("; ", problems.Take(3)), 300)}.",
                "It deleted nothing and kept every map; it tries again at its next engine start.");
        }
        foreach (var e in Node.StrList(r["errors"]))
            Add($"error:{Sync.Text.Cut(DigitsRx().Replace(e, "#"), 80)}", $"An error: {Sync.Text.Clean(e, 300)}.", "The run went on with its other steps.");
        return out_;
    }

    /// <summary>(subject, body) of a problem mail: what went wrong, what the sync did, the dashboard, the run's report.</summary>
    public static (string Subject, string Body) IssueMail(List<Issue> issues, List<Issue> ongoing, JsonObject r, SyncConfig cfg)
    {
        var when = $"{Get(r, "day", "?")} {Get(r, "started_hm", "?")}-{Get(r, "ended_hm", "?")}";
        var subject = $"{cfg.ServerName} Workshop sync: {Sync.Text.Cut(issues[0].What, 90).TrimEnd('.')}{(issues.Count > 1 ? $" (and {issues.Count - 1} more)" : "")}";
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
    public static string MailIssues(SyncConfig cfg, SyncClock clock, List<Issue> issues, JsonObject r, Func<SyncConfig, string, string, string?> send, Action<string> log)
    {
        if (issues.Count == 0)
            return "NOT sent: no problem in this run (the mail is for problems only)";
        var path = Path.Combine(cfg.StateDir, "mail-sent.json");
        var sent = Node.ReadJson(path) as JsonObject ?? [];
        var now = clock.Now();
        var fresh = issues.Where(i => now - Node.Float(sent[i.Key]) >= MailEvery).ToList();
        if (fresh.Count == 0)
            return $"NOT sent: every problem of this run was mailed in the last {Sync.Text.Hms(MailEvery)}";
        var ongoing = issues.Where(i => !fresh.Contains(i)).ToList();
        var (subject, body) = IssueMail(fresh, ongoing, r, cfg);
        var why = send(cfg, subject, body);
        if (why is not null)
            return $"NOT sent: {why}";
        foreach (var i in fresh)
            sent[i.Key] = now;
        foreach (var key in sent.Where(kv => now - Node.Float(kv.Value) > 14 * 86400).Select(kv => kv.Key).ToList())
            sent.Remove(key);
        try
        {
            OverStrike2.Core.Files.WriteAtomic(path, Node.DumpsFile(sent, 1), FileModes.Private);
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
        return MailIssues(cfg, clock, [new Issue($"run-failed:{error.GetType().Name}", $"The run failed: {Sync.Text.Clean(Sync.Text.Of(error), 300)}.",
                                                 $"The service went on; it starts the next run after {Sync.Text.Hms(Service.IdleWait)}.")], r, send, log);
    }

    // ------------------------------------------------------------------------------------------------ SMTP

    /// <summary>A header's text as RFC 2047 wants it: printable ASCII as it is, anything else as UTF-8 encoded words on folded lines, each short enough for
    /// a line of 76 behind "Subject: " and never splitting a character. A line break in the text cannot start a header of its own.</summary>
    public static string HeaderText(string text)
    {
        const int WordBytes = 39;                           // 52 characters of Base64 (no padding but at the end), 64 with its marks
        var clean = text.Replace('\r', ' ').Replace('\n', ' ');
        if (clean.All(c => c is >= ' ' and <= '~'))
            return clean;
        var words = new List<string>();
        var word = new List<byte>();
        Span<byte> one = stackalloc byte[4];
        foreach (var rune in clean.EnumerateRunes())
        {
            var n = rune.EncodeToUtf8(one);
            if (word.Count + n > WordBytes)
            {
                words.Add($"=?utf-8?B?{Convert.ToBase64String(word.ToArray())}?=");
                word.Clear();
            }
            word.AddRange(one[..n]);
        }
        words.Add($"=?utf-8?B?{Convert.ToBase64String(word.ToArray())}?=");
        return string.Join("\r\n ", words);
    }

    /// <summary>Plain-text mail through the SMTP relay (STARTTLS when it is offered, else plain; a TLS failure is tried again plain). Returns
    /// null when sent, else the reason.</summary>
    public static string? Send(SyncConfig cfg, string subject, string body)
    {
        if (cfg.SmtpHost.Length == 0 || cfg.MailFrom.Length == 0 || cfg.MailTo.Count == 0)
            return "no SMTP host, sender or recipient configured";
        var msg = $"From: {cfg.MailFrom}\r\nTo: {string.Join(", ", cfg.MailTo)}\r\nSubject: {HeaderText(subject)}\r\nDate: {DateTimeOffset.Now.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture)}{Offset(DateTimeOffset.Now.Offset)}\r\n"
                  + $"Message-ID: <{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{Environment.ProcessId}.{Random.Shared.Next()}.os2-sync@{Environment.MachineName.ToLowerInvariant()}>\r\n"
                  + $"MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\nX-Mailer: {SteamApi.UserAgent}\r\n\r\n{body.Replace("\n", "\r\n")}\r\n";
        var sender = Address(cfg.MailFrom);
        string? last = null;
        foreach (var first in new[] { true, false })
        {
            var tls = first;
            try
            {
                Smtp(cfg.SmtpHost, cfg.SmtpPort, ref tls, sender, cfg.MailTo.Select(Address).ToList(), msg);
                return null;
            }
            catch (Exception e) when (e is IOException or SocketException or SmtpException or System.Security.Authentication.AuthenticationException or TimeoutException
                                          or AggregateException)
            {
                last = $"{(e is AggregateException { InnerException: { } inner } ? inner : e).GetType().Name}: {(e is AggregateException { InnerException: { } i2 } ? i2 : e).Message}";
                if (!tls)
                    break;                           // plain already (no STARTTLS offered): no second try
            }
        }
        return last;
    }

    static string Offset(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("00", CultureInfo.InvariantCulture) + Math.Abs(o.Minutes).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>The address of "Name &lt;a@b&gt;", or the text itself.</summary>
    public static string Address(string s)
    {
        var a = s.IndexOf('<');
        var b = s.LastIndexOf('>');
        return a >= 0 && b > a ? s[(a + 1)..b].Trim() : s.Trim();
    }

    /// <summary>An SMTP answer that is not the one wanted.</summary>
    public sealed class SmtpException(string message) : Exception(message);

    /// <summary>One SMTP session: EHLO, STARTTLS when asked for and offered (then EHLO again; `tls` false afterwards when it was not offered),
    /// MAIL FROM, RCPT TO, DATA (dot-stuffed), QUIT.</summary>
    public static void Smtp(string host, int port, ref bool tls, string from, List<string> to, string message, int timeoutMs = 60000)
    {
        using var tcp = new TcpClient { ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
        if (!tcp.ConnectAsync(host, port).Wait(timeoutMs))
            throw new TimeoutException($"no connection to {host}:{port}");
        Stream stream = tcp.GetStream();
        var reader = new SmtpReader(stream);
        void Say(string line) => stream.Write(Encoding.UTF8.GetBytes(line + "\r\n"));
        (int Code, List<string> Lines) Expect(params int[] codes)
        {
            var reply = reader.Reply();
            if (!codes.Contains(reply.Code))
                throw new SmtpException($"({reply.Code}, '{string.Join(" ", reply.Lines)}')");
            return reply;
        }
        Expect(220);
        var hostname = Environment.MachineName.ToLowerInvariant();
        Say($"EHLO {hostname}");
        var ehlo = Expect(250);
        if (tls)
        {
            if (!ehlo.Lines.Any(l => l.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                tls = false;
            else
            {
                Say("STARTTLS");
                Expect(220);
                var ssl = new SslStream(stream, false, (_, _, _, _) => true);   // a relay inside the deployment: its certificate is not checked
                ssl.AuthenticateAsClient(host);
                stream = ssl;
                reader = new SmtpReader(stream);
                Say($"EHLO {hostname}");
                Expect(250);
            }
        }
        Say($"MAIL FROM:<{from}>");
        Expect(250);
        foreach (var r in to)
        {
            Say($"RCPT TO:<{r}>");
            Expect(250, 251);
        }
        Say("DATA");
        Expect(354);
        var data = new StringBuilder();
        foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
            data.Append(line.StartsWith('.') ? "." + line : line).Append("\r\n");
        if (message.EndsWith("\r\n", StringComparison.Ordinal))
            data.Length -= 2;                        // the last split part is empty: no extra line
        data.Append(".\r\n");
        stream.Write(Encoding.UTF8.GetBytes(data.ToString()));
        Expect(250);
        try
        {
            Say("QUIT");
            reader.Reply();
        }
        catch (Exception e) when (e is IOException or SmtpException)
        {
        }
    }

    sealed class SmtpReader(Stream s)
    {
        readonly List<byte> line = [];

        string Line()
        {
            line.Clear();
            while (true)
            {
                var b = s.ReadByte();
                if (b < 0)
                    throw new SmtpException("the connection closed");
                if (b == '\n')
                    return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                line.Add((byte)b);
            }
        }

        public (int Code, List<string> Lines) Reply()
        {
            var lines = new List<string>();
            while (true)
            {
                var l = Line();
                if (l.Length < 3 || !int.TryParse(l[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                    throw new SmtpException($"a reply that is no SMTP: {Sync.Text.Cut(l, 80)}");
                lines.Add(l.Length > 4 ? l[4..] : "");
                if (l.Length < 4 || l[3] != '-')
                    return (code, lines);
            }
        }
    }
}
