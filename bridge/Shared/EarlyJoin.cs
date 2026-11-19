// The warm-up: no mode starts its match the moment the first player is in any more. Every map starts in CS2's own warm-up - everybody respawns, plays freely,
// and nothing counts - while everybody who is coming gets there, and the real match then starts for all of them at once, the one restart a map makes:
//
//   - its clock: WarmUpMostSeconds at the most, from the first human's spawn on the map - CS2's own warm-up clock, set then to end there, so the game's
//     own timer counts down to the latest start, and its own end starts the match then. A map nobody has spawned on yet sits in its warm-up;
//   - who is coming: whoever was on the map before and is on the way back through the change, and whoever connects during the warm-up. A real departure
//     takes a player out of it (Reconnects: the bridge's log stream and the plugin decide alike);
//   - it ends, at the first of: everybody coming has spawned on a team and at least two humans have, SettleSeconds later - one human alone is no "all", so a
//     friend who connects meanwhile still gets the same start; WarmUpMostSeconds after the first spawn, whoever is still coming then joining the match
//     that runs; !start, by anybody (a friend who is not coming). The two early ends are CS2's own warm-up end (mp_warmup_end), and neither comes while a vote
//     is open, a map change counts down or loads, or the server's restart counts down: they come as soon as those are over. The most is CS2's clock's own;
//   - after it nothing restarts the match: whoever comes later joins the match that runs;
//   - while it runs, in Arms Race and Wingman, a teammate's hit hurts at least as an enemy's does (TeamDamage): friendly fire on, every team-damage share
//     at least 1 - Arms Race's own Instakill, 100, stays - and each value it raised is put back at its end, unless something else changed it meanwhile.
//
// Who is coming, by any of three (the bridge's JoiningNow): the plugin's os_joining - its controllers connecting or reconnecting, and the humans in the game
// who have not spawned on a team since they entered it (CS2's team menu after every load), SpawnMostSeconds at the most (StillToSpawn); the server's own
// client list (status: everybody not yet active); and the bridge's log stream - "connected", not yet "entered the game", and everybody on the way back
// through a reconnect (a map change's, the HUD addon's) from its departure until "entered the game", whatever the server's list says meanwhile: a game
// that downloads the map itself after CS2's prompt, or crashes, is off the server and writes no line. The prompt's joiner shows only in the last two: the
// game hears of a client only once it has every addon, so the plugin has no callback and no controller for it, and CS2's drop of it at PromptSeconds writes
// no "disconnected" line - status lacking it for DropConfirmSeconds ends it (never one on the way back), and SignOnMostSeconds after its "connected" or its
// departure in any case.
//
// While it lasts the countdown banner says whom it waits for and counts down to the latest start (Eyebrow, Title, Line; HudView.CountdownOf). The plugin begins
// the warm-up at every map's start and keeps its clock (EarlyJoinWatch: os_warmup); the bridge follows it and ends it early (WarmUp.cs). Shared like Hud.cs:
// the plugin feeds it the game and the bridge's --selftest drives it on a handed clock. Plain BCL only.
public sealed class EarlyJoin
{
    /// <summary>The warm-up's most, from the first human's spawn on a map: the real match starts then, whoever is still coming. One value for everybody - the
    /// owner chooses 60, 90 or 120 s.</summary>
    public const int WarmUpMostSeconds = 120;

    /// <summary>Everybody coming is in: the real match starts this much later - a breath for the last one in.</summary>
    public const int SettleSeconds = 5;

    /// <summary>How long past WarmUpMostSeconds the plugin ends a warm-up by itself (mp_warmup_end), should CS2's own clock - set to end at the most - not have:
    /// a game that took no new mp_warmuptime, say. The bridge ends it 3 s past CS2's end already (WarmUp.cs); this is for a bridge that is down.</summary>
    public const int SafetySeconds = 15;

    /// <summary>CS2's own warm-up clock (mp_warmuptime) until somebody spawns: an empty map sits in its warm-up, never in a match nobody started - the bridge's
    /// empty-server reset changes the map after 10 minutes anyway.</summary>
    public const int IdleClockSeconds = 3600;

    /// <summary>The plugin's server command (EarlyJoinWatch): os_warmup says where the warm-up stands (its first line WarmUpReply's), os_warmup start begins one,
    /// os_warmup end ends it - the real match starts - and os_warmup bridge is the bridge telling it whom its log stream has signing on (BridgeViewCommand).</summary>
    public const string WarmUpCommand = "os_warmup";
    public const string StartWord = "start", EndWord = "end", BridgeWord = "bridge";
    public const string StatusWord = "status"; // in os_warmup bridge's arguments: what follows is status's joiners (no hex has an s, t or u)

    /// <summary>The most names the banner gives; the rest are "N more".</summary>
    public const int NamesShown = 3;

