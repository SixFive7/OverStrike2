// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>The match history (the history tab, 2026-11-19): one entry per match a human was in - when it ran, the map and its mode, and every human
/// who joined it with the name they had and their time in it. The bridge keeps it for good in its state, history.jsonl, one JSON object a line, oldest
/// first (History.cs: the live record; its --history-backfill builds the same lines from the logs). The plugin gets the newest BlockMax of them as this
/// block (the bridge's /history/, fetched when HudState.HistoryVersion moves) and shows them in the map browser's History view (HudBrowser.History):
/// newest first, a card a match, the map's own card with its current likes and the player's own Like and Dislike, as in the browser.</summary>
public sealed class HudHistory
{
    /// <summary>A human in a match: their Steam account, the name they had then, their seconds in it, and when they were first in it.</summary>
    public sealed record Player(long Account, string Name, int Seconds, DateTimeOffset First);

    /// <summary>A match: when it started and ended (local time, with its offset), its map - the Workshop id (0: an official map, or not known), the name the
    /// server showed and the name the game logged - its mode (armsrace, deathmatch, wingman; "" not known), what ended it (EndedBy), the game's final
    /// score where it had one ("9:4" in Wingman; "" otherwise), its humans, what of it is not known (Missing: "start", "end", "map id", "players",
    /// "times"; empty when it is complete) and where it came from ("live", "backfill").</summary>
    public sealed record Entry(DateTimeOffset Start, DateTimeOffset End, long MapId, string MapName, string LogName, string Mode, string EndedBy, string Score,
        IReadOnlyList<Player> Players, IReadOnlyList<string> Missing, string Source)
    {
        /// <summary>When the humans' part of it began: the first of them in it - later than Start when it ran a while with nobody on.</summary>
        public DateTimeOffset From => Players.Count == 0 ? Start : Players.Min(p => p.First) is var first && first > Start ? first : Start;

        public bool Incomplete => Missing.Count > 0;
    }

    /// <summary>What ended a match.</summary>
    public const string ByMatchEnd = "match end", ByMapChange = "map change", ByServerStart = "server start", ByServerStop = "server stop", ByRestart = "restart",
        ByUnknown = "not known";

    /// <summary>The most entries the plugin is sent: two hundred pages of the browser. The file keeps every one.</summary>
    public const int BlockMax = 2400;

    public long Version;
    public List<Entry> Entries = new(); // newest first

    /// <summary>No entry; the version moves on (a plugin fetches on any move).</summary>
    public void Clear() { Entries.Clear(); Version++; }

    // ---- the file: one JSON object a line ----

    /// <summary>An entry as its line in history.jsonl.</summary>
    public static string ToJsonLine(Entry e)
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
            players.Add((JsonNode)new JsonObject { ["account"] = p.Account, ["name"] = p.Name, ["seconds"] = p.Seconds, ["first"] = Iso(p.First) });
        o["players"] = players;
        if (e.Missing.Count > 0) o["missing"] = new JsonArray(e.Missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        o["source"] = e.Source;
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
                        players.Add(new Player(account, Str(p["name"]), (int)Math.Max(0, Long(p["seconds"])), TryTime(Str(p["first"]), out var f) ? f : start));
            var missing = o["missing"] is JsonArray m ? m.Select(x => Str(x)).Where(x => x != "").ToList() : new List<string>();
            return new Entry(start, end, map != null ? Long(map["id"]) : 0, map != null ? Str(map["name"]) : "", map != null ? Str(map["log"]) : "", Str(o["mode"]),
                Str(o["endedBy"]), Str(o["score"]), players, missing, Str(o["source"]));
        }
        catch (JsonException) { return null; }
    }

    static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    static bool TryTime(string text, out DateTimeOffset t) =>
        DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    // ---- the block the plugin is sent: a version line, then the newest entries' lines, newest first ----

    public string Encode()
    {
        var sb = new StringBuilder();
        sb.Append("history\t").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var e in Entries.Take(BlockMax)) sb.Append(ToJsonLine(e)).Append('\n');
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

    // ---- the words a card shows ----

    /// <summary>When it ran, in the server's own time as it was then: "Tue 18 Nov  ·  21:44 - 21:57" - from when its humans were in it; a span over
    /// midnight names both days.</summary>
    public static string When(Entry e)
    {
        var (from, to) = (e.From, e.End);
        var day = from.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        var span = from.Date == to.Date ? $"{from:HH:mm} - {to:HH:mm}" : $"{from:HH:mm} - {to.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}";
        return $"{day}  ·  {span}";
    }

    /// <summary>Who was in it, longest in it first: "Alice, Bob, Cara and 2 more" - at most shown names, then a count.</summary>
    public static string Who(Entry e, int shown = 4)
    {
        var names = e.Players.OrderByDescending(p => p.Seconds).ThenBy(p => p.First).Select(p => p.Name.Trim() == "" ? "a player" : p.Name.Trim()).ToList();
        if (names.Count == 0) return "";
        if (names.Count <= shown) return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return string.Join(", ", names.Take(shown)) + $" and {names.Count - shown} more";
    }

    /// <summary>The line under it: how long it ran for its humans, Wingman's final score, and a mark for what the record does not know.</summary>
    public static string Note(Entry e)
    {
        var minutes = (int)Math.Round((e.End - e.From).TotalMinutes);
        var parts = new List<string> { minutes < 1 ? "under a minute" : $"{minutes} min" };
        if (e.Score != "") parts.Add(e.Score);
        if (e.EndedBy == ByServerStop || e.EndedBy == ByServerStart) parts.Add("the server went down");
        if (e.Incomplete) parts.Add("from the logs, partly");
        return string.Join("  ·  ", parts);
    }
}
