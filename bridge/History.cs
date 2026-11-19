// The match history (the history tab, 2026-11-19): every match a human was in, kept for good in history.jsonl under the data folder - one JSON object a
// line, oldest first (HudHistory.cs) - and the newest of them sent to the plugin for the map browser's History view.
//
// MatchTracker follows the game's own events and finishes an entry when a match is over: its end screen (CS2's "Game Over"), a map change, a restart
// of the match that is not the fairness one, the server going down, or a hole in the record. A match is on a map: from its start - the map's start,
// or the end screen of the match before it on that map, should the vote play it again - to that end. Its humans are everybody who entered the game
// during it, and everybody still in from the match before it; their seconds are their time in it. Bots never enter the log as players with a Steam
// account, and the lab's fake players never enter the game at all, so neither is ever in it. A match nobody human was in is no entry, nor is the end
// screen alone before a change (EndScreenSeconds). The live bridge feeds it from its log stream (HistoryIngest); --history-backfill feeds it the same
// events read back from the bridge's day logs and the game's console log, as far back as they reach, and marks what they cannot tell (Missing).
using System.Globalization;
using System.Text.RegularExpressions;

sealed partial class MatchTracker
{
    /// <summary>The end screen of a match is the next one's start on that map, should the vote play it again: a change within this many seconds after it
    /// was only the screen, no match.</summary>
    public const double EndScreenSeconds = 150;

    /// <summary>A restart of the match by a player, this early in it, is still the same match (and every fairness restart is: the joiner starts it over with
    /// everybody, EarlyJoin.cs).</summary>
    public const double RestartGraceSeconds = 60;

    /// <summary>The least time its humans were in a match for it to be one: a map voted away the moment somebody came in is no match.</summary>
    public const double LeastSeconds = 60;

    public string Source = "live";
    public Action<HudHistory.Entry>? Finished;

    sealed class Match
    {
        public DateTimeOffset Start;
        public long MapId;
        public string MapName = "", LogName = "", Mode = "";
        public bool AfterEnd;   // begun at the last match's end screen: the next match on that map, should it be played again
        public DateTimeOffset LastLeft; // when the last human to leave it left
        public readonly SortedSet<string> Missing = new(StringComparer.Ordinal);
        public readonly Dictionary<long, (string Name, double Seconds, DateTimeOffset First)> Roster = new();
    }

    readonly Dictionary<long, (string Name, DateTimeOffset Since)> present = new();
    Match? cur;
    DateTimeOffset lastStart = DateTimeOffset.MinValue;
    string lastLog = "";

    /// <summary>A match is on, as far as the record knows.</summary>
    public bool Running => cur != null;

    /// <summary>When the match that is on began; null with none on.</summary>
    public DateTimeOffset? StartedAt => cur?.Start;

    /// <summary>Nothing known: no match on, nobody in (a new record; the self-test's sections).</summary>
    public void Clear()
    {
        present.Clear();
        cur = null;
        lastStart = DateTimeOffset.MinValue;
        lastLog = "";
    }

    /// <summary>The Workshop id of the map that is on, as far as the record knows: 0 for an official map, or one it was not told.</summary>
    public long MapId => cur?.MapId ?? 0;

    /// <summary>Something the record cannot tell about the match that is on (a backfill's: "map id" for a map start its log does not name).</summary>
    public void Note(string missing) => cur?.Missing.Add(missing);

    /// <summary>The next match on the same map: its map as the last one's, a Workshop id it lacked lacking still.</summary>
    static Match Next(Match? m, DateTimeOffset at, bool afterEnd, string logName = "", string mode = "")
    {
        var n = new Match { Start = at, MapId = m?.MapId ?? 0, MapName = m?.MapName ?? "", LogName = m?.LogName ?? logName, Mode = m?.Mode ?? mode, AfterEnd = afterEnd };
        if (m?.Missing.Contains("map id") == true) n.Missing.Add("map id");
        return n;
    }