    /// <summary>The plugin's server command the bridge asks each second of a warm-up: the humans still joining.</summary>
    public const string JoiningCommand = "os_joining";

    /// <summary>os_joining's reply: "os_joining: 2" and one joiner's name a line - names as the plugin has them, a line each, so no name can break the list.</summary>
    public static string JoiningReply(IEnumerable<string> names)
    {
        var list = names.Select(n => Hud.Clean(n, 32)).Select(n => n == "" ? "?" : n).ToList();
        return $"{JoiningCommand}: {list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}" + string.Concat(list.Select(n => "\n" + n));
    }

    /// <summary>Who os_joining says is still joining, or null when the reply is none of its: no answer, or a plugin without the command ("Unknown command").</summary>
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

    /// <summary>Where a map's warm-up stands, as os_warmup's first line says it: on or off, the seconds since the first human's spawn on the map (null: nobody
    /// has spawned), how many humans have spawned on a team on it (one who left again not counted), and the seconds left on CS2's own warm-up clock (null: no
    /// latest start yet - nobody has spawned, or the warm-up is off).</summary>
    public sealed record WarmUpState(bool On, double? SinceFirstSpawn, int Spawned, double? Left = null);

    /// <summary>os_warmup's first line: "os_warmup: on 12 3 107.6" - on or off, the whole seconds since the first spawn (-1: none yet), the humans spawned, and
    /// the seconds left on CS2's own clock to a tenth (-1: none).</summary>
    public static string WarmUpReply(bool on, double? sinceFirstSpawn, int spawned, double? left = null) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{WarmUpCommand}: {(on ? "on" : "off")} {(sinceFirstSpawn is { } s ? Math.Max(0, (int)s) : -1)} {Math.Max(0, spawned)} {(on && left is { } l ? Math.Max(0, l).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "-1")}");

    /// <summary>os_warmup's first line read back, or null when the reply has none: no answer, or a plugin without the command ("Unknown command"). A line without
    /// the clock's field says no latest start.</summary>
    public static WarmUpState? ReadWarmUp(string? reply)
    {
        if (reply == null) return null;
        var line = reply.Replace("\r", "").Split('\n').FirstOrDefault(l => l.StartsWith(WarmUpCommand + ": ", StringComparison.Ordinal));
        var f = line?[(WarmUpCommand.Length + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f is not { Length: >= 3 } || f[0] is not ("on" or "off")) return null;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(f[1], System.Globalization.NumberStyles.AllowLeadingSign, ci, out var since) || !int.TryParse(f[2], System.Globalization.NumberStyles.None, ci, out var spawned)) return null;
        double? left = f.Length >= 4 && double.TryParse(f[3], System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint, ci, out var l) && l >= 0 ? l : null;
        return new(f[0] == "on", since >= 0 ? since : null, spawned, left);
    }

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

    /// <summary>What the bridge last told the plugin (os_warmup bridge): whom its log stream has signing on, the humans the server's own list had not yet active
    /// at its last reading (null: not read - no players table, or nobody has signed on since the bridge started), and when it said so.</summary>
    public sealed record BridgeView(IReadOnlyList<string> Log, IReadOnlyList<string>? Status, DateTime At);

    /// <summary>The bridge's word to the plugin: os_warmup bridge, the names its log stream has signing on, then - once read - the word status and status's
    /// joiners; each name the hex of its UTF-8, so no name can end or change the command (a ';', a '"', "//"). 16 a list at the most.</summary>
    public static string BridgeViewCommand(IEnumerable<string> log, IEnumerable<string>? status = null) =>
        string.Join(' ', new[] { WarmUpCommand, BridgeWord }.Concat(Hex(log)).Concat(status == null ? [] : new[] { StatusWord }.Concat(Hex(status))));

