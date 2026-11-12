// The game server's life as the log and the restart watcher tell it: a session, a start, a quit, a map start, a join, a match end; the start
// roll, and the maintenance flag.
using System.Text.RegularExpressions;

static partial class Program
{
    /// <summary>A server token the bridge has not seen: logged, nothing more. CS2 does not renew the token per process (one value,
    /// 5e1f0c2ab7d94a36, for three weeks through every deploy and restart, then one change, cause unknown), so a restart is caught by
    /// ServerGoingDown (the restart watcher's notice, the bridge's own !restart, the quit's echo) and OnMapStart (the boot's start map), and a
    /// crash by the spawn counter (CrashWatch.cs).</summary>
    static void Session(string token)
    {
        if (token == S.ServerToken) return;
        Log($"server token {token}{(S.ServerToken == null ? " (the first this bridge has seen)" : $" replaces {S.ServerToken}")}");
        S.ServerToken = token;
        Save();
    }

    /// <summary>The server is going down or has come up anew: its start map is still to come, and the start roll is owed once it has loaded.</summary>
    static void StartAwaited()
    {
        StartRollOwed = true; StartRollNeedsProof = false; StartMapAt = default;
    }

    /// <summary>The process is going down right now, on purpose: the restart watcher's notice right before its planned quit (CrashWatch.cs),
    /// the bridge's own !restart as it sends the quit, or the echo of an RCON quit or _restart in the log stream (an admin; and the backup
    /// of the other two, since the echo dies with the process when the engine segfaults on quit). Whatever was counting down or loading dies
    /// with it - left standing, a countdown would fire into the dead server and the boot's start map would be taken for the map it had asked
    /// for, and the restart would end on the start map. Once per quit: an echo or a notice that follows one within GoingDownSeconds changes
    /// nothing.</summary>
    static void ServerGoingDown(string source)
    {
        var now = Now();
        if (S.CleanShutdown && GoingDownAt != default && (now - GoingDownAt).TotalSeconds < GoingDownSeconds)
        {
            Log($"restart: {source} too - the quit was announced {(now - GoingDownAt).TotalSeconds:0} s ago, nothing more to do");
            return;
        }
        GoingDownAt = now;
        BeforeQuit = new(S.Map, S.PickedBy, S.LogName, S.Mode, ModeBeforeChange, RestartDue, StartRollOwed, StartRollNeedsProof, StartMapAt);
        if ((Pending?.Map ?? (LoadPending ? S.Map : null)) is { } gone) Log($"restart: the change to {Label(gone)} dies with the old process");
        Log($"restart: the server is quitting ({source}) - a clean quit; the start roll is owed once its start map has loaded");
        S.CleanShutdown = true;
        Pending = null; LoadPending = false;
        S.Map = null; S.PickedBy = null; S.LogName = null; // the restarted server will be on its start map
        EndBallot("the server is restarting"); ModeAtServerStart(); // VoteRound.cs, ModeSwitch.cs: it boots in Arms Race
        RestartGoing(); // Restarts.cs: a restart announced is happening
        MatchEndUntil = default; EndScreenAt = null;
        StartAwaited();
        Save();
    }

    /// <summary>The bridge has just started. A server that is on its start map without a player having picked it is a start roll
    /// that never happened (the bridge was down or redeployed alone while the server booted), so it is owed - but only on proof:
    /// TryStartRoll rolls it once RCON status says the start map is loaded, and lets any other map be.</summary>
    static void BridgeStart()
    {
        if (S.PickedBy != null && S.Map is { } m && IsStartMap(m)) return; // a player's own pick of the start map stays
        StartRollOwed = true; StartRollNeedsProof = true; StartMapAt = Now();
    }

    static bool IsStartMap(MapInfo m) => m.Official && m.Name.Equals(StartMap, StringComparison.OrdinalIgnoreCase);

