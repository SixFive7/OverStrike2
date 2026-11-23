// The match history (the history tab, 2026-11-19; the HISTORY page since the owner's 304 a): every match a human was in, kept for good in history.jsonl
// under the data folder - one JSON object a line, oldest first (HudHistory.cs) - and the newest of them sent to the plugin for the HISTORY page, and read
// for the STATS page (Stats.cs).
//
// MatchTracker follows the game's own events and finishes an entry when a match is over: its end screen (CS2's "Game Over"), a map change, a player's
// restart of the match, the server going down, or a hole in the record. A match is on a map: from its start - the end of the map's warm-up (WarmUp.cs:
// every map starts in CS2's own warm-up since release 219), the map's own start where there was none, or the end screen of the match before it on that
// map, should the vote play it again - to that end. A map that ends during its warm-up is no match. Its humans are everybody who entered the game during
// it - whoever joined during the warm-up from its start - and everybody still in from the match before it; their seconds are their time in it. Their
// frags come from the game's kill lines (LogFrag: kills, assists, suicides, chickens), the most of them in it at once from who is in, Arms Race's winner
// from the knife kill that came with its end. Its humans are real humans alone: bots and CS2's other fake clients never enter the log as players with a
// Steam account, and the lab's fake players - who do, on a test copy or the rehearsal, through deploy's bridgepost (Test A to Test D, HUDtest) - are left
// out by their accounts (core's LabFakes). A match nobody human was in is no entry, nor is the end screen alone before a change (EndScreenSeconds). The live
// bridge feeds it from its log stream (HistoryIngest, HistoryFrag); --history-backfill feeds it the same events read back from the bridge's day logs and
// the game's console log, as far back as they reach - its matches from before the warm-up, counted from the map's start, their frags where a console log
// has the kill lines - and marks what they cannot tell (Missing).
using System.Globalization;
using System.Text.RegularExpressions;

sealed partial class MatchTracker
{
    /// <summary>The end screen of a match is the next one's start on that map, should the vote play it again: a change within this many seconds after it
    /// was only the screen, no match.</summary>
    public const double EndScreenSeconds = 150;

    /// <summary>A restart of the match by a player, this early in it, is still the same match (and every fairness restart was: the joiner started it over with
    /// everybody, before the warm-up took its place).</summary>
    public const double RestartGraceSeconds = 60;

    /// <summary>The least time its humans were in a match for it to be one: a map voted away the moment somebody came in is no match.</summary>
    public const double LeastSeconds = 60;

    /// <summary>A knife kill this close before Arms Race's Game Over is the one that won it (the last level is the knife; CS2 logs the kill and the end in the
    /// same second).</summary>
    public const double WinningKillSeconds = 3;

    /// <summary>Metres in a unit of the game's world (an inch).</summary>
    public const double MetresPerUnit = 0.0254;

    public string Source = "live";
    public Action<HudHistory.Entry>? Finished;

    /// <summary>Every map starts with a warm-up (the live bridge's, WarmUp.cs, since release 219) - CS2's own, or the match itself ended by one restart (the
    /// restart kind, release 220): its match counts from the warm-up's end (WarmUpEnded), a map that ends during it is no match, nor a match end during it
    /// (MatchEnded's inWarmUp), and whoever joined during it is in it from its start. False for the backfill: the logs it reads are from before the warm-up,
    /// whose matches counted from the map's start.</summary>
    public bool UsesWarmUp;

    /// <summary>Every match's frags are recorded (the live bridge reads every kill line): a match with none has players with none, not players with none known.
    /// The backfill records a match's frags only where its logs have kill lines (a console log).</summary>
    public bool RecordsFrags;

    /// <summary>The live record: a bridge that starts inside a map - restarted, deployed - records nothing until the next map's start, where everybody enters
    /// again and its match is known from its start: a bridge restart writes no match (the lab round on rel-220's ce3e9af: one wrote a match with no map at
    /// the server's next stop). False for the backfill, whose first log may begin inside a match: that match begins where it finds it, not known in full.</summary>
    public bool FromMapStart;

    sealed class FragCount
    {
        public int Kills, Deaths, Assists, Hs, Knife, Taser, Noscope, Wall, Air, Chickens, Suicides, Streak, Run;
        public double Far;

        public HudHistory.Frags Done() => new(Kills, Deaths, Assists, Hs, Knife, Taser, Noscope, Wall, Air, Chickens, Suicides, Far, Math.Max(Streak, Run));
    }