    /// <summary>The humans in the game now, as the record knows them.</summary>
    public int Present => present.Count;

    /// <summary>A map started: the match that was on is over (the server started, or the map changed), and the new map's first match begins. Everybody
    /// reconnects into it, and is in it once they enter. The second line of the same load (CS2 logs "Loading map" and "Started:") changes nothing.</summary>
    public void MapStarted(DateTimeOffset at, string logName, long mapId, string mapName, string mode, bool serverStart = false)
    {
        if ((at - lastStart).TotalSeconds < 10 && (logName == "" || logName.Equals(lastLog, StringComparison.OrdinalIgnoreCase))) { Name(mapId, mapName, mode); return; }
        Close(at, serverStart ? HudHistory.ByServerStart : HudHistory.ByMapChange);
        present.Clear();
        lastStart = at;
        lastLog = logName;
        cur = new Match { Start = at, MapId = mapId, MapName = mapName, LogName = logName, Mode = mode };
    }

    /// <summary>The map's own words, once known (the server's name for it, its Workshop id, its mode), for the match that is on.</summary>
    public void Name(long mapId, string mapName, string mode)
    {
        if (cur == null) return;
        if (mapId > 0) cur.MapId = mapId;
        if (mapName != "") cur.MapName = mapName;
        if (mode != "") cur.Mode = mode;
    }

    /// <summary>A match ended (CS2's "Game Over: scrimcomp2v2 mg_ar_shoots de_assembly score 2:9 after 11 min"): its entry, with its mode and Wingman's
    /// score; its end screen is the next match's start on that map, should the vote play it again.</summary>
    public void MatchEnded(DateTimeOffset at, string gameOver)
    {
        var (mode, logName, score) = ReadGameOver(gameOver);
        if (cur != null && mode != "") cur.Mode = mode;
        var was = cur;
        Close(at, HudHistory.ByMatchEnd, score);
        cur = Next(was, at, afterEnd: true, logName, mode);
    }

    /// <summary>The match restarted (the bridge's os_restart). A fairness restart is the same match, the joiner in it; a player's Restart match ends it
    /// and starts the next on that map - unless the match had barely begun (RestartGraceSeconds).</summary>
    public void Restarted(DateTimeOffset at, bool fair)
    {
        if (cur == null || fair || (at - cur.Start).TotalSeconds < RestartGraceSeconds) return;
        var was = cur;
        Close(at, HudHistory.ByRestart);
        cur = Next(was, at, afterEnd: false);
    }

    /// <summary>A human entered the game. With no match known - the record began, or came back, inside one - a match begins here, its start and its
    /// players not known in full.</summary>
    public void Entered(DateTimeOffset at, long account, string name)
    {
        if (account <= 0) return;
        if (cur == null) { cur = new Match { Start = at }; cur.Missing.Add("start"); cur.Missing.Add("players"); }
        present[account] = present.TryGetValue(account, out var p) ? (name, p.Since) : (name, at);
        Touch(cur, account, name, present[account].Since, 0);
    }

    /// <summary>A human who was not known to be in acted (chat): in from now - the record missed their entering.</summary>
    public void Seen(DateTimeOffset at, long account, string name)
    {
        if (account <= 0 || present.ContainsKey(account)) return;
        Entered(at, account, name);
        cur?.Missing.Add("players");
    }

    /// <summary>A human left the game (any disconnect: a map change's reconnect enters again).</summary>
    public void Left(DateTimeOffset at, long account)
    {
        if (!present.Remove(account, out var p) || cur == null) return;
        var since = p.Since > cur.Start ? p.Since : cur.Start;
        Touch(cur, account, p.Name, since, Math.Max(0, (at - since).TotalSeconds));
        cur.LastLeft = at;
    }

    /// <summary>The server went down cleanly (the quit's echo): the match that was on is over.</summary>
    public void ServerDown(DateTimeOffset at)
    {
        Close(at, HudHistory.ByServerStop);
        present.Clear();
    }