    /// <summary>A "connected" or "entered the game" line; fresh is true for a real join (Arrive), false for the reconnect a map change causes.
    /// On a real join the plugin is asked over RCON to put the guide on screen - "first" for a player never seen before,
    /// who then gets the HUD's window on its start page. Nothing goes to chat. Within CrashNoticeMinutes of a crash, a joiner's note says so.</summary>
    static void OnJoin(LogEvent e, bool fresh)
    {
        if (e.Kind != Kind.Entered) return; // "entered the game" is the first moment the player can see the screen
        if (CrashNotice != "" && Now() < CrashNoticeUntil && !InMaintenance()) Post(Messages.CrashNote, CrashNotice, e.SteamId);
        TellUpdated(e.SteamId); // Restarts.cs: an update just done
        var first = S.Seen.Add(e.SteamId);
        if (first) Save();
        if (!fresh && !first) return;
        HudSeen.Remove(e.SteamId); // a real join: the HUD check asks again (it may be a new HUD)
        ShowGuide(e.SteamId, first);
    }

    /// <summary>Asks the plugin to put the guide up for one player.</summary>
    static void ShowGuide(string steamId, bool first)
    {
        var reply = Send($"{Guide.Command} {AccountId(steamId)}{(first ? " first" : "")}");
        Log($"guide for {steamId}{(first ? " (first time)" : "")} -> {reply ?? "(rcon failed)"}");
    }

    /// <summary>A map start. One load logs two lines in one burst - "Loading map" and CS2's nameless "Started:" (named only when the
    /// push carries X-Game-Map) - and they are one map start: the second is ignored, so it can neither say the banner twice nor take a
    /// player's own pick of the start map for a boot. A lone nameless one is a booting server whose push began after "Loading map":
    /// the start map. A start of START_MAP that the bridge did not ask for means the server (re)started: the start roll is owed
    /// (TryStartRoll). And a start of START_MAP while the bridge was waiting for another map is never that map: the server restarted
    /// under the change (a !restart of a stuck load, a crash or a freeze kill during it, the 07:00 quit inside a match-end countdown),
    /// and the change died with the old process.</summary>
    static void OnMapStart(string name)
    {
        var now = Now();
        if ((now - LastNamedMapStart).TotalSeconds < 10 && (name == "" || name.Equals(S.LogName, StringComparison.OrdinalIgnoreCase))) return; // the second line of the same load
        var logName = name != "" ? name : StartMap;
        WatchSpawn(logName, S.Map != null ? Key(S.Map) : S.LogName, S.CleanShutdown, InMaintenance()); // with the map that was on or loading, before this start replaces it (CrashWatch.cs)
        LastNamedMapStart = now;
        EndBallot("a map started during the vote"); // VoteRound.cs: an open vote is over; a decided one has landed
        var start = logName.Equals(StartMap, StringComparison.OrdinalIgnoreCase);
        var underChange = LoadPending || Pending != null;
        var ours = LoadPending && now < LoadUntil && S.Map != null && !(start && !IsStartMap(S.Map));
        var restarted = !ours && start && underChange;
        if (restarted)
        {
            var gone = Pending?.Map ?? S.Map;
            Log($"restart: the server came up on {StartMap} instead of {(gone != null ? Label(gone) : "?")} - that change died with the old process");
            Pending = null; LoadPending = false;
            S.LogName = null; // attribution starts over below
        }
        AllReloading(now); MatchEndUntil = default; EndScreenAt = null; RematchDue = null; FairNames.Clear(); FairSentAt = DateTime.MinValue; // fresh map: everyone reconnects into it, and it has a match of its own
        WhyOpen.Clear(); // Likes.cs: nothing on screen survives a map change; a Dislike nobody gave a reason for stays "I did not like the map"
        if (!ours && start) ModeAtServerStart();  // ModeSwitch.cs: a start of the start map nobody asked for is a server start, in Arms Race...
        if (!ours) ModeProbing = true; // ...and any map start nobody asked for has the plugin say which mode it was told (else the convars)
        else { ModeBeforeChange = null; ModeVerifyDue = true; } // the change landed: SyncMode checks the mode it came up in
        After(2000, MatchLengthAfterStart); // the mode's cfg just set mp_roundtime to its own: the chosen length back, by the mode known 2 s from now (MatchTime.cs)
        if (ours && S.Map != null) // the change we asked for has loaded (ours implies S.Map; repeated for the null analysis)
        {
            LoadPending = false;
            S.LogName = logName;
            Remember(S.Map); // the HUD's now-playing card says what is on, at everybody's spawn
        }
        else if (logName != S.LogName) // the server rotated by itself, or a new process loaded its start map: attribution no longer applies
        {
            S.Map = Officials.FirstOrDefault(o => o.Name.Equals(logName, StringComparison.OrdinalIgnoreCase)); // an official map is known by the name it logs
            S.PickedBy = null; S.LogName = logName;
            if (S.Map != null) Remember(S.Map);
        }
        if (!ours && (StartRollOwed || start))
        {
            StartRollOwed = true; StartRollNeedsProof = false; StartMapAt = now;
        }
        S.CleanShutdown = false;
        Save();
        if (S.Map != null || S.LogName != null) Post(Messages.NowPlaying, NowPlayingLine(), key: "playing"); // the board's copy of the card everybody gets at their spawn
        After(1500, SyncMode); // ModeSwitch.cs: the plugin hears the mode again after the bridge's own change, in case it reloaded; is asked after any other
    }