    sealed class Match
    {
        public DateTimeOffset Start;
        public long MapId;
        public string MapName = "", LogName = "", Mode = "";
        public bool AfterEnd;   // begun at the last match's end screen: the next match on that map, should it be played again
        public bool Warming;    // in the map's warm-up: no match yet (UsesWarmUp)
        public DateTimeOffset LastLeft; // when the last human to leave it left
        public int Most;        // the most humans in it at once
        public long Winner;     // Arms Race's winner
        public bool FragsSeen;  // a frag of it was read
        public readonly SortedSet<string> Missing = new(StringComparer.Ordinal);
        public readonly Dictionary<long, (string Name, double Seconds, DateTimeOffset First)> Roster = new();
        public readonly Dictionary<long, FragCount> Frags = new();
        public readonly Dictionary<(long Killer, long Victim), int> Pairs = new();
    }

    readonly Dictionary<long, (string Name, DateTimeOffset Since)> present = new();
    Match? cur;

    /// <summary>The names the server shows for its players, as the game's own word gives them - their chat lines and the plugin's clicks - by account: the names
    /// an entry keeps (Shown). The game's "connected" and "entered the game" lines keep the name a game was given at its connect, which a test name the server
    /// goes by (the lab's OsDocName on a copy) never reaches; production's are the same names.</summary>
    readonly Dictionary<long, string> shown = new();
    DateTimeOffset lastStart = DateTimeOffset.MinValue;
    string lastLog = "";
    (long Account, DateTimeOffset At) lastKnife = (0, DateTimeOffset.MinValue);

    /// <summary>A match is on, as far as the record knows (a map in its warm-up has none yet).</summary>
    public bool Running => cur is { Warming: false };

    /// <summary>The map that is on is in its warm-up, as far as the record knows.</summary>
    public bool Warming => cur is { Warming: true };

    /// <summary>When the match that is on began; null with none on (its map's start during the warm-up).</summary>
    public DateTimeOffset? StartedAt => cur?.Start;

    /// <summary>Nothing known: no match on, nobody in (a new record; the self-test's sections).</summary>
    public void Clear()
    {
        present.Clear();
        cur = null;
        lastStart = DateTimeOffset.MinValue;
        lastLog = "";
        lastKnife = (0, DateTimeOffset.MinValue);
        shown.Clear();
    }

    /// <summary>The server shows this player under this name (a chat line, a click): the name every entry they are in keeps from now on.</summary>
    public void Shown(long account, string name)
    {
        if (account > 0 && name.Trim() != "") shown[account] = name;
    }

    string NameOf(long account, string logged) => shown.TryGetValue(account, out var name) ? name : logged;

    /// <summary>The Workshop id of the map that is on, as far as the record knows: 0 for an official map, or one it was not told.</summary>
    public long MapId => cur?.MapId ?? 0;

    /// <summary>The mode of the match that is on, as far as the record knows ("" with none on).</summary>
    public string Mode => cur?.Mode ?? "";

    /// <summary>Something the record cannot tell about the match that is on (a backfill's: "map id" for a map start its log does not name).</summary>
    public void Note(string missing) => cur?.Missing.Add(missing);

    /// <summary>The next match on the same map: its map as the last one's, a Workshop id it lacked lacking still; whoever is in, in it.</summary>
    Match Next(Match? m, DateTimeOffset at, bool afterEnd, string logName = "", string mode = "")
    {
        var n = new Match { Start = at, MapId = m?.MapId ?? 0, MapName = m?.MapName ?? "", LogName = m?.LogName ?? logName, Mode = m?.Mode ?? mode, AfterEnd = afterEnd, Most = present.Count };
        if (m?.Missing.Contains("map id") == true) n.Missing.Add("map id");
        return n;
    }

    /// <summary>The humans in the game now, as the record knows them.</summary>
    public int Present => present.Count;

    /// <summary>A map started: the match that was on is over (the server started, or the map changed), and the new map's first match begins - with the warm-up,
    /// once it ends. Everybody reconnects into it, and is in it once they enter. The second line of the same load (CS2 logs "Loading map" and "Started:")
    /// changes nothing.</summary>
    public void MapStarted(DateTimeOffset at, string logName, long mapId, string mapName, string mode, bool serverStart = false)
    {
        if ((at - lastStart).TotalSeconds < 10 && (logName == "" || logName.Equals(lastLog, StringComparison.OrdinalIgnoreCase))) { Name(mapId, mapName, mode); return; }
        Close(at, serverStart ? HudHistory.ByServerStart : HudHistory.ByMapChange);
        present.Clear();
        lastStart = at;
        lastLog = logName;
        cur = new Match { Start = at, MapId = mapId, MapName = mapName, LogName = logName, Mode = mode, Warming = UsesWarmUp };
    }

