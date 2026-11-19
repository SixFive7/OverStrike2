// The fairness restart: no mode has a warmup, so a match is on the moment the first player is in, and whoever loads the map more slowly, or joins later,
// comes into a match that is already on - at level 0 in Arms Race, a round in, or a minute of the match's time gone. So every human who joins a match in
// its first minute starts it over, in every mode, as often as players join: up to 9 restarts in a 10-player match, known and desired.
//
//   - the match runs (Running): from its start - a round start out of warmup with no round played yet: the map's first, a Restart match, a fairness
//     restart, the game commencing (MatchStarted) - when a human is on a team then; with none, from the first human's spawn;
//   - a join: a human's first spawn on a team since they connected or the map loaded. A respawn, the spawn of a restart and a team switch are none; the
//     reconnect of a map change or of the HUD addon's download is no new connect (Reconnects: the bridge's log stream and the plugin decide it alike). Never a bot;
//   - the ask (Spawned answers true): a join while another human is on a team and the match has run less than WindowSeconds. The first human in starts
//     the match and asks nothing;
//   - what follows is the bridge's (Restarts.cs, FairRestart): CountdownSeconds on the countdown banner ("The match restarts for fairness - Bob is in"),
//     then the plugin's os_restart. A join while that counts down, or as it goes out, folds into it: one restart, and the banner names them all. The
//     restart's round start is a new start, so the window opens again. None while a vote is on, a map change counts down or loads, the server's restart
//     counts down, or the match has just ended;
//   - while anybody is still joining - connecting, at CS2's prompt to confirm a first Workshop download (which drops them after 120 s unanswered),
//     downloading the map or loading it, as everybody does after a map change, or in the game and not yet spawned on a team (CS2's team menu after every
//     load) - the restart waits for them, so the players in already start over once for all of them, not once each; HoldMostSeconds at the most, so a
//     joiner stuck there does not hold it for good. Who is joining (the bridge's FairWait): the plugin's os_joining (its controllers connecting or
//     reconnecting, and the humans in the game who have not spawned on a team since they entered it, SpawnMostSeconds at the most: StillToSpawn), the
//     server's own client list (status: everybody not yet active) and the bridge's log stream ("connected", not yet "entered the game"; and everybody on
//     the way back through a reconnect - a map change's, the HUD addon's - from its departure to their "entered the game", whatever the server's list
//     says meanwhile: a game that downloads the map itself after the prompt, or crashes, is off the server and writes no line). The prompt's joiner shows
//     only in the last two: the game hears of a client only once it has every addon, so the plugin has no callback and no controller for it, and CS2's drop
//     of it at PromptSeconds writes no "disconnected" line - status lacking it for DropConfirmSeconds ends it (never one on the way back), and
//     SignOnMostSeconds after its "connected" or its departure in any case. Their own join, as they come in, folds into it.
//
// Shared like Hud.cs: the plugin feeds it the game (plugin/OverStrike2/EarlyJoinWatch.cs) and the bridge's --selftest drives it on a handed clock. Plain BCL only.
public sealed class EarlyJoin
{
    /// <summary>The plugin's word to the bridge on /act when the plan asks: never a chat command (!fair is How it works' alias), never a click.</summary>
    public const string Command = "!latejoin";

    /// <summary>A join this soon after the match started running (or last restarted) restarts it.</summary>
    public const int WindowSeconds = 60;

    /// <summary>The countdown banner's seconds before the fairness restart goes out.</summary>
    public const int CountdownSeconds = 5;

    /// <summary>The most names the banner and the line give; the rest are "N more".</summary>
    public const int NamesShown = 3;

    /// <summary>The plugin's server command the bridge asks before a fairness restart goes out (and each second while it waits): the humans still joining.</summary>
    public const string JoiningCommand = "os_joining";

    /// <summary>How long a fairness restart waits at the most for the humans still joining, from when it began to wait: a joiner stuck at the prompt or in a long
    /// download does not hold it for good. CS2 drops an unanswered prompt after 120 s by itself.</summary>
    public const int HoldMostSeconds = 120;

    /// <summary>os_joining's reply: "os_joining: 2" and one joiner's name a line - names as the plugin has them, a line each, so no name can break the list.</summary>
    public static string JoiningReply(IEnumerable<string> names)
    {
        var list = names.Select(n => Hud.Clean(n, 32)).Select(n => n == "" ? "?" : n).ToList();
        return $"{JoiningCommand}: {list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}" + string.Concat(list.Select(n => "\n" + n));
    }

