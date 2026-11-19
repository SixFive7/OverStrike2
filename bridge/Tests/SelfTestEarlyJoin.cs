// --selftest, the warm-up's plan (Shared/EarlyJoin.cs) on a handed clock: who has spawned on a map and when the first did, who is still to spawn, which departure
// is a reconnect, os_warmup's answer, the words. The bridge's half - following the warm-up, who is coming, its end, !start - is MatchSelfTest's
// (SelfTestMatch.cs); that CS2's warm-up begins, respawns everybody, keeps our clock and ends into the match as the plugin expects is the live lab's.
static partial class Program
{
    static void EarlyJoinSelfTest(Action<string, bool> Check)
    {
        var t0 = new DateTime(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);
        const long Ann = 1, Ben = 2, Cas = 3;
        DateTime At(double s) => t0.AddSeconds(s);

        var plan = new EarlyJoin();
        plan.MapStarted();
        Check("Warm-up: nobody has spawned on a new map - no first spawn, so no clock runs yet", plan.FirstSpawn == null && plan.SpawnedCount == 0);
        Check("...the first human's spawn starts the warm-up's clock, and each human's first spawn on the map counts once - a respawn, the match's start after the "
            + "warm-up and a team switch are none", plan.Spawned(Ann, At(15)) && plan.FirstSpawn == At(15) && plan.Spawned(Ben, At(40)) && !plan.Spawned(Ann, At(41))
            && !plan.Spawned(Ben, At(160)) && plan.FirstSpawn == At(15) && plan.SpawnedCount == 2);
        plan.Left(Ben);
        Check("...somebody who leaves for real is out, and coming again: their next spawn counts anew; the clock stays the first spawn's",
            plan.SpawnedCount == 1 && plan.Spawned(Ben, At(70)) && plan.FirstSpawn == At(15));
        plan.MapStarted();
        Check("...a new map starts everything over", plan.FirstSpawn == null && plan.SpawnedCount == 0 && plan.Spawned(Cas, At(5)) && plan.FirstSpawn == At(5));
        plan.Skip("the plugin loaded during this map");
        Check("...a map the plugin loaded during is none of the plan's: no spawn counts, and it has no first spawn to say",
            !plan.Spawned(Ann, At(6)) && plan.FirstSpawn == null && plan.Describe(At(6)).Contains("the plugin loaded during this map"));
        plan.MapStarted();
        Check("...and the next map is the plan's again", plan.Spawned(Ann, At(1)) && plan.FirstSpawn == At(1));

        // ---- who is still to spawn (StillToSpawn: the plugin's half of os_joining), 2026-11-18's Shortdust replayed with made-up names: after a map change, four in
        // the game within 5 s and Max at +13 s; the first team pick at +8.4 s, three more at +9, +10 and +15 s, and Max's at +29 s - 16 s after entering, which
        // release 218's wait did not hold for ----
        const long Ana = 11, Bix = 12, Cyd = 13, Dov = 14, Max = 15;
        var sd = new EarlyJoin();
        sd.MapStarted();
        foreach (var (who, s) in new[] { (Bix, 3.0), (Ana, 4.0), (Cyd, 4.0), (Dov, 5.0) }) sd.Entered(who, At(s));
        var inGame = new List<long> { Bix, Ana, Cyd, Dov };
        sd.Spawned(Ana, At(8.4));
        sd.Spawned(Cyd, At(9));
        var atNine = sd.StillToSpawn(inGame, At(9));
        sd.Spawned(Bix, At(10));
        sd.Entered(Max, At(13)); inGame.Add(Max);
        var atMaxIn = sd.StillToSpawn(inGame, At(13));
        sd.Spawned(Dov, At(15));
        var at28 = sd.StillToSpawn(inGame, At(28));
        sd.Spawned(Max, At(29));
        Check("Still to spawn (2026-11-18's Shortdust, replayed): the two in the game but not yet on a team are still coming - os_joining names them",
            atNine is [Bix, Dov] && sd.FirstSpawn == At(8.4));
        Check("...and Max, in the game at +13 s, is still coming until his spawn at +29 s: the warm-up waits for him through CS2's team menu",
            atMaxIn is [Dov, Max] && at28 is [Max] && sd.StillToSpawn(inGame, At(29)) is [] && sd.SpawnedCount == 5);
        var afk = new EarlyJoin();
        afk.MapStarted();
        afk.Entered(21, At(0));
        Check($"...one who never spawns - a spectator, somebody away at CS2's team menu - is waited for {EarlyJoin.SpawnMostSeconds} s from entering at the most",
            afk.StillToSpawn([21], At(EarlyJoin.SpawnMostSeconds - 1)) is [21] && afk.StillToSpawn([21], At(EarlyJoin.SpawnMostSeconds)) is []);
        Check("...one whose entering the plan never heard of counts from its first sight; one who spawned on this map already (a team switch, a side picked again) is in",
            afk.StillToSpawn([22], At(100)) is [22] && afk.StillToSpawn([22], At(100 + EarlyJoin.SpawnMostSeconds)) is []
            && afk.Spawned(23, At(1)) && afk.StillToSpawn([23], At(2)) is []);
        afk.Entered(23, At(3));
        var redone = afk.StillToSpawn([23], At(4)) is [];
        afk.MapStarted();
        afk.Entered(23, At(5));
        var newMap = afk.StillToSpawn([23], At(6)) is [23];
        afk.Skip("the plugin loaded during this map");
        Check("...entering again on the same map changes nothing for one who is in; a new map starts everybody over; a map the plugin loaded during names nobody",
            redone && newMap && afk.StillToSpawn([23], At(7)) is []);
        Check("Warm-up: a reconnect is no leaving by one rule for the bridge's log stream and the plugin - a reason with SHUTDOWN in it, a map change's or the HUD "
            + "addon's; NETWORK_DISCONNECT_LOOPDEACTIVATE is a leaving (the two seen live, 2026-11-12 and 16: one came back 11 minutes later), and so is every other",
            EarlyJoin.Reconnects("NETWORK_DISCONNECT_SHUTDOWN") && EarlyJoin.Reconnects("NETWORK_DISCONNECT_LOOPSHUTDOWN")
            && !EarlyJoin.Reconnects("NETWORK_DISCONNECT_LOOPDEACTIVATE") && !EarlyJoin.Reconnects("NETWORK_DISCONNECT_DISCONNECT_BY_USER")
            && !EarlyJoin.Reconnects("NETWORK_DISCONNECT_EXITING") && !EarlyJoin.Reconnects("NETWORK_DISCONNECT_TIMEDOUT") && !EarlyJoin.Reconnects(""));

        // ---- os_warmup's answer, the bridge's each second ----
        Check("os_warmup's first line reads back: on or off, the whole seconds since the first spawn (none: -1), the humans spawned, the seconds left on CS2's "
            + "own clock to a tenth (none: -1, and none while off or without the field); a later line and a reply with no such line (no answer, a plugin without the "
            + "command) are none of it",
            EarlyJoin.WarmUpReply(true, 12.7, 3, 107.64) == "os_warmup: on 12 3 107.6"
            && EarlyJoin.ReadWarmUp("os_warmup: on 12 3 107.6\nos_warmup: on (ours) ...") == new EarlyJoin.WarmUpState(true, 12, 3, 107.6)
            && EarlyJoin.WarmUpReply(true, null, 0) == "os_warmup: on -1 0 -1" && EarlyJoin.ReadWarmUp("os_warmup: on -1 0 -1") == new EarlyJoin.WarmUpState(true, null, 0)
            && EarlyJoin.WarmUpReply(false, 30, 2, 5) == "os_warmup: off 30 2 -1" && EarlyJoin.ReadWarmUp("os_warmup: on 12 3") == new EarlyJoin.WarmUpState(true, 12, 3)
            && EarlyJoin.ReadWarmUp(EarlyJoin.WarmUpReply(false, null, 0)) == new EarlyJoin.WarmUpState(false, null, 0)
            && EarlyJoin.ReadWarmUp(null) == null && EarlyJoin.ReadWarmUp("Unknown command 'os_warmup'!") == null && EarlyJoin.ReadWarmUp("") == null
            && EarlyJoin.ReadWarmUp("os_warmup: maybe 1 1") == null && EarlyJoin.ReadWarmUp("os_warmup: on x 1") == null);

        // ---- the words ----
        Check("Warm-up: its words - one name, two, three, more; the countdown banner's title: one still coming with what they are doing, several by name, everybody "
            + "in, nobody else on the way",
            EarlyJoin.Names(["Bob"]) == "Bob" && EarlyJoin.Names(["Bob", "Cas"]) == "Bob and Cas" && EarlyJoin.Names(["Bob", "Cas", "Dee"]) == "Bob, Cas and Dee"
            && EarlyJoin.Names(["Bob", "Cas", "Dee", "Eve", "Fay"]) == "Bob, Cas, Dee and 2 more"
            && EarlyJoin.Title([("Cas", EarlyJoin.Downloading)], false) == "Waiting for Cas - downloading" && EarlyJoin.Title([("Cas", EarlyJoin.PickingTeam)], false) == "Waiting for Cas - picking a team"
            && EarlyJoin.Title([("Bob", EarlyJoin.Loading), ("Cas", EarlyJoin.Downloading)], false) == "Waiting for Bob and Cas" && EarlyJoin.Title([], true) == "Everybody is in"
            && EarlyJoin.Title([], false) == "Nobody else on the way yet" && EarlyJoin.Eyebrow == "Warm-up");
        Check("...its line: when the match starts and !start - one alone hears that a friend who joins now starts with them; everybody in, a moment; and the line for "
            + "everybody as the match starts, naming who joins it on arrival",
            EarlyJoin.Line(2, false) == "The match starts when everyone is in - !start to begin now" && EarlyJoin.Line(0, false) == "Anyone who joins now starts with you - !start to begin now"
            && EarlyJoin.Line(0, true) == "The match starts in a moment"
            && M.Plain(EarlyJoin.StartLine("Everybody is in", [])) == "The match starts now - Everybody is in"
            && M.Plain(EarlyJoin.StartLine("120 s are up", ["Bob"])) == "The match starts now - 120 s are up - Bob joins it on arrival"
            && M.Plain(EarlyJoin.StartLine("Ann typed !start", ["Bob", "Cas"])) == "The match starts now - Ann typed !start - Bob and Cas join it on arrival");
        var warm = new HudState { WarmUpTitle = "Waiting for Bob - loading", WarmUpLine = EarlyJoin.Line(1, false), WarmUpAtMs = HudState.Ms(At(100)) };
        var warmBack = HudState.Decode(warm.Encode(endTimes: true))!;
        var unclocked = new HudState { WarmUpTitle = "Waiting for Bob - picking a team", WarmUpLine = EarlyJoin.Line(1, false) }.AsOf(HudState.Ms(At(0)));
        var settling = new HudState { WarmUpTitle = "Everybody is in", WarmUpLine = EarlyJoin.Line(0, true), WarmUpAtMs = HudState.Ms(At(100)), WarmUpGoMs = HudState.Ms(At(5)) }.AsOf(HudState.Ms(At(1)));
        Check("...on the countdown banner: WARM-UP over the title and the line, the logo in the picture's place, the seconds to the latest start counted down by the "
            + "plugin from its end time (100, then 40, Now); no seconds before anybody has spawned; the settle's seconds once everybody is in; a map change has the banner "
            + "first; the state carries it end to end, and without it there is none",
            HudView.CountdownOf(warmBack.AsOf(HudState.Ms(At(0)))) is { Eyebrow: "Warm-up", Title: "Waiting for Bob - loading", Logo: true, Seconds: 100, Warm: true }
            && HudView.CountdownOf(warmBack.AsOf(HudState.Ms(At(60))))?.Seconds == 40 && HudView.Texts(warmBack.AsOf(HudState.Ms(At(100)))).Single(t => t.Id == "os_k_secs").Text == "Now"
            && HudView.Texts(warmBack.AsOf(HudState.Ms(At(0)))).Single(t => t.Id == "os_k_by").Text == "The match starts when everyone is in - !start to begin now"
            && HudView.CountdownOf(unclocked) is { Seconds: -1, Warm: true } && HudView.Texts(unclocked).Single(t => t.Id == "os_k_secs").Text == ""
            && HudView.Classes(unclocked).Single(c => c.Class == "changing").On && HudView.Classes(unclocked).Single(c => c.Class == "cd-logo").On
            && HudView.CountdownOf(settling) is { Title: "Everybody is in", Seconds: 4 }
            && HudView.CountdownOf(new HudState { WarmUpTitle = "Waiting for Bob", ChangeIn = 5, ChangeMap = "de_dust2" }) is { Eyebrow: "Map change", Warm: false }
            && HudView.CountdownOf(new HudState().AsOf(HudState.Ms(At(0)))) == null && !HudState.Decode(new HudState().Encode(endTimes: true))!.WarmUpOn);

        // ---- the bridge's word to the plugin, and the console's lines ----
        // status as CS2 writes it (a real reply's lines, test names; the connecting row is the live lab's run AY's at the prompt): a joiner there is "connecting" until CS2 drops them
        const string status = "Server:  Running [0.0.0.0:27015]\r\nClient:  Disconnected\r\n----- Status -----\r\n@ Current  :  game\r\nsource   : console\r\n"
            + "hostname : Over Strike 2\r\nspawn    : 7\r\nos/type  : Linux dedicated\r\nplayers  : 2 humans, 1 bots (16 max) (not hibernating) (unreserved)\r\n"
            + "---------spawngroups----\r\nloaded spawngroup(  1)  : SV:  [1: aim_homage | main lump | mapload]\r\n---------players--------\r\n"
            + "  id     time ping loss      state   rate adr name\r\n65535 [NoChan]    0    0 challenging      0unknown ''\r\n"
            + "65280    04:10   20    0     active 786432 192.0.2.1:27005 'Bob'\r\n    1    BOT    0    0     active      0 'Ted'\r\n"
            + "    0    00:01    0    0 connecting 262144 192.0.2.9:55493 'Cas O'Hara'\r\n#end\r\n";
        Check("Warm-up: the server's own client list - status's players table, as CS2 writes it - reads back: the humans and their state, a joiner at the prompt "
            + "connecting; no bot, no handshake placeholder; a name with a quote whole; no table (a short status, no answer) is none, an empty one nobody",
            EarlyJoin.ReadStatus(status) is [{ Name: "Bob", State: "active", Joining: false }, { Name: "Cas O'Hara", State: "connecting", Joining: true }]
            && EarlyJoin.ReadStatus("hostname : test\nplayers  : 2 humans, 0 bots (0 max)\n#end") == null && EarlyJoin.ReadStatus(null) == null
            && EarlyJoin.ReadStatus("---------players--------\n  id     time ping loss      state   rate adr name\n65535 [NoChan]    0    0 challenging      0unknown ''\n#end") is []);
        var told = EarlyJoin.BridgeViewCommand(["Cas", "x\"; quit; //"], ["Cas", "Dee"]);
        var (toldLog, toldStatus) = EarlyJoin.ReadBridgeView([.. told.Split(' ').Skip(2)]);
        var (bareLog, bareStatus) = EarlyJoin.ReadBridgeView([]);
        Check("Warm-up: the bridge's word to os_warmup names its log stream's sign-ons, then status's joiners after their word, as hex - a ';', a '\"' or \"//\" in a name "
            + "cannot end or change the command - and the plugin reads both back; nobody is the bare word, status unread no status part; an argument not hex is left out",
            told.StartsWith("os_warmup bridge ", StringComparison.Ordinal) && !told.Contains(';') && !told.Contains('"') && !told.Contains("//")
            && toldLog is ["Cas", "x\"; quit; //"] && toldStatus is ["Cas", "Dee"] && EarlyJoin.BridgeViewCommand([]) == "os_warmup bridge"
            && EarlyJoin.BridgeViewCommand([], []) == "os_warmup bridge status" && bareLog is [] && bareStatus == null
            && EarlyJoin.ReadBridgeView(["zz", "436173"]).Log is ["Cas"] && EarlyJoin.ReadBridgeView(["status"]) is ([], []));
        Check("Warm-up: os_warmup names whom the warm-up waits for, each with who names them - the plugin, status, the bridge's log stream - and how old the "
            + "bridge's word is; the lab's run AY's joiner at the prompt is status's and the log stream's; nobody is said so; the connect callbacks last",
            EarlyJoin.JoiningLine([], new(["Cas"], ["Cas"], At(0)), At(2)) == "still joining: Cas (status, log stream) - by the plugin, status and the bridge's log stream, told 2 s ago"
            && EarlyJoin.JoiningLine(["Dee"], new(["Cas"], null, At(0)), At(1)) == "still joining: Dee (the plugin), Cas (log stream) - by the plugin and the bridge's log stream (status not read yet), told 1 s ago"
            && EarlyJoin.JoiningLine([], new([], [], At(0)), At(5)) == "still joining: nobody - by the plugin, status and the bridge's log stream, told 5 s ago"
            && EarlyJoin.JoiningLine([], null, At(0)) == "still joining: nobody - by the plugin; the bridge has told it nothing yet"
            && EarlyJoin.ConnectsLine(null, 0, At(0)) == "connect callbacks this map: 0" && EarlyJoin.ConnectsLine(("Bob", At(1)), 2, At(13)) == "connect callbacks this map: 2 (the last: Bob, 12 s ago)");
        Check("Warm-up: !start is a chat command and a click's word for the bridge; the fairness restart's ask is gone - no word of the plugin restarts a match any "
            + "more; !ready is no word of ours (in a pick-up game it says one player is ready, not that the match starts for everybody)",
            Hud.IsAction(Hud.StartWord) && ChatCommands.Parse(Hud.StartWord) is { Kind: ChatCommands.Kind.Bridge, Arg: Hud.StartWord } && !Hud.IsAction("!latejoin")
            && ChatCommands.Parse("!begin") is { Kind: ChatCommands.Kind.Bridge, Arg: Hud.StartWord } && ChatCommands.Parse("!ready").Kind == ChatCommands.Kind.Unknown);
    }
}