    /// <summary>The record has a hole from lastSeen on (the bridge was down): what was on ends there, its end not known - or, after a short hole, goes on
    /// with what happened during it not known.</summary>
    public void Gap(DateTimeOffset lastSeen, DateTimeOffset resumed, double shortSeconds = 600)
    {
        if (cur == null) { present.Clear(); return; }
        if ((resumed - lastSeen).TotalSeconds <= shortSeconds) { cur.Missing.Add("players"); cur.Missing.Add("times"); return; }
        cur.Missing.Add("end");
        Close(lastSeen, HudHistory.ByUnknown);
        present.Clear();
    }

    /// <summary>The record ends (a backfill's last line, the moment the live record takes over): the match that is on is an entry, its end not known - at
    /// the last human's leaving when nobody is in it any more, else now. Returns whether there was one.</summary>
    public bool Finish(DateTimeOffset at)
    {
        if (cur == null) return false;
        var end = present.Count > 0 || cur.LastLeft <= cur.Start ? at : cur.LastLeft;
        cur.Missing.Add("end");
        Close(end, HudHistory.ByUnknown);
        present.Clear();
        return true;
    }

    static void Touch(Match m, long account, string name, DateTimeOffset first, double seconds) =>
        m.Roster[account] = m.Roster.TryGetValue(account, out var r) ? (name, r.Seconds + seconds, r.First < first ? r.First : first) : (name, seconds, first);

    void Close(DateTimeOffset at, string by, string score = "")
    {
        var m = cur;
        cur = null;
        if (m == null) return;
        foreach (var (account, p) in present.ToList())
        {
            var since = p.Since > m.Start ? p.Since : m.Start;
            Touch(m, account, p.Name, since, Math.Max(0, (at - since).TotalSeconds));
            present[account] = (p.Name, at); // still in: on into whatever comes next
        }
        if (m.Roster.Count == 0) return;
        if (m.AfterEnd && by != HudHistory.ByMatchEnd && (at - m.Start).TotalSeconds < EndScreenSeconds) return; // only the end screen before a change
        var entry = Snapshot(m, at, by, score);
        if ((entry.End - entry.From).TotalSeconds < LeastSeconds) return;
        Finished?.Invoke(entry);
    }

    HudHistory.Entry Snapshot(Match m, DateTimeOffset at, string by, string score = "")
    {
        var players = m.Roster.Select(r => new HudHistory.Player(r.Key, r.Value.Name, (int)Math.Round(r.Value.Seconds), r.Value.First))
            .OrderBy(p => p.First).ThenBy(p => p.Account).ToList();
        return new HudHistory.Entry(m.Start, at, m.MapId, m.MapName != "" ? m.MapName : m.LogName, m.LogName, m.Mode, by, score, players, m.Missing.ToList(), Source);
    }

    [GeneratedRegex(@"^(\S+)\s+\S+\s+(\S+)\s+score\s+(\d+):(\d+)")] private static partial Regex GameOverRx();

    /// <summary>CS2's Game Over line, read: the mode its game mode word names, the map it logged, and the score where one tells (not 0:0).</summary>
    public static (string Mode, string LogName, string Score) ReadGameOver(string text)
    {
        if (GameOverRx().Match(text.Trim()) is not { Success: true } g) return ("", "", "");
        var mode = g.Groups[1].Value switch { "gungameprogressive" => "armsrace", "deathmatch" => "deathmatch", "scrimcomp2v2" => "wingman", _ => "" };
        var score = g.Groups[3].Value == "0" && g.Groups[4].Value == "0" ? "" : $"{g.Groups[3].Value}:{g.Groups[4].Value}";
        return (mode, g.Groups[2].Value, score);
    }
}

static partial class Program
{
    /// <summary>history.jsonl: the match history, kept for good.</summary>
    static string HistoryPath => Path.Combine(StateDir, "history.jsonl");