    /// <summary>Who os_joining says is still joining, or null when the reply is none of its: no answer, or a plugin without the command ("Unknown command") -
    /// then nothing is waited for, as before it.</summary>
    public static List<string>? ReadJoining(string? reply)
    {
        if (reply == null) return null;
        var lines = reply.Replace("\r", "").Split('\n');
        var at = Array.FindIndex(lines, l => l.StartsWith(JoiningCommand + ": ", StringComparison.Ordinal));
        if (at < 0 || !int.TryParse(lines[at][(JoiningCommand.Length + 2)..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
        var names = lines.Skip(at + 1).Select(l => l.Trim()).Where(l => l != "").Take(n).ToList();
        while (names.Count < n) names.Add("?");
        return names;
    }

    /// <summary>How long CS2 keeps a client in the connected sign-on state - at its prompt to confirm a first Workshop download, unanswered - before it drops it
    /// (NETWORK_DISCONNECT_TIMEDOUT): 120 s, measured twice by the live lab, with MultiAddonManager's own time-out off. The drop writes no "disconnected" line.</summary>
    public const int PromptSeconds = 120;

    /// <summary>How long the bridge counts a joiner only its log stream knows of ("connected", and no "entered the game" or "disconnected" since; or on the way
    /// back through a reconnect, from its departure) when the server's own client list cannot say: the prompt's PromptSeconds and a margin. Whoever gets further
    /// connects again, or shows in status and os_joining.</summary>
    public const int SignOnMostSeconds = PromptSeconds + 30;

    /// <summary>How long the server's own list must lack a joiner, read after read, before the bridge ends their sign-on as gone before they were in: the log
    /// stream can say a reconnect's departure a moment after the list has lost it - the HUD addon's, whose next "connected" is a second or two away
    /// (seen live on 2026-11-18: a joiner ended so in the same second as their departure's line).</summary>
    public const int DropConfirmSeconds = 5;

    /// <summary>How long a human in the game who has not yet spawned on a team counts as still joining, from their entering it (StillToSpawn): CS2's team menu
    /// after every load, or a game still finishing its load once the server has it in. The longest seen live from entering to the first spawn, 2026-11-12 to
    /// 18: 17 s; a joiner past this - a spectator, an absent player at the menu - holds nothing more.</summary>
    public const int SpawnMostSeconds = 30;

    /// <summary>Whether a departure, by its reason as CS2 names it, is a reconnect and not a leaving: a reason with SHUTDOWN in it - NETWORK_DISCONNECT_SHUTDOWN at
    /// a map change, NETWORK_DISCONNECT_LOOPSHUTDOWN when MultiAddonManager has a joining game reconnect for the HUD addon. Every other reason is a leaving,
    /// NETWORK_DISCONNECT_LOOPDEACTIVATE too: the two seen live, 2026-11-12 and 16, were games that went - one came back 11 minutes later, one never. The
    /// bridge's log stream (Players.cs) and the plugin (EarlyJoinWatch) both decide by this one rule.</summary>
    public static bool Reconnects(string reason) => reason.Contains("SHUTDOWN", StringComparison.Ordinal);

    /// <summary>The plugin's server command for the fairness restart's state (EarlyJoinWatch): os_fair says where the map stands, os_fair live starts the match
    /// running now, and os_fair bridge is the bridge telling it whom its log stream has signing on (BridgeViewCommand), which os_fair then shows (ViewLine).</summary>
    public const string FairCommand = "os_fair";
    public const string BridgeWord = "bridge";
    public const string StatusWord = "status"; // in os_fair bridge's arguments: what follows is status's joiners (no hex has an s, t or u)

    /// <summary>A human in the server's own client list (status's players table): the name, and the state as the server has it - "active" once in the game,
    /// "connecting" from the connect until then (CS2's first-download prompt, the download, the map's load, a map change's reconnect).</summary>
    public sealed record StatusClient(string Name, string State)
    {
        public bool Joining => State != "active";
    }

    static readonly System.Text.RegularExpressions.Regex StatusRow = new(@"^\s*(\d+)\s+(\S+)\s+\d+\s+\d+\s+([a-z]+)\s+\d+\s*\S*\s+'(.*)'\s*$");

    /// <summary>The humans in status's players table ("---------players--------", its header, a line a client, "#end"), as CS2 writes it: the bots (time BOT)
    /// and the handshake's placeholder (65535 [NoChan] challenging) left out. null when the reply has no such table: no answer, an error, a short status.</summary>
    public static List<StatusClient>? ReadStatus(string? reply)
    {
        if (reply == null) return null;
        var lines = reply.Replace("\r", "").Split('\n');
        var at = Array.FindIndex(lines, l => l.Trim() == "---------players--------");
        if (at < 0) return null;
        var clients = new List<StatusClient>();
        foreach (var line in lines.Skip(at + 1))
        {
            if (line.StartsWith("#end", StringComparison.Ordinal)) break;
            if (StatusRow.Match(line) is not { Success: true } m || m.Groups[2].Value is "BOT" or "[NoChan]") continue;
            clients.Add(new(m.Groups[4].Value, m.Groups[3].Value));
        }
        return clients;
    }

    /// <summary>What the bridge last told the plugin (os_fair bridge): whom its log stream has signing on, the humans the server's own list had not yet active at
    /// its last reading (null: not read - no players table, or nobody has signed on since the bridge started), and when it said so.</summary>
    public sealed record BridgeView(IReadOnlyList<string> Log, IReadOnlyList<string>? Status, DateTime At);

    /// <summary>The bridge's word to the plugin: os_fair bridge, the names its log stream has signing on, then - once read - the word status and status's joiners;
    /// each name the hex of its UTF-8, so no name can end or change the command (a ';', a '"', "//"). 16 a list at the most.</summary>
    public static string BridgeViewCommand(IEnumerable<string> log, IEnumerable<string>? status = null) =>
        string.Join(' ', new[] { FairCommand, BridgeWord }.Concat(Hex(log)).Concat(status == null ? [] : new[] { StatusWord }.Concat(Hex(status))));

    static IEnumerable<string> Hex(IEnumerable<string> names) => names.Take(16).Select(n => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Hud.Clean(n, 32))));

    static string? Unhex(string a) { try { return System.Text.Encoding.UTF8.GetString(Convert.FromHexString(a)); } catch (FormatException) { return null; } }

    /// <summary>os_fair bridge's arguments, after the word, read back: the log stream's names, and status's after its word (null without it); an argument that
    /// is not hex is left out.</summary>
    public static (List<string> Log, List<string>? Status) ReadBridgeView(IReadOnlyList<string> args)
    {
        var at = args.ToList().FindIndex(a => a.Equals(StatusWord, StringComparison.OrdinalIgnoreCase));
        return ([.. (at < 0 ? args : args.Take(at)).Select(Unhex).OfType<string>()], at < 0 ? null : [.. args.Skip(at + 1).Select(Unhex).OfType<string>()]);
    }

    static string Ago(DateTime at, DateTime now) => $"{Math.Max(0, (int)(now - at).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)} s ago";

    /// <summary>os_fair's opening: whom a fairness restart's wait would count now, each with who names them - the plugin (its controllers connecting or
    /// reconnecting, as os_joining), status (the server's own list: not yet active) and the log stream (the bridge's: "connected", no "entered the game" yet) -
    /// and how old the bridge's word on the last two is. A joiner at CS2's first-download prompt is named by status and the log stream, never by the plugin.</summary>
    public static string JoiningLine(IReadOnlyList<string> plugin, BridgeView? bridge, DateTime now)
    {
        var named = plugin.Select(n => (Name: n, By: "the plugin")).Concat(bridge?.Status?.Select(n => (Name: n, By: "status")) ?? [])
            .Concat(bridge?.Log.Select(n => (Name: n, By: "log stream")) ?? []);
        var who = named.GroupBy(x => x.Name, StringComparer.Ordinal).Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.By).Distinct())})").ToList();
        var by = bridge == null ? "by the plugin; the bridge has told it nothing yet"
            : bridge.Status == null ? $"by the plugin and the bridge's log stream (status not read yet), told {Ago(bridge.At, now)}"
            : $"by the plugin, status and the bridge's log stream, told {Ago(bridge.At, now)}";
        return $"still joining: {(who.Count == 0 ? "nobody" : string.Join(", ", who))} - {by}";
    }

    /// <summary>os_fair's last part: CounterStrikeSharp's connect callbacks on this map (OnClientConnect, which comes only once a client has every addon - none
    /// for one at the prompt; the live lab's run AY saw 0 there) and the last of them.</summary>
    public static string ConnectsLine((string Name, DateTime At)? lastConnect, int connects, DateTime now) =>
        connects == 0 || lastConnect is not { } c ? "connect callbacks this map: 0" : $"connect callbacks this map: {connects} (the last: {c.Name}, {Ago(c.At, now)})";

    /// <summary>The line for everybody as the restart begins to wait for those still joining: who is in, and who it waits for.</summary>
    public static string WaitLine(string names, int count, IReadOnlyList<string> joining) =>
        $"The match restarts for fairness once everybody is in - {M.N(names)} {(count > 1 ? "are" : "is")} in, waiting for {M.N(Names(joining))}";

    /// <summary>The line for everybody in chat and on the board as the countdown starts, or as another join folds into it with seconds left.</summary>
    public static string Line(string names, int count, int seconds = CountdownSeconds) =>
        $"The match restarts for fairness in {M.V(MapVote.Left(seconds))} - {M.N(names)} {(count > 1 ? "are" : "is")} in";

    /// <summary>The countdown banner's title while it counts down (HudView.CountdownOf).</summary>
    public static string BannerTitle(string names, int count = 1) => $"The match restarts for fairness - {names} {(count > 1 ? "are" : "is")} in";

    /// <summary>Who came in, as the banner and the line say it: "Bob", "Bob and Cas", "Bob, Cas and Dee", "Bob, Cas, Dee and 2 more".</summary>
    public static string Names(IReadOnlyList<string> names)
    {
        if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
        if (names.Count > NamesShown) return $"{string.Join(", ", names.Take(NamesShown))} and {names.Count - NamesShown} more";
        return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    readonly HashSet<long> joined = new();   // the humans who have spawned on a team since they connected or the map loaded
    readonly Dictionary<long, DateTime> entered = new(); // the humans in the game who have not spawned on a team since they entered it, and since when
    DateTime? running;                       // the match runs since then: its start with a human on a team, or the first human's spawn; null: nobody yet
    bool forced;                             // os_fair live: the next join counts as joining somebody
    string? skipped;                         // why this map has no fairness restart at all (the plugin loaded mid-map)

    /// <summary>A new map: nobody has joined it or entered it, and its match runs once somebody is in.</summary>
    public void MapStarted()
    {
        joined.Clear();
        entered.Clear();
        (running, forced, skipped) = (null, false, null);
    }

    /// <summary>Nothing on this map: it began before the plugin saw it (a reload), so nobody can tell who has joined.</summary>
    public void Skip(string why) => skipped = why;

    /// <summary>A human leaves for real (not the reconnect of a map change, nor the addon download's): when they come back, their first spawn is a join again.</summary>
    public void Left(long account)
    {
        joined.Remove(account);
        entered.Remove(account);
    }

    /// <summary>A human enters the game - connected in full: a new connect, or the reconnect of a map change. Still joining until their first spawn on a team,
    /// SpawnMostSeconds at the most (StillToSpawn); one who has spawned on a team on this map already is in.</summary>
    public void Entered(long account, DateTime now)
    {
        if (!joined.Contains(account)) entered[account] = now;
    }

    /// <summary>Of the humans in the game now (their accounts, as the plugin finds them connected), those who have not spawned on a team since they entered it,
    /// for less than SpawnMostSeconds: CS2's team menu after every load, or a game still finishing its load once the server has it in. The bridge's wait holds
    /// for them as for a game still loading (seen live on 2026-11-18: the wait ended as a joiner entered the game, and their spawn 16 s later restarted the
    /// match a second time). One whose entering the plan never heard of counts from its first sight here. None on a map the plugin loaded during.</summary>
    public List<long> StillToSpawn(IEnumerable<long> inGame, DateTime now)
    {
        var still = new List<long>();
        if (skipped != null) return still;
        foreach (var account in inGame)
        {
            if (account <= 0 || joined.Contains(account)) continue;
            if (!entered.TryGetValue(account, out var at)) entered[account] = at = now;
            if ((now - at).TotalSeconds < SpawnMostSeconds) still.Add(account);
        }
        return still;
    }

    /// <summary>A round start out of warmup with no round played yet - the match starts, or starts over (a restart, the fairness one's too) - with this many
    /// humans on a team. With any, the match runs from now; with none, from the first human's spawn - unless os_fair live's join is still to come: the game
    /// commencing as a lone test game picks a team must not undo it.</summary>
    public void MatchStarted(DateTime now, int humansOnTeam) => running = humansOnTeam > 0 || forced ? now : null;

    /// <summary>os_fair live, the client harness's: the match runs from now, and the next join counts as joining somebody - so a lone test game's spawn asks.</summary>
    public void Force(DateTime now) => (running, forced) = (now, true);

    /// <summary>A human spawns on a team; others: the other humans on a team now. True when it asks for the restart: their first spawn since they connected or
    /// the map loaded, somebody else plays, and the match has run less than WindowSeconds. A first spawn with nobody else playing starts the match running.</summary>
    public bool Spawned(long account, DateTime now, int others)
    {
        if (skipped != null || !joined.Add(account)) return false;
        entered.Remove(account); // in: no more still to spawn
        if (forced) (forced, others) = (false, Math.Max(others, 1));
        if (others == 0 || running == null)
        {
            running = now;
            return false;
        }
        return (now - running.Value).TotalSeconds < WindowSeconds;
    }

    /// <summary>The seconds the match has run, or null while nobody plays it.</summary>
    public double? Running(DateTime now) => running is { } at ? (now - at).TotalSeconds : null;

    /// <summary>For the console: where this map stands.</summary>
    public string Describe(DateTime now) =>
        skipped != null ? $"nothing on this map: {skipped}"
        : Running(now) is not { } s ? $"nobody plays the match yet; {joined.Count} human(s) joined this map"
        : s < WindowSeconds ? $"the match has run {s:0} s: a join in the next {WindowSeconds - s:0} s restarts it; {joined.Count} human(s) joined this map"
        : $"the match has run {s:0} s, past the first {WindowSeconds} s: a join plays on; {joined.Count} human(s) joined this map";
}
