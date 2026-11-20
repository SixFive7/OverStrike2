// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>The match history (the history tab, 2026-11-19; its own page since the owner's 304 a): one entry per match a human was in - when it ran, the map
/// and its mode, how it ended, every human who joined it with the name they had, their time in it and their frags, the most humans in it at once, Arms Race's
/// winner and who killed whom. The bridge keeps it for good in its state, history.jsonl, one JSON object a line, oldest first (History.cs: the live record; its
/// --history-backfill builds the same lines from the logs). The plugin gets the newest BlockMax of them as this block - compact: the frags but the kills, and
/// the pairs, stay in the file (the bridge's /history/, fetched when HudState.HistoryVersion moves) - and shows them on the HISTORY page (HudHistoryPage).</summary>
public sealed class HudHistory
{
    /// <summary>A human's frags in a match, from the game's log: kills (bots too), deaths (by anybody, their own suicides among them), assists, head shots,
    /// knife, taser, noscope, wallbang ("penetrated") and in-air kills, chickens killed, suicides, the longest kill in metres and the best kill streak (kills
    /// with no death between). Counted from the real match's start, the warm-up's end.</summary>
    public sealed record Frags(int Kills = 0, int Deaths = 0, int Assists = 0, int Hs = 0, int Knife = 0, int Taser = 0, int Noscope = 0, int Wall = 0, int Air = 0,
        int Chickens = 0, int Suicides = 0, double Far = 0, int Streak = 0)
    {
        public bool Any => Kills + Deaths + Assists + Chickens + Suicides > 0;
    }

    /// <summary>A human in a match: their Steam account, the name they had then, their seconds in it, when they were first in it, and their frags (null: none
    /// recorded - a match from before the frags, or from logs without them).</summary>
    public sealed record Player(long Account, string Name, int Seconds, DateTimeOffset First, Frags? Frags = null);

    /// <summary>How often one human killed another in a match.</summary>
    public sealed record Pair(long Killer, long Victim, int Kills);

    /// <summary>A match: when it started and ended (local time, with its offset), its map - the Workshop id (0: an official map, or not known), the name the
    /// server showed and the name the game logged - its mode (armsrace, deathmatch, wingman; "" not known), what ended it (EndedBy), the game's final
    /// score where it had one ("9:4" in Wingman; "" otherwise), its humans, what of it is not known (Missing: "start", "end", "map id", "players",
    /// "times"; empty when it is complete), where it came from ("live", "backfill"), the most humans in it at once (0: not known), Arms Race's winner (an
    /// account; 0 none or not known) and every pair of humans one of whom killed the other (null: not recorded; the block the plugin gets has none).</summary>
    public sealed record Entry(DateTimeOffset Start, DateTimeOffset End, long MapId, string MapName, string LogName, string Mode, string EndedBy, string Score,
        IReadOnlyList<Player> Players, IReadOnlyList<string> Missing, string Source, int Most = 0, long Winner = 0, IReadOnlyList<Pair>? Pairs = null)
    {
        /// <summary>When the humans' part of it began: the first of them in it - later than Start when it ran a while with nobody on.</summary>
        public DateTimeOffset From => Players.Count == 0 ? Start : Players.Min(p => p.First) is var first && first > Start ? first : Start;

        public bool Incomplete => Missing.Count > 0;

        /// <summary>Its map as the map list keys it: the Workshop id, else the name the game logged (an official map's own name).</summary>
        public string Key => MapId > 0 ? MapId.ToString(CultureInfo.InvariantCulture) : LogName != "" ? LogName : MapName;

        /// <summary>The human with the most kills in it, and how many; null when nobody's are recorded.</summary>
        public (Player Player, int Kills)? TopFragger => Players.Where(p => p.Frags?.Kills > 0).OrderByDescending(p => p.Frags!.Kills).ThenBy(p => p.First)
            .Select(p => ((Player Player, int Kills)?)(p, p.Frags!.Kills)).FirstOrDefault();

        /// <summary>Who killed whom the most in it, and how often; null with no pair recorded.</summary>
        public Pair? Nemesis => Pairs?.OrderByDescending(p => p.Kills).FirstOrDefault();

        /// <summary>The name a human had in it, by account; "" when they were not in it.</summary>
        public string NameOf(long account) => Players.FirstOrDefault(p => p.Account == account)?.Name.Trim() ?? "";
    }

    /// <summary>What ended a match.</summary>
    public const string ByMatchEnd = "match end", ByMapChange = "map change", ByServerStart = "server start", ByServerStop = "server stop", ByRestart = "restart",
        ByUnknown = "not known";

    /// <summary>The most entries the plugin is sent: the HISTORY page's six hundred pages. The file keeps every one.</summary>
    public const int BlockMax = 2400;