    /// <summary>The time zone the history's times are in: the container's own (TZ), UTC in --selftest.</summary>
    static TimeZoneInfo HistoryZone = TimeZoneInfo.Local;

    static readonly MatchTracker MatchHistory = new() { Finished = KeepMatch };

    /// <summary>The history the plugin is sent, newest first; its version moves with every new entry and with every bridge, so a plugin fetches after a restart.</summary>
    static readonly HudHistory History = new() { Version = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond };

    /// <summary>The bridge's clock as the history writes it: local time, with its offset.</summary>
    static DateTimeOffset HistoryNow() => TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(Now(), DateTimeKind.Utc)), HistoryZone);

    /// <summary>The history from history.jsonl, at the bridge's start: a line that does not read is skipped, and said.</summary>
    static string LoadHistory()
    {
        History.Entries.Clear();
        if (!File.Exists(HistoryPath)) return $"history: no {HistoryPath} yet - the first match a human finishes starts it";
        var bad = 0;
        foreach (var line in File.ReadLines(HistoryPath))
        {
            if (line.Trim() == "") continue;
            if (HudHistory.FromJsonLine(line) is { } e) History.Entries.Add(e); else bad++;
        }
        History.Entries.Reverse(); // the file is oldest first, the HUD newest first
        History.Version++;
        HistoryText = History.Encode();
        return $"history: {History.Entries.Count} match(es) from {HistoryPath}" + (bad > 0 ? $", {bad} line(s) that do not read skipped" : "")
            + (History.Entries.Count > 0 ? $", the oldest of {History.Entries[^1].Start:yyyy-MM-dd}" : "");
    }

    /// <summary>A finished match: appended to history.jsonl, and the plugin's copy moves.</summary>
    static void KeepMatch(HudHistory.Entry e)
    {
        try { File.AppendAllText(HistoryPath, HudHistory.ToJsonLine(e) + "\n"); }
        catch (Exception x) { Log($"history: {HistoryPath} could not be written - {x.Message}"); }
        History.Entries.Insert(0, e);
        History.Version++;
        HistoryText = History.Encode();
        Log($"history: {e.MapName} ({e.Mode}), {e.Start:HH:mm}-{e.End:HH:mm}, {e.EndedBy}, {e.Players.Count} human(s): {HudHistory.Who(e, 8)}");
    }

    /// <summary>The block the plugin fetches (Hud.HistoryPath): made on the event loop whenever the history moves (a full block takes tens of milliseconds to
    /// write), and served as it stands from the HTTP thread, as the Sync page's block is.</summary>
    static volatile string HistoryText = new HudHistory().Encode();

    /// <summary>Set by OnMapStart: the start it just took was the server's own (a start of the start map nobody asked for).</summary>
    static bool LastStartWasServerStart;

    /// <summary>A line of the game's log, for the history: who entered, acted and left, the maps' starts, the matches' ends, the quit.</summary>
    static void HistoryIngest(LogEvent e)
    {
        var at = HistoryNow();
        switch (e.Kind)
        {
            case Kind.Entered: MatchHistory.Entered(at, AccountId(e.SteamId), e.Name); break;
            case Kind.Chat: MatchHistory.Seen(at, AccountId(e.SteamId), e.Name); break;
            case Kind.Disconnected: MatchHistory.Left(at, AccountId(e.SteamId)); break;
            case Kind.MapStart: MatchHistory.MapStarted(at, S.LogName ?? e.Text, S.Map?.Id ?? 0, S.Map?.Name ?? "", S.Mode, LastStartWasServerStart); break;
            case Kind.MatchEnd: MatchHistory.MatchEnded(at, e.Text); break;
            case Kind.Shutdown: MatchHistory.ServerDown(at); break;
        }
    }

    // ---- --history-backfill: the same history, read back from the logs ----

    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) (.*)$")] private static partial Regex BridgeLineRx();
    [GeneratedRegex(@"^\d\d/\d\d \d\d:\d\d:\d\d \[Server\] (L \d\d/\d\d/\d{4} - \d\d:\d\d:\d\d: .*)$")] private static partial Regex ConsoleLineRx();
    [GeneratedRegex(@"^L (\d\d/\d\d/\d{4} - \d\d:\d\d:\d\d): ")] private static partial Regex ConsoleStampRx();
    [GeneratedRegex(@"^(Entered|Disconnected|Chat) (.+?) \[U:1:(\d+)\](?: (.*))?$")] private static partial Regex BridgePlayerRx();
    [GeneratedRegex(@"^MapStart\s+(\S*)\s*\[X-Game-Map: ([^\]]*)\]$")] private static partial Regex BridgeMapRx();
    [GeneratedRegex(@"^playing: (?:#\d+ )?(.+?)(?: \(official\))?(?:  ·  .*)?$")] private static partial Regex BridgePlayingRx();
    [GeneratedRegex(@"^host_workshop_map (\d+) ->")] private static partial Regex BridgeWorkshopRx();
    [GeneratedRegex(@"^os_mode (armsrace|deathmatch|wingman)\b")] private static partial Regex BridgeModeRx();

    /// <summary>One event read back from a log, for the backfill: when, which source (0 the bridge's day log, 1 the game's console log - the bridge's is the
    /// richer at the same moment), its kind and its words.</summary>
    sealed record BackEvent(DateTimeOffset At, int Source, int Seq, string Kind, string Name, long Account, string Text);

    /// <summary>bridge --history-backfill OUT [--zone ZONE] [--until "yyyy-MM-dd HH:mm"] FILE...: the history from the bridge's day logs (bridge-*.log) and
    /// the game's console logs (console.log, its "[Server] L" lines), as far back as they reach - read only, written to OUT (one JSON object a line,
    /// oldest first, as history.jsonl). --until stops at the moment the live record takes over. Returns 0, or 2 for words it cannot use.</summary>
    static int HistoryBackfillCommand(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: bridge --history-backfill OUT [--zone ZONE] [--until \"yyyy-MM-dd HH:mm\"] FILE..."); return 2; }
        var output = args[0];
        var zone = TimeZoneInfo.Local; // the logs' times are the server's local time: run it with --zone where this machine's differs
        DateTimeOffset? until = null;
        var files = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--zone" && i + 1 < args.Length) zone = Zone(args[++i]);
            else if (args[i] == "--until" && i + 1 < args.Length) until = Local(DateTime.ParseExact(args[++i], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), zone);
            else files.Add(args[i]);
        }
        var (entries, words) = Backfill(files.Select(f => (f, File.ReadAllLines(f))).ToList(), zone, until);
        File.WriteAllText(output, string.Concat(entries.Select(e => HudHistory.ToJsonLine(e) + "\n")));
        Console.WriteLine(words);
        return 0;
    }

    /// <summary>A time zone by its system name, or by its IANA name where the system knows only Windows' names (a workstation without ICU).</summary>
    static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows)) return TimeZoneInfo.FindSystemTimeZoneById(windows);
            throw;
        }
    }

    static DateTimeOffset Local(DateTime wall, TimeZoneInfo zone) => new(wall, zone.GetUtcOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified)));

    /// <summary>The backfill proper: every file's events in time order, through a MatchTracker of their own (source "backfill"). A file is a bridge day log
    /// or a console log by its lines. Returns the entries, oldest first, and a summary.</summary>
    static (List<HudHistory.Entry> Entries, string Words) Backfill(List<(string Name, string[] Lines)> files, TimeZoneInfo zone, DateTimeOffset? until)
    {
        var events = new List<BackEvent>();
        var seq = 0;
        foreach (var (name, lines) in files)
            foreach (var line in lines)
                if (ReadBack(line, zone, ref seq) is { } e) events.Add(e);
        // Where the bridge's day logs reach, they are the record: the console's lines there are the same moments told without the maps' ids and names.
        var bridged = events.Where(e => e.Source == 0).Select(e => e.At).DefaultIfEmpty().ToList();
        var (bridgeFrom, bridgeTo) = events.Any(e => e.Source == 0) ? (bridged.Min(), bridged.Max()) : (DateTimeOffset.MaxValue, DateTimeOffset.MinValue);
        events = events.Where(e => until == null || e.At < until.Value).Where(e => e.Source == 0 || e.At < bridgeFrom || e.At > bridgeTo)
            .GroupBy(e => (e.At, e.Kind, e.Account, e.Kind == "map" ? e.Text.ToLowerInvariant() : "")).Select(g => g.OrderBy(e => e.Source).First()) // one moment told twice
            .OrderBy(e => e.At).ThenBy(e => e.Source).ThenBy(e => e.Seq).ToList();
        var entries = new List<HudHistory.Entry>();
        var t = new MatchTracker { Source = "backfill", Finished = entries.Add };
        var serverStarts = events.Where(e => e.Kind == "serverstart").Select(e => e.At).ToList();
        var fairAt = DateTimeOffset.MinValue;
        long nextId = 0;
        string nextMode = "";
        DateTimeOffset? last = null;
        foreach (var e in events)
        {
            if (e.Kind == "bridgestart") { if (last is { } l) t.Gap(l, e.At); last = e.At; continue; }
            switch (e.Kind)
            {
                case "entered": t.Entered(e.At, e.Account, e.Name); break;
                case "seen": t.Seen(e.At, e.Account, e.Name); break;
                case "left": t.Left(e.At, e.Account); break;
                case "workshop": nextId = e.Account; break;
                case "mode": // the mode a change is to load in - or, said just after a start (a server's own start is Arms Race), the mode of the map that started
                    nextMode = e.Text;
                    if (t.StartedAt is { } began && (e.At - began).TotalSeconds is >= 0 and <= 10) t.Name(0, "", e.Text);
                    break;
                case "map":
                    t.MapStarted(e.At, e.Text, e.Source == 0 ? nextId : 0, "", nextMode, serverStarts.Any(s => s >= e.At && (s - e.At).TotalSeconds <= 20));
                    if (e.Source == 1 && t.MapId == 0) t.Note("map id"); // the console names the map's file alone
                    nextId = 0;
                    break;
                case "playing": // the bridge's name for the map that just started; an official map has no Workshop id to miss
                    t.Name(0, e.Text, "");
                    if (e.Account == 0 && t.MapId == 0) t.Note("map id");
                    break;
                case "matchend": t.MatchEnded(e.At, e.Text); break;
                case "quit": t.ServerDown(e.At); break;
                case "fair": fairAt = e.At; break;
                case "restart": t.Restarted(e.At, (e.At - fairAt).TotalSeconds <= 15); break;
            }
            last = e.At;
        }
        var before = entries.Count;
        var finished = last is { } end && t.Finish(end) && entries.Count > before;
        var words = entries.Count == 0 ? "history backfill: no match with a human in these logs"
            : $"history backfill: {entries.Count} match(es) from {entries[0].Start:yyyy-MM-dd HH:mm} to {entries[^1].End:yyyy-MM-dd HH:mm}, {entries.Count(x => x.Incomplete)} of them incomplete"
              + $", {entries.SelectMany(x => x.Players).Select(p => p.Account).Distinct().Count()} human(s)"
              + (finished ? $"; the last, {entries[^1].MapName}, still on at the logs' end: its end not known" : "");
        return (entries, words);
    }

    /// <summary>One line of a bridge day log or a console log, as a backfill event; null for any other line.</summary>
    static BackEvent? ReadBack(string line, TimeZoneInfo zone, ref int seq)
    {
        if (BridgeLineRx().Match(line) is { Success: true } b)
        {
            var at = Local(DateTime.ParseExact(b.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), zone);
            var msg = b.Groups[2].Value;
            seq++;
            if (BridgePlayerRx().Match(msg) is { Success: true } p)
            {
                var account = long.Parse(p.Groups[3].Value, CultureInfo.InvariantCulture);
                return new(at, 0, seq, p.Groups[1].Value switch { "Entered" => "entered", "Disconnected" => "left", _ => "seen" }, p.Groups[2].Value, account, p.Groups[4].Value);
            }
            if (BridgeMapRx().Match(msg) is { Success: true } m)
            {
                var logName = m.Groups[1].Value != "" ? m.Groups[1].Value : m.Groups[2].Value is var x && x != "none" ? x : "";
                return new(at, 0, seq, "map", "", 0, logName);
            }
            if (msg.StartsWith("MatchEnd ", StringComparison.Ordinal)) return new(at, 0, seq, "matchend", "", 0, msg[9..].Trim());
            if (msg.StartsWith("Shutdown ", StringComparison.Ordinal)) return new(at, 0, seq, "quit", "", 0, "");
            if (BridgePlayingRx().Match(msg) is { Success: true } g) // "playing: #4 Lake  ·  980 MB  ·  Picked by the vote"; an official map's is its name alone
                return new(at, 0, seq, "playing", "", msg.Contains(" (official)", StringComparison.Ordinal) || !msg.Contains("  ·  ", StringComparison.Ordinal) ? 1 : 0, g.Groups[1].Value.Trim());
            if (BridgeWorkshopRx().Match(msg) is { Success: true } w) return new(at, 0, seq, "workshop", "", long.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture), "");
            if (BridgeModeRx().Match(msg) is { Success: true } o) return new(at, 0, seq, "mode", "", 0, o.Groups[1].Value);
            if (msg.StartsWith("rematch: The match restarts for fairness", StringComparison.Ordinal)) return new(at, 0, seq, "fair", "", 0, "");
            if (msg.StartsWith("rematch by ", StringComparison.Ordinal) && msg.Contains(": os_restart", StringComparison.Ordinal)) return new(at, 0, seq, "restart", "", 0, "");
            if (msg.StartsWith("change: Server started", StringComparison.Ordinal)) return new(at, 0, seq, "serverstart", "", 0, "");
            if (msg.StartsWith("settings: the environment variable ", StringComparison.Ordinal) || msg.StartsWith("settings: the settings file ", StringComparison.Ordinal)
                || msg.StartsWith("settings: no settings file ", StringComparison.Ordinal)) return new(at, 0, seq, "bridgestart", "", 0, ""); // the bridge's first line (BridgeConfig.StartLine)
            return null;
        }
        if (ConsoleLineRx().Match(line) is { Success: true } c)
        {
            var logLine = c.Groups[1].Value;
            if (ConsoleStampRx().Match(logLine) is not { Success: true } s) return null;
            var at = Local(DateTime.ParseExact(s.Groups[1].Value, "MM/dd/yyyy - HH:mm:ss", CultureInfo.InvariantCulture), zone);
            if (Parse(logLine) is not { } e) return null;
            seq++;
            return e.Kind switch
            {
                Kind.Entered => new(at, 1, seq, "entered", e.Name, AccountId(e.SteamId), ""),
                Kind.Disconnected => new(at, 1, seq, "left", e.Name, AccountId(e.SteamId), e.Text),
                Kind.Chat => new(at, 1, seq, "seen", e.Name, AccountId(e.SteamId), ""),
                Kind.MapStart when e.Text != "" => new(at, 1, seq, "map", "", 0, e.Text),
                Kind.MatchEnd => new(at, 1, seq, "matchend", "", 0, e.Text),
                Kind.Shutdown => new(at, 1, seq, "quit", "", 0, ""),
                _ => null,
            };
        }
        return null;
    }
}