    /// <summary>The map's warm-up is over, as a look of the bridge's finds it - CS2's warm-up off, or the restart kind's round start after the plugin's
    /// os_restart 1 - whether the bridge asked or its clock ran out: the real match starts now, with everybody in the game in it from this
    /// moment - whoever joined during the warm-up is present at its start - and their seconds count from here.</summary>
    public void WarmUpEnded(DateTimeOffset at)
    {
        if (cur is not { Warming: true } m) return;
        m.Warming = false;
        m.Start = at;
        foreach (var (account, p) in present.ToList())
        {
            present[account] = (p.Name, at);
            Touch(m, account, p.Name, at, 0);
        }
        m.Most = present.Count;
    }

    /// <summary>No warm-up on this map after all (a plugin without it, or none began): the match runs as CS2 has it, from the map's start as before.</summary>
    public void NoWarmUp()
    {
        if (cur is not { Warming: true } m) return;
        m.Warming = false;
        foreach (var (account, p) in present.ToList()) Touch(m, account, p.Name, p.Since > m.Start ? p.Since : m.Start, 0);
        m.Most = Math.Max(m.Most, present.Count);
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
    /// score, and in Arms Race its winner - the human whose knife kill came with the end; its end screen is the next match's start on that map, should the
    /// vote play it again. <paramref name="inWarmUp"/>: the bridge saw the warm-up on as the match ended - the restart kind's waiting time, where the match
    /// itself runs (Arms Race, Deathmatch), won or run out: no match, and the next match on the map, from this end, is the real one (WarmUp.cs) - unless
    /// <paramref name="warmUpGoesOn"/>: nobody had spawned, and the waiting time goes on into the next match, as it does on an empty map (nobody in) whose
    /// match runs out during it: the warm-up's end starts the real match, as ever.</summary>
    public void MatchEnded(DateTimeOffset at, string gameOver, bool inWarmUp = false, bool warmUpGoesOn = false)
    {
        if (cur == null && FromMapStart) return; // a bridge started inside this map: the next match is known from the next map's start
        var (mode, logName, score) = ReadGameOver(gameOver);
        var idle = UsesWarmUp && cur is { Warming: true } && present.Count == 0; // an empty map in its warm-up, its match run out: nobody's, and the warm-up goes on
        if (inWarmUp && cur != null) cur.Warming = true; // the waiting time's win counts for nothing, whatever the record thought of it
        else if (idle) { }
        else if (cur is { Warming: true } unseen) // CS2 ends no match during its own warm-up, and the bridge saw none on: its end went by unseen - the match counts from the map's start, its start not known
        {
            NoWarmUp();
            unseen.Missing.Add("start");
        }
        if (cur != null && mode != "") cur.Mode = mode;
        if (cur is { Warming: false } m && mode == Modes.ArmsRaceId && lastKnife.Account > 0 && (at - lastKnife.At).TotalSeconds is >= 0 and <= WinningKillSeconds)
            m.Winner = lastKnife.Account;
        var was = cur;
        Close(at, HudHistory.ByMatchEnd, score);
        cur = Next(was, at, afterEnd: true, logName, mode);
        if (warmUpGoesOn || idle) cur.Warming = true; // the next match is the waiting time still
    }

    /// <summary>The match restarted (the bridge's os_restart). A fairness restart (before the warm-up took its place) was the same match, the joiner in it; a
    /// player's Restart match ends it and starts the next on that map - unless the match had barely begun (RestartGraceSeconds).</summary>
    public void Restarted(DateTimeOffset at, bool fair)
    {
        if (cur is not { Warming: false } m || fair || (at - m.Start).TotalSeconds < RestartGraceSeconds) return;
        Close(at, HudHistory.ByRestart);
        cur = Next(m, at, afterEnd: false);
    }

    /// <summary>A human entered the game. With no match known - the record began, or came back, inside one - a match begins here, its start and its
    /// players not known in full; or, for the live record (FromMapStart), nothing until the next map's start. During the warm-up they are present, and
    /// in the match once it starts. A bot (account 0) or one of the lab's fake players (LabFakes) is no human: never in it.</summary>
    public void Entered(DateTimeOffset at, long account, string name)
    {
        if (account <= 0 || LabFakes.IsFake(account) || cur == null && FromMapStart) return;
        if (cur == null) { cur = new Match { Start = at }; cur.Missing.Add("start"); cur.Missing.Add("players"); }
        present[account] = present.TryGetValue(account, out var p) ? (name, p.Since) : (name, at);
        if (cur.Warming) return;
        Touch(cur, account, name, present[account].Since, 0);
        cur.Most = Math.Max(cur.Most, present.Count);
    }

    /// <summary>A human who was not known to be in acted (chat): in from now - the record missed their entering.</summary>
    public void Seen(DateTimeOffset at, long account, string name)
    {
        if (account <= 0 || LabFakes.IsFake(account) || present.ContainsKey(account)) return;
        Entered(at, account, name);
        if (cur is { Warming: false }) cur.Missing.Add("players");
    }

    /// <summary>A human left the game (any disconnect: a map change's reconnect enters again). One who leaves during the warm-up was never in the match.</summary>
    public void Left(DateTimeOffset at, long account)
    {
        if (!present.Remove(account, out var p) || cur is not { Warming: false } m) return;
        var since = p.Since > m.Start ? p.Since : m.Start;
        Touch(m, account, p.Name, since, Math.Max(0, (at - since).TotalSeconds));
        m.LastLeft = at;
    }

    // ---- the frags: the game's kill lines (LogFrag), counted in the match that is on - none during the warm-up ----

    FragCount? FragsOf(Match m, long account) => account > 0 ? m.Frags.TryGetValue(account, out var f) ? f : m.Frags[account] = new FragCount() : null;

    /// <summary>A kill: by a human or a bot (account 0) of a human or a bot, with this weapon, the game's marks ("headshot", "penetrated", "noscope",
    /// "attackerinair" ...) and over this many metres.</summary>
    public void Killed(DateTimeOffset at, long killer, long victim, string weapon, string marks, double metres)
    {
        var knife = IsKnife(weapon);
        if (knife) lastKnife = (killer, at);
        if (cur is not { Warming: false } m) return;
        m.FragsSeen = true;
        if (FragsOf(m, killer) is { } k)
        {
            k.Kills++;
            if (marks.Contains("headshot", StringComparison.Ordinal)) k.Hs++;
            if (marks.Contains("penetrated", StringComparison.Ordinal)) k.Wall++;
            if (marks.Contains("noscope", StringComparison.Ordinal)) k.Noscope++;
            if (marks.Contains("attackerinair", StringComparison.Ordinal)) k.Air++;
            if (knife) k.Knife++;
            if (weapon.Equals("taser", StringComparison.OrdinalIgnoreCase)) k.Taser++;
            if (metres > k.Far) k.Far = metres;
            k.Run++;
            k.Streak = Math.Max(k.Streak, k.Run);
        }
        if (FragsOf(m, victim) is { } v)
        {
            v.Deaths++;
            v.Run = 0;
        }
        if (killer > 0 && victim > 0 && killer != victim) m.Pairs[(killer, victim)] = m.Pairs.GetValueOrDefault((killer, victim)) + 1;
    }

    /// <summary>A human's assist in a kill.</summary>
    public void Assisted(DateTimeOffset at, long account)
    {
        if (cur is not { Warming: false } m || FragsOf(m, account) is not { } a) return;
        m.FragsSeen = true;
        a.Assists++;
    }

    /// <summary>A human killed themselves (a fall, their own grenade): a suicide, and a death.</summary>
    public void Suicide(DateTimeOffset at, long account)
    {
        if (cur is not { Warming: false } m || FragsOf(m, account) is not { } s) return;
        m.FragsSeen = true;
        s.Suicides++;
        s.Deaths++;
        s.Run = 0;
    }

    /// <summary>A human killed a chicken.</summary>
    public void Chicken(DateTimeOffset at, long account)
    {
        if (cur is not { Warming: false } m || FragsOf(m, account) is not { } c) return;
        m.FragsSeen = true;
        c.Chickens++;
    }

    /// <summary>Whether a weapon of a kill line is a knife: Valve's knives and every skin of one, Arms Race's golden knife among them.</summary>
    public static bool IsKnife(string weapon) => weapon.Contains("knife", StringComparison.OrdinalIgnoreCase) || weapon.Equals("bayonet", StringComparison.OrdinalIgnoreCase);

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
        if (m.Warming) return; // the map ended during its warm-up: no match
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
        var frags = RecordsFrags || m.FragsSeen;
        var players = m.Roster.Select(r => new HudHistory.Player(r.Key, NameOf(r.Key, r.Value.Name), (int)Math.Round(r.Value.Seconds), r.Value.First,
                frags ? m.Frags.TryGetValue(r.Key, out var f) ? f.Done() : new HudHistory.Frags() : null))
            .OrderBy(p => p.First).ThenBy(p => p.Account).ToList();
        var inIt = m.Roster.Keys.ToHashSet();
        var pairs = frags ? m.Pairs.Where(p => inIt.Contains(p.Key.Killer) && inIt.Contains(p.Key.Victim)).OrderByDescending(p => p.Value).ThenBy(p => p.Key.Killer).ThenBy(p => p.Key.Victim)
            .Select(p => new HudHistory.Pair(p.Key.Killer, p.Key.Victim, p.Value)).ToList() : null;
        return new HudHistory.Entry(m.Start, at, m.MapId, m.MapName != "" ? m.MapName : m.LogName, m.LogName, m.Mode, by, score, players, m.Missing.ToList(), Source,
            Math.Max(m.Most, players.Count > 0 ? 1 : 0), inIt.Contains(m.Winner) ? m.Winner : 0, pairs);
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

    static readonly MatchTracker MatchHistory = new() { Finished = KeepMatch, UsesWarmUp = true, RecordsFrags = true, FromMapStart = true };

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
        StatsDirty(); // Stats.cs
        Log($"history: {e.MapName} ({e.Mode}), {e.Start:HH:mm}-{e.End:HH:mm}, {e.EndedBy}, {e.Players.Count} human(s): {HudHistory.Who(e, 8)}");
    }

