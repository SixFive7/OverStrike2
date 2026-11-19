// The warm-up (Shared/EarlyJoin.cs): every map starts in CS2's own warm-up. The plugin begins it a second into the map and keeps its clock (EarlyJoinWatch):
// from the first human's spawn it runs WarmUpMostSeconds, so the game's own timer counts down to the latest start, and the plugin ends it then. The bridge,
// which knows who is still coming, asks the plugin to end it sooner (os_warmup end): once everybody coming has spawned and at least two humans have,
// SettleSeconds later, or at a player's !start - and says so once a look finds it ended (WarmUpEnding): CS2 ends it within seconds of the plugin's request.
// That end is the one restart a map makes; nothing restarts the match after it. While it lasts the countdown banner says whom it waits for and counts down
// to the latest start (PutWarmUp, HudView.CountdownOf).
static partial class Program
{
    /// <summary>The warm-up on this map as the bridge follows it: on since (null: none seen, or over), the first human's spawn by the plugin's word, CS2's own
    /// warm-up end by the plugin's word (the latest start), everybody in since (the settle runs), whom it waited for at the last look and what each is doing, how
    /// many humans have spawned, whether the last look found it on, and how many looks are left for one to begin - or to begin again before anybody has spawned.</summary>
    static DateTime? WarmUpSince;
    static DateTime? WarmUpFirstSpawn;
    static DateTime? WarmUpEnd;
    static DateTime? WarmUpAllInSince;
    static List<(string Name, string State)> WarmUpComing = [];
    static int WarmUpSpawned;
    static bool WarmUpOnNow;
    static int WarmUpLooks;
    static DateTime? WarmUpOffSince;
    static bool WarmUpTickDue;

    /// <summary>The bridge asked the plugin to end the warm-up (EndWarmUp): when it last asked, why in the log's words and in the line's, who was still coming, and
    /// how often it has asked. The line for everybody goes out once a look finds CS2's warm-up off.</summary>
    static (DateTime At, string Why, string Says, List<string> Coming, int Asks)? WarmUpEnding;

    /// <summary>An end the bridge asked for and not come this long after its last ask is asked for again, three times at the most: CS2 ends it about 2 s after the
    /// plugin's request, and the plugin presses harder on its own after 5 (EarlyJoin.EndStepSeconds).</summary>
    const int WarmUpEndWaitSeconds = 10;

    /// <summary>How long into a map the bridge looks for the plugin's warm-up to begin (a second in, by the plugin's clock), a look a second; and how long one that
    /// went off before anybody spawned may take to begin again (the plugin begins it again within seconds: EarlyJoinWatch).</summary>
    const int WarmUpFindSeconds = 30;

    /// <summary>A warm-up on with humans on the server and no first spawn by the plugin's word this long - a plugin loaded during it cannot say who spawned -
    /// ends anyway, the most and a minute after it began.</summary>
    const int WarmUpBlindSeconds = EarlyJoin.WarmUpMostSeconds + 60;

    /// <summary>The latest start passed this long ago, and the warm-up still on: the bridge asks for its end. The plugin ends it there itself (EarlyJoinWatch), and CS2's
    /// own clock in the modes where it does; this is for neither having done it.</summary>
    const int WarmUpLateSeconds = 10;

    /// <summary>A joiner connected this long with no controller in the game yet is at CS2's prompt to download the Workshop items, or downloading them: the game
    /// hears of a client only once it has every addon (ComingState).</summary>
    const int PromptGuessSeconds = 5;

    /// <summary>Off within EarlyJoin.EarlyLeaveSeconds of the first spawn (WarmUpOffSince: the look that found it so) - CS2 leaving its warm-up by itself, which
    /// the plugin begins again once - the bridge looks this long more before it calls the match on: the plugin's look comes a second after the leave, and CS2
    /// is back in its warm-up a frame later.</summary>
    const int WarmUpOffMost = 5;

    /// <summary>A map's start (OnMapStart): a new warm-up to look for.</summary>
    static void WarmUpAtMapStart()
    {
        WarmUpOver();
        WarmUpLooks = WarmUpFindSeconds;
        WarmUpTickSoon();
    }

    /// <summary>The warm-up over, or the server going down: nothing to follow.</summary>
    static void WarmUpOver() => (WarmUpSince, WarmUpFirstSpawn, WarmUpEnd, WarmUpAllInSince, WarmUpComing, WarmUpSpawned, WarmUpOnNow, WarmUpLooks, WarmUpOffSince, WarmUpEnding) = (null, null, null, null, [], 0, false, 0, null, null);

    static void WarmUpTickSoon()
    {
        if (WarmUpTickDue) return;
        WarmUpTickDue = true;
        After(1000, WarmUpTick);
    }