    public long Version;
    public List<Entry> Entries = new(); // newest first

    /// <summary>No entry; the version moves on (a plugin fetches on any move).</summary>
    public void Clear() { Entries.Clear(); Version++; }

    // ---- the file: one JSON object a line ----

    /// <summary>An entry as its line in history.jsonl; compact (the block the plugin gets): a player's frags but the kills, and the pairs, left out.</summary>
    public static string ToJsonLine(Entry e, bool compact = false)
    {
        var o = new JsonObject
        {
            ["v"] = 1,
            ["start"] = Iso(e.Start),
            ["end"] = Iso(e.End),
            ["map"] = new JsonObject { ["id"] = e.MapId, ["name"] = e.MapName, ["log"] = e.LogName },
            ["mode"] = e.Mode,
            ["endedBy"] = e.EndedBy,
        };
        if (e.Score != "") o["score"] = e.Score;
        var players = new JsonArray();
        foreach (var p in e.Players)
        {
            var po = new JsonObject { ["account"] = p.Account, ["name"] = p.Name, ["seconds"] = p.Seconds, ["first"] = Iso(p.First) };
            if (p.Frags is { } f)
            {
                void Put(string key, int n) { if (n > 0) po[key] = n; }
                Put("k", f.Kills);
                if (!compact)
                {
                    Put("d", f.Deaths); Put("a", f.Assists); Put("hs", f.Hs); Put("knife", f.Knife); Put("taser", f.Taser); Put("noscope", f.Noscope); Put("wall", f.Wall);
                    Put("air", f.Air); Put("chickens", f.Chickens); Put("suicides", f.Suicides); Put("streak", f.Streak);
                    if (f.Far > 0) po["far"] = Math.Round(f.Far, 1);
                    if (!f.Any) po["frags"] = 0; // recorded, and none: not the same as not recorded
                }
                else if (f.Kills == 0) po["k"] = 0;
            }
            players.Add((JsonNode)po);
        }
        o["players"] = players;
        if (e.Missing.Count > 0) o["missing"] = new JsonArray(e.Missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        o["source"] = e.Source;
        if (e.Most > 0) o["most"] = e.Most;
        if (e.Winner > 0) o["winner"] = e.Winner;
        if (!compact && e.Pairs is { } pairs)
        {
            if (e.Nemesis is { } n) o["nemesis"] = new JsonArray(n.Killer, n.Victim, n.Kills);
            o["pairs"] = new JsonArray(pairs.Select(p => (JsonNode)new JsonArray(p.Killer, p.Victim, p.Kills)).ToArray());
        }
        return o.ToJsonString(LineJson);
    }

    /// <summary>A line as a person reads it: a name's own letters and a time's "+01:00" as they are, not escaped (still JSON; never put in a web page).</summary>
    static readonly JsonSerializerOptions LineJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>An entry from its line; null for a line that is not one (a later version's line is read for what it shares with this one).</summary>
    public static Entry? FromJsonLine(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            if (!TryTime(Str(o["start"]), out var start) || !TryTime(Str(o["end"]), out var end)) return null;
            var map = o["map"] as JsonObject;
            var players = new List<Player>();
            if (o["players"] is JsonArray a)
                foreach (var n in a)
                    if (n is JsonObject p && Long(p["account"]) is > 0 and var account)
                    {
                        int I(string key) => (int)Math.Max(0, Long(p[key]));
                        var recorded = FragKeys.Any(p.ContainsKey);
                        var frags = recorded ? new Frags(I("k"), I("d"), I("a"), I("hs"), I("knife"), I("taser"), I("noscope"), I("wall"), I("air"), I("chickens"), I("suicides"),
                            Num(p["far"]), I("streak")) : null;
                        players.Add(new Player(account, Str(p["name"]), I("seconds"), TryTime(Str(p["first"]), out var f) ? f : start, frags));
                    }
            var missing = o["missing"] is JsonArray m ? m.Select(x => Str(x)).Where(x => x != "").ToList() : new List<string>();
            List<Pair>? pairs = null;
            if (o["pairs"] is JsonArray pa)
                pairs = [.. pa.OfType<JsonArray>().Where(x => x.Count == 3).Select(x => new Pair(Long(x[0]), Long(x[1]), (int)Long(x[2]))).Where(x => x.Killer > 0 && x.Victim > 0 && x.Kills > 0)];
            return new Entry(start, end, map != null ? Long(map["id"]) : 0, map != null ? Str(map["name"]) : "", map != null ? Str(map["log"]) : "", Str(o["mode"]),
                Str(o["endedBy"]), Str(o["score"]), players, missing, Str(o["source"]), (int)Math.Max(0, Long(o["most"])), Math.Max(0, Long(o["winner"])), pairs);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A player's keys that say their frags were recorded ("frags": 0 for a player who had none).</summary>
    static readonly string[] FragKeys = ["k", "d", "a", "hs", "knife", "taser", "noscope", "wall", "air", "chickens", "suicides", "far", "streak", "frags"];

    static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    static bool TryTime(string text, out DateTimeOffset t) =>
        DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? Math.Max(0, d) : 0;

    // ---- the block the plugin is sent: a version line, then the newest entries' lines, newest first, compact ----

    public string Encode()
    {
        var sb = new StringBuilder();
        sb.Append("history\t").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var e in Entries.Take(BlockMax)) sb.Append(ToJsonLine(e, compact: true)).Append('\n');
        return sb.ToString();
    }

    public static HudHistory? Decode(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("history\t", StringComparison.Ordinal)
            || !long.TryParse(lines[0][8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)) return null;
        var h = new HudHistory { Version = version };
        foreach (var line in lines.Skip(1))
            if (line.Length > 0 && FromJsonLine(line) is { } e) h.Entries.Add(e);
        return h;
    }

    // ---- the words a card shows (HudHistoryPage) ----

    /// <summary>A day as the pages say it: "Fri 20 Nov".</summary>
    public static string Day(DateTimeOffset t) => t.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>When it ran, in the server's own time as it was then: "Tue 18 Nov  ·  21:44 - 21:57" - from when its humans were in it; a span over
    /// midnight names both days.</summary>
    public static string When(Entry e)
    {
        var (from, to) = (e.From, e.End);
        var span = from.Date == to.Date ? $"{from:HH:mm} - {to:HH:mm}" : $"{from:HH:mm} - {to.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}";
        return $"{Day(from)}  ·  {span}";
    }

    /// <summary>How long it ran for its humans: "11 min", "1 h 12 min", or "under a minute".</summary>
    public static string Duration(Entry e) => Minutes((e.End - e.From).TotalMinutes);

    /// <summary>A length in minutes as the pages say it.</summary>
    public static string Minutes(double minutes)
    {
        var m = (int)Math.Round(minutes);
        return m < 1 ? "under a minute" : m < 60 ? $"{m} min" : m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";
    }

    /// <summary>How it ended: "Match end", "Map change", "Server restart" (the server's own planned quit), "The server went down" (it came back without one),
    /// "Match restarted" (a player's Restart match) or "Not known"; Wingman's final score after it ("Match end  ·  9:4"), and a mark for a record known in part.</summary>
    public static string Ended(Entry e)
    {
        var parts = new List<string>
        {
            e.EndedBy switch
            {
                ByMatchEnd => "Match end", ByMapChange => "Map change", ByServerStop => "Server restart", ByServerStart => "The server went down",
                ByRestart => "Match restarted", _ => "Not known",
            },
        };
        if (e.Score != "") parts.Add(e.Score);
        if (e.Incomplete) parts.Add("from the logs, partly");
        return string.Join("  ·  ", parts);
    }

    /// <summary>The longest Joined may be: about two lines of the card's words. Content v4.17.0 clips the line where the card ends (hud-422: a multi-line ellipsis
    /// cost HISTORY's frames), so a longer list ends in our own words instead - "and 9 more".</summary>
    public const int JoinedMax = 150;

    /// <summary>Everybody who joined it, the longest in it first: "6 players: Alice, Bob, Cara, Dan, Eve and Fay" (the card wraps it at two lines). A list longer than
    /// JoinedMax takes names until the next would pass it, then "and N more": "14 players: Alice, Bob, ... and 9 more".</summary>
    public static string Joined(Entry e)
    {
        var names = Names(e);
        if (names.Count == 0) return "";
        var head = $"{names.Count} player{(names.Count == 1 ? "" : "s")}: ";
        var all = head + (names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]);
        if (all.Length <= JoinedMax) return all;
        var shown = 0;
        while (shown < names.Count - 1 && (head + string.Join(", ", names.Take(shown + 1)) + $" and {names.Count - shown - 1} more").Length <= JoinedMax) shown++;
        return shown == 0 ? $"{names.Count} players" : head + string.Join(", ", names.Take(shown)) + $" and {names.Count - shown} more";
    }

    /// <summary>Who was in it, longest in it first: "Alice, Bob, Cara and 2 more" - at most shown names, then a count (the bridge's log line).</summary>
    public static string Who(Entry e, int shown = 4)
    {
        var names = Names(e);
        if (names.Count == 0) return "";
        if (names.Count <= shown) return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return string.Join(", ", names.Take(shown)) + $" and {names.Count - shown} more";
    }

    static List<string> Names(Entry e) =>
        [.. e.Players.OrderByDescending(p => p.Seconds).ThenBy(p => p.First).Select(p => p.Name.Trim() == "" ? "a player" : p.Name.Trim())];
}