    /// <summary>The block the plugin fetches (Hud.HistoryPath): made on the event loop whenever the history moves (a full block takes tens of milliseconds to
    /// write), and served as it stands from the HTTP thread, as the Sync page's block is.</summary>
    static volatile string HistoryText = new HudHistory().Encode();

    /// <summary>Set by OnMapStart: the start it just took was the server's own (a start of the start map nobody asked for).</summary>
    static bool LastStartWasServerStart;

    // ---- the frags: the game's kill lines, which the rest of the bridge does not read ----

    /// <summary>A frag line of the game's log: a kill ("kill": the killer's and the victim's accounts, 0 for a bot; the weapon; the game's marks such as
    /// "headshot penetrated"; the metres between them), an assist ("assist"), a suicide ("suicide") or a chicken killed ("chicken"), the human's account as
    /// Killer.</summary>
    sealed record LogFrag(string What, long Killer, long Victim, string Weapon, string Marks, double Metres);

    const string FragWho = @"""(?:.+?)<\d+><(\[U:1:\d+\]|BOT|STEAM_[0-5]:[01]:\d+)><[^>]*>""";
    [GeneratedRegex("^" + FragWho + @" \[(-?\d+) (-?\d+) (-?\d+)\] killed " + FragWho + @" \[(-?\d+) (-?\d+) (-?\d+)\] with ""([^""]*)""(?: \(([^)]*)\))?$")] private static partial Regex KillRx();
    [GeneratedRegex("^" + FragWho + @" (?:flash-)?assisted killing """)] private static partial Regex AssistRx();
    [GeneratedRegex("^" + FragWho + @" \[-?\d+ -?\d+ -?\d+\] committed suicide with """)] private static partial Regex SuicideRx();
    [GeneratedRegex("^" + FragWho + @" \[-?\d+ -?\d+ -?\d+\] killed other ""chicken<")] private static partial Regex ChickenRx();

