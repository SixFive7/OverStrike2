using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>A run's report: its publish steps added up, and its text - the run's .txt beside its .json in the reports folder.</summary>
public static class RunReport
{
    public const int RowsShown = 40;                 // past this many rows of a kind the text gives counts; the report's JSON keeps every row

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
        var result = (JsonObject)before.DeepClone();
        foreach (var (key, value) in now ?? [])
        {
            if (value is JsonArray list)
            {
                var have = Node.Arr(result[key]).Select(x => x?.DeepClone()).ToList();
                if (key == "files")
                    have.AddRange(list.Where(x => !have.Any(h => Node.Eq(h, x))).Select(x => x?.DeepClone()));
                else
                    have.AddRange(list.Select(x => x?.DeepClone()));
                result[key] = new JsonArray(have.ToArray());
            }
            else if (key == "modes" && value is JsonObject modes)
            {
                var merged = Node.Obj(result["modes"]?.DeepClone());
                foreach (var (mode, rows) in modes)
                    merged[mode] = new JsonArray(Node.Arr(merged[mode]).Select(x => x?.DeepClone()).Concat(Node.Arr(rows).Select(x => x?.DeepClone())).ToArray());
                result["modes"] = merged;
            }
            else if (key == "backfill" && value is JsonObject fill)
            {
                var merged = Node.Obj(result["backfill"]?.DeepClone());
                foreach (var (mode, n) in fill)
                    merged[mode] = Node.Int(merged[mode]) + Node.Int(n);
                result["backfill"] = merged;
            }
            else if (!(value is null || (Node.IsString(value) && value.GetValue<string>().Length == 0) || (value is JsonObject o && o.Count == 0)))
                result[key] = value.DeepClone();
        }
        return result;
    }

    /// <summary>A run's report as text, from its report `r`: its title and its body (the run's .txt beside its .json).</summary>
    public static (string Subject, string Body) Summary(JsonObject r, SyncConfig cfg)
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
                      + $"{(head.Count > 0 ? string.Join(", ", head) : "nothing new")}{(stop is not null ? $" - STOPPED: {Issues.ShortStop(stop)}" : "")}";
        W($"{cfg.ServerName} - the Workshop sync, a run of {S(r["day"])} ({Get(r, "started_hm", "?")} to {Get(r, "ended_hm", "?")}). It runs whenever nobody plays and mails problems only;");
        W("every run leaves a report like this one in its reports folder.");
        W("");
        if (stop is not null)
        {
            W($"STOPPED: {stop}");
            W("Every Web API step stopped for the rest of the day at that moment (any 429, 403 or burst of");
            W("server errors). The next day starts again from the ledger's counts.");
            W("");
        }
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
            W($"   {N(sv, "missing")} missing of the keep set, {N(sv, "ok")} handed over ({Text.Mb(N(sv, "bytes"))}), {N(sv, "tested")} tested in this run and handed over, "
              + $"{Node.Arr(sv["failed"]).Count} failed, {N(sv, "left")} still missing{(Node.Truthy(sv["stop"]) ? $"; stopped: {S(sv["stop"])}" : "")}.");
        var dl = Node.Obj(r["downloads"]);
        W("3. Downloads (SteamCMD, anonymous login):");
        var failed = Node.Arr(dl["failed"]);
        var firstFails = string.Join("; ", failed.Take(5).Select(f => $"{S(Node.Arr(f).ElementAtOrDefault(0))}: {S(Node.Arr(f).ElementAtOrDefault(1))}"));
        W($"   {N(dl, "ok")} downloaded ({Text.Mb(N(dl, "bytes"))}), {failed.Count} failed{(failed.Count > 0 ? $" ({firstFails})" : "")}; {N(dl, "queued_left")} still queued for later runs.");
        if (Node.Truthy(dl["stop"]))
            W($"   stopped: {S(dl["stop"])}");
        var t = Node.Obj(r["tests"]);
        W("4. Scan and load test (a test copy of the server, no plugins, its own anonymous logon):");
        var rows = Node.StrList(t["rows"]);
        var passes = rows.Where(x => x.StartsWith("PASS", StringComparison.Ordinal)).ToList();
        if (rows.Count > RowsShown)                   // a long run: the counts, what did not pass, a taste of what did; the report has them all
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
            foreach (var row in rows.Where(x => !x.StartsWith("PASS", StringComparison.Ordinal)).Take(RowsShown))
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
            foreach (var row in listedRows.Take(RowsShown).Select(Node.Arr))
                W($"   maps.json  #{S(row.ElementAtOrDefault(1))} {S(row.ElementAtOrDefault(2))} ({S(row.ElementAtOrDefault(0))}, {I(row.ElementAtOrDefault(3))} MB)");
            if (listedRows.Count > RowsShown)
                W($"   maps.json  ... and {listedRows.Count - RowsShown} more");
            foreach (var (mode, modeRows) in Node.Obj(pub["modes"]).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var fname = mode == "deathmatch" ? "maps-deathmatch.json" : "maps-wingman.json";
                var list = Node.Arr(modeRows);
                if (list.Count > RowsShown / 2)
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
            foreach (var row in packs.Take(RowsShown).Select(Node.Arr))
                W($"   broken.json packs  {S(row.ElementAtOrDefault(1))} ({S(row.ElementAtOrDefault(0))}): {S(row.ElementAtOrDefault(2))}");
            if (packs.Count > RowsShown)
                W($"   broken.json packs  ... and {packs.Count - RowsShown} more");
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
        W($"Limits: Web API calls this run {Get(lim, "api_run", "?")} of {Get(lim, "api_run_cap", "?")}, last 24 h {Get(lim, "api_24h", "?")} of {Get(lim, "api_day_cap", "?")} "
          + $"(10% of Valve's 100,000), at least {Text.Fixed(lim.ContainsKey("api_gap") ? Node.Float(lim["api_gap"]) : 2, 0)} s apart;");
        W($"downloads today {Get(lim, "dl_items", "?")}, {Text.Mb(Node.Int(lim["dl_bytes"]))}{(Node.Truthy(lim["dl_engine"]) ? $" (with {I(lim["dl_engine"])} copies the test engine fetched itself)" : "")} "
          + $"- only Steam's push-back stops them; test-engine starts {Get(lim, "engine_starts", "?")} of {Get(lim, "engine_starts_max", "?")}.");
        var bl = Node.Obj(r["backlog"]);
        if (Node.Truthy(bl["items"]))
        {
            var eta = Or(r["backlog_until"], "");
            var byMode = Node.Obj(bl["by_mode"]);
            W($"Backlog: {I(bl["items"])} item(s) waiting, {Text.Mb(Node.Int(bl["bytes"]))} (Deathmatch {N(byMode, "deathmatch")}, Wingman {N(byMode, "wingman")}, Arms Race {N(byMode, "armsrace")} - one item can carry several tags), taken");
            W($"the ones the keep set wants first, new uploads next, then strictly by subscribers. About {N(bl, "days")} more day(s){eta}: {Text.Fixed(bl.ContainsKey("pace") ? Node.Float(bl["pace"]) : Verdicts.PaceDefault, 0)} s a map");
            W($"(download and test), about {Text.Fixed(Node.Float(bl["hours"]), 0)} h a day while nobody plays, so about {(Node.Integer(bl["per_day"]) is { } pd ? pd : 0)} a day: only time and the test speed set");
            W("it, and every hour somebody plays comes off it.");
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
}