    /// <summary>What the warm-up's early end waits for, or null: a vote decides first, a map change is under way, or the server is about to go down. Everybody
    /// in and !start end it as soon as that is over; CS2's own clock ends it at the most whatever is on.</summary>
    static string? WarmUpHeld(DateTime now) =>
        Ballot is { Open: true } ? "the vote for the next map"
        : Pending != null || LoadPending ? "the map change"
        : RestartDue is { GoesAt: { } at } && at > now.AddSeconds(-RestartStaleMinutes * 60) ? "the server's restart" : null;

    /// <summary>A moment from the plugin's word again: the one before while the new one is within a second and a half of it - the word comes in whole seconds and
    /// over RCON, and the live state should not change each second for nothing.</summary>
    static DateTime Steady(DateTime? before, DateTime now) => before is { } b && Math.Abs((now - b).TotalSeconds) < 1.5 ? b : now;

    /// <summary>A look a second: where the plugin's warm-up stands, who is coming, and whether it ends now - everybody in (two humans at least) and settled; CS2's
    /// own end not come WarmUpLateSeconds past its moment; or a warm-up nobody can say a first spawn of. Off again after somebody spawned: over (WarmUpGone).</summary>
    static void WarmUpTick()
    {
        WarmUpTickDue = false;
        var now = Now();
        if (WarmUpSince == null && WarmUpLooks <= 0) return;
        if (LoadPending || PresentIds(now).Count == 0 && SigningOnNames(now).Count == 0) { WarmUpTickSoon(); return; } // a map loading answers RCON late or not at all; nobody on: nothing to follow it for
        var reply = Send(EarlyJoin.WarmUpCommand);
        var state = EarlyJoin.ReadWarmUp(reply);
        if (state == null && reply != null) // an answer that is none of os_warmup's: a plugin without it - no warm-up, and no more asking on this map
        {
            Log($"warm-up: the plugin does not answer {EarlyJoin.WarmUpCommand} - no warm-up on this map");
            WarmUpOver();
            return;
        }
        if (state == null) // no answer: asked again, and given up after as many looks as a warm-up has to begin (an answer that finds it on gives them back)
        {
            if (--WarmUpLooks > 0) { WarmUpTickSoon(); return; }
            Log($"warm-up: no answer to {EarlyJoin.WarmUpCommand} for {WarmUpFindSeconds} s - not followed on this map");
            WarmUpOver();
            return;
        }
        if (WarmUpEnding is { } ending) // the bridge asked for its end: said once a look finds it off, asked again while it is not
        {
            if (!state.On)
            {
                Log($"warm-up: ended, {(now - ending.At).TotalSeconds:0} s after the bridge last asked - the match is on");
                WarmUpOver();
                Post(Messages.News, EarlyJoin.StartLine(ending.Says, ending.Coming));
                return;
            }
            if ((now - ending.At).TotalSeconds >= WarmUpEndWaitSeconds)
            {
                if (ending.Asks >= 3)
                {
                    Log($"warm-up: asked {ending.Asks} times to end it, and CS2 is still in its warm-up - no more asking, left to the plugin");
                    WarmUpOver();
                    return;
                }
                EndWarmUp(now, ending.Why, ending.Says);
                return;
            }
            WarmUpTickSoon();
            return;
        }
        if (!state.On)
        {
            WarmUpOnNow = false;
            var first = state.SinceFirstSpawn is { } spawnedAgo ? now.AddSeconds(-spawnedAgo) : WarmUpFirstSpawn;
            if (WarmUpSince != null && first is { } spawned)
            {
                // soon after the first spawn CS2 may have left it by itself, which the plugin begins again once (its look comes a second or two after the leave):
                // a few looks more; later, an end is the end - CS2's own clock, the plugin's safety, an operator's
                WarmUpOffSince ??= now;
                if ((WarmUpOffSince.Value - spawned).TotalSeconds < EarlyJoin.EarlyLeaveSeconds + 2 && (now - WarmUpOffSince.Value).TotalSeconds < WarmUpOffMost) { WarmUpTickSoon(); return; }
                WarmUpFirstSpawn ??= spawned;
                WarmUpGone(now);
                return;
            }
            if (--WarmUpLooks > 0) { WarmUpTickSoon(); return; } // none yet, or off before anybody spawned: the plugin begins it again within seconds
            Log(WarmUpSince == null ? "warm-up: none began on this map (the plugin loaded during it?)" : "warm-up: off before anybody spawned, and not begun again - the match runs as CS2 has it");
            WarmUpOver();
            return;
        }
        if (WarmUpSince == null) { WarmUpSince = now; Log("warm-up: on - the match starts once everybody coming has spawned, or at the most"); }
        if (WarmUpOffSince is { } off) Log($"warm-up: on again {(now - off).TotalSeconds:0} s after it went off - the plugin began it again, and it is followed on");
        (WarmUpOnNow, WarmUpLooks, WarmUpOffSince, WarmUpSpawned) = (true, WarmUpFindSeconds, null, state.Spawned);
        if (state.SinceFirstSpawn is { } since) WarmUpFirstSpawn = Steady(WarmUpFirstSpawn, now.AddSeconds(-since));
        WarmUpEnd = state.Left is { } left ? Steady(WarmUpEnd, now.AddSeconds(left)) : null;
        WarmUpComing = JoiningNow(now);
        if (WarmUpLatest() is { } latest && now >= latest.AddSeconds(WarmUpLateSeconds))
        {
            EndWarmUp(now, $"CS2's own end has not come, {(now - latest).TotalSeconds:0} s past it", $"{EarlyJoin.WarmUpMostSeconds} s are up");
            return;
        }
        if (WarmUpFirstSpawn == null && now >= WarmUpSince.Value.AddSeconds(WarmUpBlindSeconds) && PresentIds(now).Count > 0)
        {
            EndWarmUp(now, "nobody can say who has spawned", "The warm-up is over");
            return;
        }
        if (WarmUpComing.Count == 0 && WarmUpSpawned >= 2 && WarmUpHeld(now) == null)
        {
            WarmUpAllInSince ??= now;
            if ((now - WarmUpAllInSince.Value).TotalSeconds >= EarlyJoin.SettleSeconds) { EndWarmUp(now, "everybody is in", "Everybody is in"); return; }
        }
        else WarmUpAllInSince = null;
        WarmUpTickSoon();
    }