    /// <summary>The now-playing card's words: what is on, how big, who picked it.</summary>
    static string NowPlayingLine() => string.Join("  ·  ", new[] { Clip(CurrentLabel(), 40), S.Map is { Official: false, Rank: > 0 } m ? Size(m) : "", Hud.ByWords(S.PickedBy ?? "") }.Where(x => x != ""));

    const int StartRollDelayMs = 5000, HistoryLength = 20;

    /// <summary>Played maps, oldest first, for !previous and for the dice's recent-map rule. A map played again moves to the end. A map played is no
    /// longer new to us (S.Met): a stock entry played is used up.</summary>
    static void Remember(MapInfo map)
    {
        S.History.RemoveAll(m => Key(m) == Key(map));
        S.History.Add(map);
        if (S.Met.Add(Key(map))) CatalogVersion++; // its cards' tags change
        if (S.History.Count > HistoryLength) S.History.RemoveRange(0, S.History.Count - HistoryLength);
    }

    /// <summary>Every tick: once per server start, 5 s after the start map has loaded, the dice - the start roll, one of the
    /// automatic changes. With players on and the HUD drawn they choose instead: a vote opens for everybody, and nobody voting rolls the dice. Waits out the maintenance flag, a change under way and a vote that is open (whose result settles it: CloseBallot); probes
    /// RCON with a status first (every 10 s until it answers), and the debt stays open until a change command has actually gone out (FireChange).
    /// The status also says which map is loaded: on any other map than START_MAP there is nothing to roll, and a debt from BridgeStart rolls only
    /// when it says START_MAP.</summary>
    static void TryStartRoll(DateTime now)
    {
        if (!StartRollOwed || StartMapAt == default || now < StartMapAt.AddMilliseconds(StartRollDelayMs) || now < StartRollRetryAt) return;
        if (InMaintenance() || Pending != null || LoadPending || Ballot != null) return;
        StartRollRetryAt = now.AddSeconds(10);
        if (Send("status") is not { } status) { Log("start: RCON not answering yet - start roll again in 10 s"); return; }
        var on = StatusMap(status);
        if (on != null && !on.Equals(StartMap, StringComparison.OrdinalIgnoreCase)) { StartRollOwed = false; Log($"start: the server is on {on}, not {StartMap} - nothing to roll"); return; }
        if (on == null && StartRollNeedsProof) { StartRollOwed = false; Log("start: RCON status does not name the loaded map - a bridge start rolls only on proof"); return; }
        // Players on: a vote for everybody instead of the dice - everybody's window opens on the ballot at once, and nobody voting rolls the dice. An empty
        // server, or one whose HUD nobody sees: the dice, as ever.
        if (Humans() is > 0 and var humans && HudActive())
        {
            if (OpenBallot(now, humans, serverStart: true)) { StartRollOwed = false; Log($"start: {humans} player(s) on - a vote instead of the dice"); return; }
            Log("start: the vote could not open - the dice instead");
        }
        if (!AutoRoll("Server started")) { StartRollOwed = false; Log("start: no map is eligible to roll"); }
    }