    /// <summary>A line of the game's log as a frag, or null for any other line: "Alice&lt;2&gt;&lt;[U:1:1]&gt;&lt;CT&gt;" [486 -150 2] killed "Bob&lt;3&gt;&lt;BOT&gt;&lt;TERRORIST&gt;"
    /// [-466 -1524 2] with "scar20" (headshot), and the assist's, the suicide's and the chicken's lines (their shapes in docs/reference.md).</summary>
    static LogFrag? FragOf(string line)
    {
        var msg = PrefixRx().Replace(line, "", 1).Trim();
        if (!msg.StartsWith('"')) return null;
        static long Account(string id) => AccountId(id) is > 0 and var a ? a : 0;
        if (KillRx().Match(msg) is { Success: true } k)
        {
            double N(int g) => double.Parse(k.Groups[g].Value, CultureInfo.InvariantCulture);
            var (dx, dy, dz) = (N(2) - N(6), N(3) - N(7), N(4) - N(8));
            return new("kill", Account(k.Groups[1].Value), Account(k.Groups[5].Value), k.Groups[9].Value, k.Groups[10].Value,
                Math.Sqrt(dx * dx + dy * dy + dz * dz) * MatchTracker.MetresPerUnit);
        }
        if (AssistRx().Match(msg) is { Success: true } a) return Account(a.Groups[1].Value) is > 0 and var who ? new("assist", who, 0, "", "", 0) : null;
        if (SuicideRx().Match(msg) is { Success: true } s) return Account(s.Groups[1].Value) is > 0 and var who2 ? new("suicide", who2, 0, "", "", 0) : null;
        if (ChickenRx().Match(msg) is { Success: true } c) return Account(c.Groups[1].Value) is > 0 and var who3 ? new("chicken", who3, 0, "", "", 0) : null;
        return null;
    }