    /// <summary>The latest start: CS2's own end by the plugin's word, else the most from the first spawn; null before anybody has spawned.</summary>
    static DateTime? WarmUpLatest() => WarmUpEnd ?? WarmUpFirstSpawn?.AddSeconds(EarlyJoin.WarmUpMostSeconds);

    /// <summary>Who is coming now, by any of three, and what each is doing (ComingState): the plugin's os_joining (its controllers connecting or reconnecting, and
    /// the humans in the game still to spawn on a team), the server's own client list (status: everybody not yet active - the prompt's joiner is "connecting"
    /// there until CS2 drops them, which ends their sign-on once the list has lacked them EarlyJoin.DropConfirmSeconds: EndDroppedSignOns) and the log stream's
    /// sign-ons (SigningOnNames: "connected", not yet "entered the game", and everybody on the way back through a reconnect, off the server's list too;
    /// EarlyJoin.SignOnMostSeconds at the most).</summary>
    static List<(string Name, string State)> JoiningNow(DateTime now)
    {
        var plugin = EarlyJoin.ReadJoining(Send(EarlyJoin.JoiningCommand)) ?? [];
        var clients = ReadServerList(now); // null: no players table - the log stream's sign-ons then count on their own bound
        var byPlugin = plugin.ToHashSet(StringComparer.Ordinal);
        return [.. plugin.Concat(clients?.Where(c => c.Joining).Select(c => c.Name) ?? []).Concat(SigningOnNames(now)).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal)
            .Select(n => (n, ComingState(n, byPlugin, now)))];
    }

    /// <summary>What one still coming is doing, as far as the server can tell. Downloading: on the way back through a reconnect, connected again and off the
    /// server's list since - their own download of the map, or a crash, which the server cannot tell apart; or connected PromptGuessSeconds ago with no controller
    /// in the game yet - CS2's prompt to download the Workshop items, and the download. Picking a team: in the game by the log stream, still to spawn by the
    /// plugin - CS2's team menu. Loading: everybody else on the way in.</summary>
    static string ComingState(string name, HashSet<string> byPlugin, DateTime now)
    {
        var p = Players.Values.FirstOrDefault(x => x.GoneSince == null && x.Name == name);
        if (p is { Returning: not null, OffList: true }) return EarlyJoin.Downloading;
        if (p is { SigningOn: { } at } && !byPlugin.Contains(name) && (now - at).TotalSeconds >= PromptGuessSeconds) return EarlyJoin.Downloading;
        if (byPlugin.Contains(name) && p is { SigningOn: null, Returning: null, ReloadSince: null }) return EarlyJoin.PickingTeam;
        return EarlyJoin.Loading;
    }

    /// <summary>The warm-up ends: the bridge asks the plugin to end CS2's (os_warmup end), whose end starts the real match over for everybody at once. Why, in the
    /// log's words, and as the line for everybody says it (marked); who is still coming joins the match that runs. The line goes out once a look finds it off
    /// (WarmUpEnding) - CS2 ends it seconds after the plugin's request - and a request with no answer, or with no end WarmUpEndWaitSeconds on, is asked again.</summary>
    static void EndWarmUp(DateTime now, string why, string says)
    {
        var asks = WarmUpEnding?.Asks ?? 0;
        var coming = WarmUpEnding?.Coming ?? [.. WarmUpComing.Select(c => c.Name)];
        var reply = Send($"{EarlyJoin.WarmUpCommand} {EarlyJoin.EndWord}");
        Log($"warm-up: {why}, {(WarmUpSince is { } since ? (now - since).TotalSeconds : 0):0} s on{(coming.Count > 0 ? $", still coming: {string.Join(", ", coming)}" : "")} - {EarlyJoin.WarmUpCommand} {EarlyJoin.EndWord}{(asks > 0 ? $" (ask {asks + 1})" : "")} -> {reply?.Replace('\n', ' ') ?? "(rcon failed)"}");
        WarmUpEnding = (reply == null ? now.AddSeconds(-WarmUpEndWaitSeconds) : now, why, says, coming, asks + (reply == null ? 0 : 1)); // no answer: asked again at the next look
        WarmUpTickSoon();
    }

    /// <summary>The warm-up went off after somebody had spawned: CS2's own clock ended it at the most - the latest start, the match on for everybody - or the game
    /// left it sooner by itself (an operator's mp_warmup_end, say). Either way the match is on, and who is still coming joins it.</summary>
    static void WarmUpGone(DateTime now)
    {
        var coming = WarmUpComing.Select(c => c.Name).ToList();
        var atMost = WarmUpLatest() is { } latest && now >= latest.AddSeconds(-2);
        Log($"warm-up: {(atMost ? $"CS2's own clock ended it, {EarlyJoin.WarmUpMostSeconds} s since the first spawn" : "the game left it by itself")}, {(now - WarmUpSince!.Value).TotalSeconds:0} s on{(coming.Count > 0 ? $", still coming: {string.Join(", ", coming)}" : "")} - the match is on");
        WarmUpOver();
        Post(Messages.News, EarlyJoin.StartLine(atMost ? $"{EarlyJoin.WarmUpMostSeconds} s are up" : "The warm-up is over", coming));
    }

    /// <summary>!start: the warm-up ends now, for everybody - a friend who is not coming. Anybody present may; refused outside a warm-up, and while a vote decides,
    /// a change is under way or the server's restart counts down.</summary>
    static void StartNow(string name, string steamId)
    {
        var now = Now();
        if (WarmUpSince == null || !WarmUpOnNow) { Tell(steamId, M.Refused("No warm-up on - The match is on already")); return; }
        if (WarmUpEnding != null) { Tell(steamId, M.Refused("The match is starting already")); return; }
        if (WarmUpHeld(now) is { } held) { Tell(steamId, M.Refused($"Not during {held} - The warm-up ends after it")); return; }
        EndWarmUp(now, $"{name} {steamId} typed {Hud.StartWord}", $"{M.N(Clip(name, 24))} typed {M.C(Hud.StartWord)}");
    }

    /// <summary>Into the live state: the warm-up's countdown banner while it lasts (HudView.CountdownOf) - whom it waits for, and the latest start, which the plugin
    /// counts down; once everybody is in, the settle's end instead. While its early end waits (WarmUpHeld) the line says for what. A map change or a restart
    /// counting down has the banner first.</summary>
    static void PutWarmUp(HudState h, DateTime now)
    {
        if (WarmUpSince == null || !WarmUpOnNow || WarmUpEnding != null) return; // ending: the line for everybody follows in a moment
        var held = WarmUpHeld(now);
        var allIn = WarmUpComing.Count == 0 && WarmUpSpawned >= 2;
        h.WarmUpTitle = EarlyJoin.Title(WarmUpComing, allIn);
        h.WarmUpLine = held != null ? $"The match starts after {held}" : EarlyJoin.Line(WarmUpComing.Count, allIn);
        h.WarmUpAtMs = WarmUpLatest() is { } latest ? HudState.Ms(latest) : 0;
        h.WarmUpGoMs = held == null && WarmUpAllInSince is { } all ? HudState.Ms(all.AddSeconds(EarlyJoin.SettleSeconds)) : 0;
    }

    /// <summary>The health page's line about the warm-up.</summary>
    static string WarmUpHealth() => WarmUpSince is not { } since ? "warm-up: none on"
        : $"warm-up: {(WarmUpEnding is { } ending ? $"ending (asked {ending.Asks} time(s))" : WarmUpOnNow ? "on" : "off at the last look")} {(Now() - since).TotalSeconds:0} s, {WarmUpSpawned} spawned{(WarmUpLatest() is { } latest ? $", the latest start in {Math.Max(0, (latest - Now()).TotalSeconds):0} s" : "")}"
          + (WarmUpComing.Count > 0 ? $", coming: {string.Join(", ", WarmUpComing.Select(c => $"{c.Name} ({c.State})"))}" : "");
}