    /// <summary>A match ended: the vote, where the plugin draws it (it polls /hud) and somebody is on; the dice where nothing draws a vote, the
    /// vanilla server's case. A vote that is open runs on as a match end's - Keep is "Play this map again", nobody voting is Random. Nothing happens
    /// while a change counts down or the next map is still loading - that match was the old map's - nor while the maintenance flag is set. A match
    /// end's vote ignores the wait after the last one.</summary>
    static void OnMatchEnd()
    {
        var now = Now();
        if (now < MatchEndUntil) return;
        MatchEndUntil = now.AddSeconds(30);
        EndScreenAt = now; // Restarts.cs: no restart of the match while its screen is up
        MatchLengthAtMatchEnd(); // MatchTime.cs: Wingman's next match plays the chosen rounds - a kept map's starts without a load
        if (RestartAtMatchEnd(now)) return; // Restarts.cs: a restart was waiting for this match's end - no vote, the server goes down
        if (Pending != null) { Log($"match end: a change to {Label(Pending.Map)} is already counting down"); return; }
        if (LoadPending) { Log("match end: the next map is still loading - that was the old map's match"); return; }
        if (Ballot is { Open: true } running)
        {
            if (!running.MatchEnded) { running.MatchEnded = true; Log("match end: the vote that is open decides as a match end's from now (Play this map again; nobody voting is Random)"); }
            return;
        }
        if (InMaintenance()) { Log("match end: maintenance flag set - no automatic change"); return; }
        var humans = Humans();
        if (humans <= 0) { Log("match end: nobody on - nothing to do"); return; }
        if (HudActive() && OpenBallot(now, humans, matchEnd: true)) return; // VoteRound.cs
        if (!AutoRoll("Match over")) Log("match end: no map is eligible to roll"); // no plugin draws a vote (or nothing to put on one): the dice
    }

    [GeneratedRegex(@"^loaded spawngroup\(\s*1\)\s*:\s*SV:\s*\[1:\s*([^|\]\s]+)\s*\|", RegexOptions.Multiline)] private static partial Regex StatusMapRx();

    /// <summary>The map RCON status reports as loaded: its first spawngroup, "loaded spawngroup(  1)  : SV:  [1: ar_shoots | main lump | mapload]"
    /// (CS2 1.41.8.8, 2026-10-05). Null when the reply has no such line.</summary>
    static string? StatusMap(string status) => StatusMapRx().Match(status) is { Success: true } m ? m.Groups[1].Value : null;

    static bool StartRollOwed;                             // a (re)started server: roll once, after its start map (TryStartRoll)
    static bool StartRollNeedsProof;                       // owed by BridgeStart: roll only if RCON status says the start map is loaded
    static DateTime StartMapAt, StartRollRetryAt;          // when the start map logged its start; when to probe RCON again
    static DateTime LastNamedMapStart;                     // a nameless map start within 10 s of a named one belongs to it
    static bool MaintenanceOn;

    // ---- the maintenance flag: a file the map-test sweep creates and deletes ----

    static string MaintenanceFile => Path.Combine(StateDir, "maintenance");

    /// <summary>True while STATE_DIR/maintenance exists: no automatic map change of any kind - the start roll, a match end's vote, the way back to Arms Race -
    /// and a vote nobody called or nobody voted in changes nothing; a vote a player called still decides (CloseBallot). Checked at every decision and every
    /// tick; each flip is logged, never said in chat.</summary>
    static bool InMaintenance()
    {
        var on = File.Exists(MaintenanceFile);
        if (on != MaintenanceOn)
        {
            MaintenanceOn = on;
            Log(on ? $"maintenance: {MaintenanceFile} exists - no automatic map changes until it is gone (the start roll, a match end's vote, the way back to Arms Race, the dice when nobody voted); a vote a player called still decides"
                : $"maintenance: {MaintenanceFile} is gone - automatic map changes resume");
        }
        return on;
    }
}