    /// <summary>A frag into a match record.</summary>
    static void Frag(MatchTracker t, DateTimeOffset at, LogFrag f)
    {
        switch (f.What)
        {
            case "kill": t.Killed(at, f.Killer, f.Victim, f.Weapon, f.Marks, f.Metres); break;
            case "assist": t.Assisted(at, f.Killer); break;
            case "suicide": t.Suicide(at, f.Killer); break;
            case "chicken": t.Chicken(at, f.Killer); break;
        }
    }

    /// <summary>A frag line of the live log stream, for the history (LogStream.cs hands it here before anything else reads the line).</summary>
    static void HistoryFrag(LogFrag f) => Frag(MatchHistory, HistoryNow(), f);

    /// <summary>The bridge settled the mode of the map that is on after its start (ModeSwitch.cs: the plugin's word, the convars, a map that came up in another
    /// mode): the match on it runs in that mode. A map start nobody asked for - an operator's changelevel, a server that rotated by itself - takes the mode
    /// the bridge last knew until the probe says otherwise, and an entry closed before any Game Over put it right kept that one.</summary>
    static void HistoryModeSettled() => MatchHistory.Name(0, "", S.Mode);

    /// <summary>A line of the game's log, for the history: who entered, acted and left, the maps' starts, the matches' ends, the quit. <paramref name="inWarmUp"/>:
    /// a match end the bridge saw during the warm-up (LogStream.cs looks before OnMatchEnd ends it); <paramref name="warmUpGoesOn"/>: before anybody spawned,
    /// so the waiting time goes on into the next match.</summary>
    static void HistoryIngest(LogEvent e, bool inWarmUp = false, bool warmUpGoesOn = false)
    {
        var at = HistoryNow();
        switch (e.Kind)
        {
            case Kind.Entered: MatchHistory.Entered(at, AccountId(e.SteamId), e.Name); break;
            case Kind.Chat: MatchHistory.Seen(at, AccountId(e.SteamId), e.Name); break;
            case Kind.Disconnected: MatchHistory.Left(at, AccountId(e.SteamId)); break;
            case Kind.MapStart: MatchHistory.MapStarted(at, S.LogName ?? e.Text, S.Map?.Id ?? 0, S.Map?.Name ?? "", S.Mode, LastStartWasServerStart); break;
            case Kind.MatchEnd: MatchHistory.MatchEnded(at, e.Text, inWarmUp, warmUpGoesOn); break;
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
    sealed record BackEvent(DateTimeOffset At, int Source, int Seq, string Kind, string Name, long Account, string Text, LogFrag? Frag = null);

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
        events = events.Where(e => until == null || e.At < until.Value).Where(e => e.Source == 0 || e.Kind == "frag" || e.At < bridgeFrom || e.At > bridgeTo) // the frags only a console log has
            .GroupBy(e => (e.At, e.Kind, e.Account, e.Kind == "map" ? e.Text.ToLowerInvariant() : e.Kind == "frag" ? e.Seq.ToString(CultureInfo.InvariantCulture) : ""))
            .Select(g => g.OrderBy(e => e.Source).First()) // one moment told twice; every frag its own
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
                case "frag": Frag(t, e.At, e.Frag!); break;
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
            if (FragOf(logLine) is { } frag) return new(at, 1, ++seq, "frag", "", frag.Killer, "", frag);
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