    static IEnumerable<string> Hex(IEnumerable<string> names) => names.Take(16).Select(n => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Hud.Clean(n, 32))));

    static string? Unhex(string a) { try { return System.Text.Encoding.UTF8.GetString(Convert.FromHexString(a)); } catch (FormatException) { return null; } }

    /// <summary>os_warmup bridge's arguments, after the word, read back: the log stream's names, and status's after its word (null without it); an argument that
    /// is not hex is left out.</summary>
    public static (List<string> Log, List<string>? Status) ReadBridgeView(IReadOnlyList<string> args)
    {
        var at = args.ToList().FindIndex(a => a.Equals(StatusWord, StringComparison.OrdinalIgnoreCase));
        return ([.. (at < 0 ? args : args.Take(at)).Select(Unhex).OfType<string>()], at < 0 ? null : [.. args.Skip(at + 1).Select(Unhex).OfType<string>()]);
    }

    static string Ago(DateTime at, DateTime now) => $"{Math.Max(0, (int)(now - at).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)} s ago";

    /// <summary>os_warmup's part on who is coming, each with who names them - the plugin (its controllers connecting or reconnecting, and the humans in the game
    /// still to spawn, as os_joining), status (the server's own list: not yet active) and the log stream (the bridge's: "connected", no "entered the game" yet,
    /// or on the way back through a reconnect) - and how old the bridge's word on the last two is. A joiner at CS2's first-download prompt is named by status and
    /// the log stream, never by the plugin.</summary>
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

    /// <summary>os_warmup's last part: CounterStrikeSharp's connect callbacks on this map (OnClientConnect, which comes only once a client has every addon - none
    /// for one at the prompt; the live lab's run AY saw 0 there) and the last of them.</summary>
    public static string ConnectsLine((string Name, DateTime At)? lastConnect, int connects, DateTime now) =>
        connects == 0 || lastConnect is not { } c ? "connect callbacks this map: 0" : $"connect callbacks this map: {connects} (the last: {c.Name}, {Ago(c.At, now)})";

    /// <summary>Who is still coming, as the banner says it: "Bob", "Bob and Cas", "Bob, Cas and Dee", "Bob, Cas, Dee and 2 more".</summary>
    public static string Names(IReadOnlyList<string> names)
    {
        if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
        if (names.Count > NamesShown) return $"{string.Join(", ", names.Take(NamesShown))} and {names.Count - NamesShown} more";
        return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    // ---- the warm-up's words: the countdown banner while it lasts (the bridge's PutWarmUp, HudView.CountdownOf: eyebrow, title, line, and beside them the
    //      seconds to the latest start, or to the settle's end), and the line for everybody as the match starts ----

    /// <summary>The countdown banner's eyebrow while a warm-up is on (the layout writes it in capitals: WARM-UP).</summary>
    public const string Eyebrow = "Warm-up";

    /// <summary>What a player still coming is doing, as far as the server can tell (the bridge's ComingState): loading - their game is on its way in, the map's
    /// load or a reconnect; downloading - off the server mid-way with the map to fetch (or crashed: the server cannot tell those apart), or at CS2's prompt to
    /// download the Workshop items, which the plugin cannot see; picking a team - in the game, at CS2's team menu.</summary>
    public const string Loading = "loading", Downloading = "downloading", PickingTeam = "picking a team";

    /// <summary>The banner's title: whom it waits for - one with what they are doing ("Waiting for Cas - downloading"), several by name ("Waiting for Bob, Cas
    /// and Dee", Names) - everybody in, or nobody else on the way.</summary>
    public static string Title(IReadOnlyList<(string Name, string State)> coming, bool allIn) =>
        allIn ? "Everybody is in"
        : coming.Count == 1 ? $"Waiting for {coming[0].Name}{(coming[0].State != "" ? $" - {coming[0].State}" : "")}"
        : coming.Count > 1 ? $"Waiting for {Names([.. coming.Select(c => c.Name)])}"
        : "Nobody else on the way yet";

    /// <summary>The banner's line: when the match starts, and !start. One alone waits too: a friend who connects now gets the same start.</summary>
    public static string Line(int coming, bool allIn) =>
        allIn ? "The match starts in a moment"
        : coming > 0 ? $"The match starts when everyone is in - {Hud.StartWord} to begin now"
        : $"Anyone who joins now starts with you - {Hud.StartWord} to begin now";

    /// <summary>The line for everybody in chat and on the board as the match starts: why, and who is still on the way (they join the match that runs).</summary>
    public static string StartLine(string why, IReadOnlyList<string> coming) =>
        $"The match starts now - {why}" + (coming.Count > 0 ? $" - {M.N(Names(coming))} {(coming.Count > 1 ? "join" : "joins")} it on arrival" : "");

    // ---- the warm-up's team damage: a teammate's hit hurts at least as an enemy's does while it runs, in the modes with teams ----

    /// <summary>The convars of the warm-up's team damage, and the least each holds while it runs: friendly fire on, and every share of an enemy's damage a
    /// teammate takes (ff_damage_reduction_*: the share, despite the name - 1 is an enemy's damage, Arms Race's cfg sets 100, Instakill) at least 1. Wingman's
    /// own are Valve's (0.33 for bullets); a share above 1 stays.</summary>
    public static readonly (string ConVar, double Least)[] TeamDamage =
        [("mp_friendlyfire", 1), ("ff_damage_reduction_bullets", 1), ("ff_damage_reduction_other", 1), ("ff_damage_reduction_grenade", 1)];

    /// <summary>Whether the warm-up raises team damage: in Arms Race and Wingman, whose teams are teams - not in Deathmatch, nor while everybody is an enemy
    /// (mp_teammates_are_enemies, the Fun page's free-for-all).</summary>
    public static bool TeamDamageMode(string modeId, string? teammatesAreEnemies) => modeId is Modes.ArmsRaceId or Modes.WingmanId && !Fun.Same(teammatesAreEnemies, "1");

    /// <summary>A convar's value as a number: true and false as 1 and 0; null when it is none.</summary>
    static double? Number(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => null, "true" => 1, "false" => 0,
        var v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
    };

    /// <summary>What the warm-up's begin raises: each TeamDamage convar below its least, with its value now (a bool as 1 or 0) and the value written. One that
    /// cannot be read is left alone.</summary>
    public static List<(string ConVar, string Was, string Ours)> RaiseTeamDamage(Func<string, string?> read) =>
        [.. TeamDamage.Select(t => (t.ConVar, Value: Number(read(t.ConVar)), t.Least)).Where(t => t.Value is { } v && v < t.Least - 1e-6)
            .Select(t => (t.ConVar, t.Value!.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), t.Least.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))];

    /// <summary>What the warm-up's end puts back of what its begin raised: each convar still at the warm-up's value gets its own again; one that something else
    /// changed meanwhile - a player's Fun page choice, the map - keeps that (Kept).</summary>
    public static (List<(string ConVar, string Value)> Back, List<string> Kept) PutBackTeamDamage(IEnumerable<(string ConVar, string Was, string Ours)> raised, Func<string, string?> read)
    {
        var (back, kept) = (new List<(string, string)>(), new List<string>());
        foreach (var (convar, was, ours) in raised)
            if (Fun.Same(read(convar), ours)) back.Add((convar, was)); else kept.Add(convar);
        return (back, kept);
    }

    // ---- the plan, the plugin's: who has spawned on this map, and since when somebody has ----

    readonly HashSet<long> joined = new();   // the humans who have spawned on a team on this map (one who left for real, out again)
    readonly Dictionary<long, DateTime> entered = new(); // the humans in the game who have not spawned on a team since they entered it, and since when
    DateTime? firstSpawn;                    // the first human's spawn on this map: the warm-up's clock runs from it
    string? skipped;                         // why this map is none of the plan's (the plugin loaded mid-map: it cannot tell who has spawned)

    /// <summary>A new map: nobody has spawned on it or entered it.</summary>
    public void MapStarted()
    {
        joined.Clear();
        entered.Clear();
        (firstSpawn, skipped) = (null, null);
    }

    /// <summary>Nothing on this map: it began before the plugin saw it (a reload), so nobody can tell who has spawned.</summary>
    public void Skip(string why) => skipped = why;

    /// <summary>Why this map is none of the plan's, or null.</summary>
    public string? Skipped => skipped;

    /// <summary>A human leaves for real (not the reconnect of a map change, nor the addon download's): no longer in, and when they come back, coming again.</summary>
    public void Left(long account)
    {
        joined.Remove(account);
        entered.Remove(account);
    }

    /// <summary>A human enters the game - connected in full: a new connect, or the reconnect of a map change. Still coming until their first spawn on a team,
    /// SpawnMostSeconds at the most (StillToSpawn); one who has spawned on a team on this map already is in.</summary>
    public void Entered(long account, DateTime now)
    {
        if (!joined.Contains(account)) entered[account] = now;
    }

    /// <summary>Of the humans in the game now (their accounts, as the plugin finds them connected), those who have not spawned on a team since they entered it,
    /// for less than SpawnMostSeconds: CS2's team menu after every load, or a game still finishing its load once the server has it in. The warm-up waits for
    /// them as for a game still loading (seen live on 2026-11-18: a wait that ended as a joiner entered the game saw their spawn 16 s later). One whose
    /// entering the plan never heard of counts from its first sight here. None on a map the plugin loaded during.</summary>
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

    /// <summary>A human spawns on a team. True on their first spawn on this map since they connected or it loaded - a respawn, a restart's spawn and a team
    /// switch are none. The map's first starts the warm-up's clock.</summary>
    public bool Spawned(long account, DateTime now)
    {
        if (skipped != null || !joined.Add(account)) return false;
        entered.Remove(account); // in: no more still to spawn
        firstSpawn ??= now;
        return true;
    }

    /// <summary>The first human's spawn on this map, or null: nobody yet, or a map the plugin loaded during.</summary>
    public DateTime? FirstSpawn => skipped == null ? firstSpawn : null;

    /// <summary>The humans who have spawned on a team on this map and are still on it.</summary>
    public int SpawnedCount => joined.Count;

    /// <summary>For the console: where this map stands.</summary>
    public string Describe(DateTime now) =>
        skipped != null ? $"nothing on this map: {skipped}"
        : firstSpawn is not { } at ? $"nobody has spawned on this map yet; {entered.Count} in the game still to spawn"
        : $"the first spawn was {(int)(now - at).TotalSeconds} s ago; {joined.Count} human(s) spawned on this map";
}
