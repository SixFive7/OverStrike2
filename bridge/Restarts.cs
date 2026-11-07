// The restart watcher's word before and after a restart it decides. Updates restart the server at the end of the match that is on, players on
// or not - the watcher's server program decides when - and the players hear it coming, and hear what it was.
//
//   POST /restart-notice/<secret>, one line, tab-separated - what the server program's watcher sends (server/OverStrike2.Server, Watch.cs), in
//   core's words (BridgeWire.RestartBody):
//     os2-restart\t1\t<kind>\t<when>\t<seconds>
//       kind     game-update (a CS2 update), our-update (an update of ours), daily (the daily restart), downloads (stuck Workshop downloads)
//       when     match-end (when the match that is on ends) or now
//       seconds  whole seconds, 0 or more: with now, the countdown to the quit; with match-end, the time from the match's end to the quit
//                (the watcher sends 0, and at the match's end a second notice: now, with its countdown)
//     os2-restart\t1\tcancel\t<kind>     the restart announced does not happen after all
//     A match-end notice the bridge has already (the watcher sends it again now and then) changes nothing.
//   POST /updated/<secret>, once the new engine is up (BridgeWire.UpdatedBody):
//     os2-updated\t1\t<kind>\t<from>\t<to>   what was installed and what is now: CS2's versions for game-update, our version's fingerprint for
//                                          our-update (the note then names no number)
//     os2-updated\t1\t<kind>\t<words>         or what it is updated to as players read it ("CS2 1.41.9.5"; the client harness's bridgepost.py); may be empty
//   Both answer "ok: ..." when taken and "refused: ..." when not, like POST /quitting (CrashWatch.cs), which still comes right before the quit.
//
// Before: a warning on the top line that stays until the restart - a newer line covers it while it lasts, then it comes back - with its
// countdown, and the restart in the live state (HudState's rst line: the kind, when, the seconds left and the moment it goes, the words), in parts
// each surface draws in its own way. Each step is said once in chat and on the board, by the catalogue's row for it (Messages.cs; SayRestart): waiting for the match's end the status
// banner, a CS2 update the top line instead, both until the restart; counting down the countdown banner. A restart at the match's end opens no vote at that match end - the server goes
// down under it - and its countdown starts there. While one counts down nobody calls a vote. After: "Updated to ..." on the top line, and to every player who joins in the next
// UpdatedNoticeMinutes. A CS2 update tells players to restart CS2 afterwards; the rest say back in a minute. A new HUD on the Workshop needs no restart, so there is no kind for it.
//
// The two restarts a player can press (the Game page, !restart and !rematch) live here too. Anyone may press them, and a second click within 30 s confirms; then PressedSeconds count down
// on the same countdown banner, naming who pressed. Restart the server is a restart of the kind "player": at the end of its countdown ServerGoingDown and the quit. Restart match has its
// own countdown (HudState's rematch line) and then mp_restartgame: the same map, the same mode and the fun settings stay, and the game's round start reads mp_roundtime and starts the
// ladder and catch-up's count over.
using System.Globalization;
using System.Net;

static partial class Program
{
    public const int UpdatedNoticeMinutes = 10;   // a player who joins this long after the update hears what it was
    public const int RestartStaleMinutes = 5;     // a countdown this long past its end without the server going down is over
    public const int RestartMatchEndHours = 6;    // a restart waiting this long for a match's end is over too (the watcher sends it again)
    public const int PressedSeconds = 10;         // Restart the server and Restart match count down this long after their second click
    public const int RematchWaitSeconds = 60;     // the least time between two Restart matches, from one's second click: anyone may press it
    public const string PlayerKind = "player";    // Restart the server: the watcher has no such kind (RestartKinds), and may not name it
    const string GameUpdateKind = BridgeWire.GameUpdate;
    const string AtMatchEnd = BridgeWire.AtMatchEnd, AtOnce = BridgeWire.AtOnce; // a restart's when, as the watcher's notice says it and the live state carries it

    /// <summary>The kinds a restart notice may name, as the wire names them, and the words for each: why, and what players do afterwards.</summary>
    public static readonly (string Kind, string Why, string After)[] RestartKinds =
    [
        (GameUpdateKind, "A CS2 update", "Restart CS2 afterwards, and Steam updates it"),
        (BridgeWire.OurUpdate, "An Over Strike 2 update", "Back in a minute"),
        (BridgeWire.Daily, "The daily restart", "Back in a minute"),
        (BridgeWire.Downloads, "Stuck map downloads", "Back in a minute"),
    ];

    /// <summary>A restart coming, the watcher's or a player's: its kind, at the match's end or now, who pressed it (a player's alone), the seconds it gave, when it came, and the moment it
    /// goes once that is known (now: at once; match-end: at the match's end).</summary>
    sealed class RestartNotice
    {
        public string Kind = "", When = "", By = "";
        public int Seconds;
        public DateTime NoticeAt;
        public DateTime? GoesAt;
    }

    /// <summary>Restart match counting down: who pressed it, and the moment it goes.</summary>
    sealed record Rematch(string By, DateTime At);

    static RestartNotice? RestartDue;
    static Rematch? RematchDue;
    static string UpdatedNotice = "";
    static DateTime UpdatedNoticeUntil;
    static DateTime RestartConfirmUntil, RestartCooldownUntil, RematchConfirmUntil, RematchWaitUntil;
    static string RestartConfirmBy = "", RematchConfirmBy = "";

    static void RestartNoticeRequest(HttpListenerContext c) => OnLoop(c, ApplyRestartNotice, "restart notice");

    static void UpdatedRequest(HttpListenerContext c) => OnLoop(c, ApplyUpdated, "updated");

    static (string Kind, string Why, string After)? RestartKind(string kind) =>
        kind == PlayerKind ? (PlayerKind, "A player's restart", "Back in a minute") : RestartKinds.FirstOrDefault(k => k.Kind == kind) is { Kind: not null } k ? k : null;

    /// <summary>A whole number of seconds to come, as a line says it: "10 s", or "a moment" once it is due.</summary>
    static string LeftWords(DateTime at, DateTime now) => (int)Math.Ceiling((at - now).TotalSeconds) is > 0 and var left ? MapVote.Left(left) : "a moment";

    static string ApplyRestartNotice(string body)
    {
        if (BridgeWire.Fields(body, BridgeWire.RestartWord) is not { Length: >= 2 } f) return $"refused: not a {BridgeWire.RestartWord} {BridgeWire.Version} notice";
        var now = Now();
        if (f[0] == BridgeWire.Cancel)
        {
            if (RestartDue is not { } due) return "ok: nothing was announced";
            if (due.Kind == PlayerKind || f[1] != due.Kind) return $"refused: the restart announced is {due.Kind}, not {Clip(f[1], 20)}";
            Log($"restart: the restart watcher takes back its {due.Kind} restart");
            RestartDue = null;
            Announce($"{RestartKind(due.Kind)!.Value.Why}: no restart after all - The server stays up");
            return "ok: cancelled";
        }
        if (f[0] == PlayerKind || RestartKind(f[0]) is not { } kind) return $"refused: the kind is {string.Join(", ", RestartKinds.Select(k => k.Kind))}, not {Clip(f[0], 20)}";
        var when = f[1];
        if (when is not (AtMatchEnd or AtOnce)) return $"refused: when is {AtMatchEnd} or {AtOnce}, not {Clip(when, 20)}";
        if (f.Length < 3 || !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return "refused: the seconds are a whole number";
        seconds = Math.Min(seconds, 86400);
        if (RestartDue is { Kind: PlayerKind, GoesAt: { } pressed } && pressed > now) return $"ok: {kind.Kind} restart: a player's restart goes first, in {LeftWords(pressed, now)}"; // the server is going down anyway
        if (when == AtMatchEnd && RestartDue is { When: AtMatchEnd, GoesAt: null } same && same.Kind == kind.Kind)
        {
            (same.Seconds, same.NoticeAt) = (seconds, now); // the watcher's reminder: nothing new to say
            return $"ok: {kind.Kind} restart at the end of this match";
        }
        RestartDue = new RestartNotice { Kind = kind.Kind, When = when, Seconds = seconds, NoticeAt = now, GoesAt = when == AtOnce ? now.AddSeconds(seconds) : null };
        Log($"restart: the restart watcher announces a {kind.Kind} restart {(when == AtOnce ? $"in {seconds} s" : $"at the end of this match, {seconds} s after it")}");
        if (when == AtOnce && Ballot is { Open: true }) EndBallot("the server restarts"); // the warning says why
        if (when == AtOnce) RematchDue = null;
        SayRestart(RestartDue, now, 10);
        return $"ok: {kind.Kind} restart {(when == AtOnce ? $"in {seconds} s" : "at the end of this match")}";
    }

    static string ApplyUpdated(string body)
    {
        if (BridgeWire.Fields(body, BridgeWire.UpdatedWord) is not { Length: >= 1 } f) return $"refused: not a {BridgeWire.UpdatedWord} {BridgeWire.Version} notice";
        if (f[0] == PlayerKind || RestartKind(f[0]) is not { } kind) return $"refused: the kind is {string.Join(", ", RestartKinds.Select(k => k.Kind))}, not {Clip(f[0], 20)}";
        // from and to: CS2's versions name what it is now; our version's fingerprint names nothing a player reads. Else the words as sent.
        var words = Hud.Clean(f.Length >= 3 ? (kind.Kind == GameUpdateKind ? $"CS2 {f[2]}" : "") : f.Length > 1 ? string.Join(' ', f.Skip(1)) : "", 60);
        var now = Now();
        UpdatedNotice = words != "" ? $"Updated to {M.V(words)} - Welcome back" : kind.Kind == BridgeWire.OurUpdate ? "Over Strike 2 is updated - Welcome back" : $"{kind.Why} is done - Welcome back";
        UpdatedNoticeUntil = now.AddMinutes(UpdatedNoticeMinutes);
        if (RestartDue is not { Kind: PlayerKind }) RestartDue = null; // a player's countdown, pressed since the server came up, goes on
        Log($"restart: the restart watcher says the {kind.Kind} restart is done{(words != "" ? $" - {words}" : "")}");
        Post(Messages.Updated, UpdatedNotice);
        return $"ok: {M.Plain(UpdatedNotice)}";
    }

    /// <summary>The warning, with left where it says how long is left: the live state's words take Hud.LeftToken, which the HUD counts down from the end time; what chat and the
    /// board keep takes the seconds of this moment. Marked for chat (M). Null for a kind nobody knows.</summary>
    static string? RestartLine(RestartNotice r, string left)
    {
        if (RestartKind(r.Kind) is not { } k) return null;
        if (r.Kind == PlayerKind) return $"{M.N(r.By)} is restarting the server in {M.V(left)} - {k.After}, reconnect then (console: retry)";
        return r.GoesAt == null ? $"{k.Why}: the server restarts at the end of this match - {k.After}" : $"{k.Why}: the server restarts in {M.V(left)} - {k.After}";
    }

    /// <summary>The words a line about this restart starts with, however much is left: the top line that starts so is this restart's, and follows its countdown.</summary>
    static string RestartStem(RestartNotice r, (string Kind, string Why, string After) k) => r.Kind == PlayerKind ? $"{r.By} is restarting the server" : $"{k.Why}: the server restarts";

    /// <summary>A restart announced, or starting to count down: said once, in chat and on the board, by the catalogue's row for this step.</summary>
    static void SayRestart(RestartNotice r, DateTime now, double seconds)
    {
        var kind = r.GoesAt == null ? (r.Kind == GameUpdateKind ? Messages.GameUpdate : Messages.RestartAfterMatch) : r.Kind == PlayerKind ? Messages.PlayerRestart : Messages.RestartCountdown;
        Post(kind, RestartLine(r, r.GoesAt is { } at ? LeftWords(at, now) : "")!, seconds: seconds);
    }

    /// <summary>Into the live state: the restart coming, in its parts, and its warning on the top line while no newer line covers it - with the
    /// countdown of this moment, also where the line on is the warning as it was announced.</summary>
    static void PutRestart(HudState h, DateTime now)
    {
        if (RematchDue is { } rematch) (h.RematchAtMs, h.RematchBy) = (HudState.Ms(rematch.At), rematch.By);
        if (RestartDue is not { } r || RestartLine(r, Hud.LeftToken) is not { } marked || RestartKind(r.Kind) is not { } k) return;
        var line = M.Plain(marked);
        h.RestartKind = r.Kind; h.RestartWhen = r.When; h.RestartWords = line; h.RestartBy = r.By;
        h.RestartAfter = k.Kind == GameUpdateKind ? "Restart CS2 afterwards" : "Back in a minute - Reconnect then (console: retry)";
        if (r.Kind == GameUpdateKind) // a CS2 update has the top line, until the restart: a newer line covers it while it lasts, then it comes back
        {
            if (h.TopText == "") (h.TopText, h.TopCat, h.TopAtMs, h.TopUntilMs) = (r.GoesAt == null ? line : $"{k.Why}: the server restarts for it - {k.After}", Messages.GameUpdate.Cat.Id, HudState.Ms(r.NoticeAt), HudState.Ms(r.GoesAt ?? r.NoticeAt.AddHours(RestartMatchEndHours)));
        }
        else if (r.GoesAt == null) (h.BanTone, h.BanHead, h.BanLine) = ("warning", "The server restarts after this match", $"{k.Why} - {k.After}");
        if (r.GoesAt is { } at) (h.RestartIn, h.RestartAt) = (Math.Max(0, (int)Math.Ceiling((at - now).TotalSeconds)), HudState.Ms(at) / 1000); // on the same clock as every other end time (HudState.Ms)
        if (h.News == "" || h.News.StartsWith(RestartStem(r, k), StringComparison.Ordinal)) // refreshed every tick while it stands
            (h.News, h.NewsFor, h.NewsUntilMs) = (line, Math.Max(h.NewsFor, 2), HudState.Ms(r.GoesAt ?? r.NoticeAt.AddHours(3))); // a fixed end: the state stays the same
    }

    /// <summary>A match ended (OnMatchEnd): a restart waiting for it starts its countdown, and no vote opens - an open one is dropped. A restart counting down already opens none
    /// either: the server goes down under it. True when it did. Restart match counting down is over: the match it would start over has ended by itself.</summary>
    static bool RestartAtMatchEnd(DateTime now)
    {
        if (RematchDue is { } rematch) { Log($"rematch: the match ended before {rematch.By}'s restart of it went out"); RematchDue = null; }
        if (RestartDue is { GoesAt: { } goes } && goes > now) { Log($"restart: the match is over - the {RestartDue.Kind} restart counting down goes on; no vote at this match end"); return true; }
        if (RestartDue is not { When: AtMatchEnd, GoesAt: null } r) return false;
        r.GoesAt = now.AddSeconds(r.Seconds);
        r.When = AtOnce; // it counts down from here: the countdown banner's
        EndBallot("the server restarts at this match's end");
        Log($"restart: the match is over - the {r.Kind} restart goes in {r.Seconds} s; no vote at this match end");
        SayRestart(r, now, Math.Max(10, r.Seconds));
        return true;
    }

    /// <summary>Why nobody may call a vote now because of a restart, or null: one counts down, of the server or of the match.</summary>
    static string? RestartRefusal(DateTime now)
    {
        if (RematchDue is { } rematch) return $"The match restarts in {LeftWords(rematch.At, now)} - No vote until it has";
        return RestartDue is { GoesAt: { } at } && at > now.AddSeconds(-RestartStaleMinutes * 60)
            ? $"The server restarts {(at > now ? $"in {LeftWords(at, now)}" : "now")} - No vote until it is back" : null;
    }

    /// <summary>From ServerGoingDown: the restart announced is happening, and Restart match counting down dies with the process.</summary>
    static void RestartGoing()
    {
        if (RestartDue != null) Log($"restart: the {RestartDue.Kind} restart announced is happening");
        RestartDue = null; RematchDue = null;
    }

    // ---- the Game page's two buttons, and their chat commands ----

    /// <summary>!restart: quit the game process, 10 s after the second click, so nobody is dropped without a word. The container's restart policy starts it again, so pre.sh re-runs and
    /// every cfg is re-executed - which is what clears cvars a map left mangled. Needs no Docker access: the bridge only speaks RCON. The bridge marks the quit clean itself as it sends it
    /// (its echo in the log stream dies with the process when the engine segfaults on quit), so this is never recorded as a crash. A vote that is open and a map change counting down
    /// end at the second click: the server would go down under them. A restart waiting for the match's end gives way to it.</summary>
    static void RestartServer(string by, string steamId)
    {
        var now = Now();
        if (RestartDue is { GoesAt: { } goes } && goes > now)
        {
            Tell(steamId, M.Refused($"The server restarts in {LeftWords(goes, now)} already"));
            return;
        }
        if (now < RestartCooldownUntil)
        {
            Tell(steamId, M.Refused($"Just restarted - Wait {(int)(RestartCooldownUntil - now).TotalSeconds} s before asking again"));
            return;
        }
        if (RestartConfirmBy != steamId || now > RestartConfirmUntil)
        {
            RestartConfirmBy = steamId; RestartConfirmUntil = now.AddSeconds(HudBrowser.ConfirmSeconds);
            Tell(steamId, $"A restart kicks everyone for about a minute - Click or type it again within {HudBrowser.ConfirmSeconds:0} s to confirm", HudBrowser.ConfirmSeconds);
            return;
        }
        RestartConfirmBy = ""; RestartConfirmUntil = default;
        RestartCooldownUntil = now.AddSeconds(RestartCooldown);
        var due = new RestartNotice { Kind = PlayerKind, When = AtOnce, By = Clip(by, 32), Seconds = PressedSeconds, NoticeAt = now, GoesAt = now.AddSeconds(PressedSeconds) };
        RestartDue = due;
        RematchDue = null;
        EndBallot("the server restarts");
        if (Pending is { } change) { Log($"restart: the change to {Label(change.Map)} dies with the restart"); Pending = null; }
        Log($"restart: {by} presses Restart - the quit goes in {PressedSeconds} s");
        SayRestart(due, now, PressedSeconds);
        After(PressedSeconds * 1000, () =>
        {
            if (RestartDue != due) return; // something else took over: a countdown of the watcher's, say
            Log($"restart by {by}");
            ServerGoingDown($"its own !restart by {by}");
            Send("quit");
        });
    }

    /// <summary>Why nobody may press Restart match now, or null: it counts down already, the server is about to go down, the map is about to change or a vote is deciding that, the match
    /// has just ended by itself, or it was just pressed.</summary>
    static string? RematchRefusal(DateTime now)
    {
        if (RematchDue is { } rematch) return $"{rematch.By} is restarting the match already - It goes in {LeftWords(rematch.At, now)}";
        if (RestartDue is { GoesAt: { } at } && at > now.AddSeconds(-RestartStaleMinutes * 60)) return $"The server restarts {(at > now ? $"in {LeftWords(at, now)}" : "now")} - No restart of the match";
        if (Pending is { } p) return $"Already changing to {Clip(Label(p.Map), 40)} - Wait for it";
        if (LoadPending) return "The next map is still loading - Wait for it";
        if (Ballot != null) return "The vote for the next map is on - It decides first";
        if (now < MatchEndUntil) return "This match has just ended - The next one starts by itself";
        if (now < RematchWaitUntil) return $"The match was just restarted - Wait {(int)Math.Ceiling((RematchWaitUntil - now).TotalSeconds)} s before asking again";
        return null;
    }

    /// <summary>!rematch: the match starts over, 10 s after the second click, naming who pressed it. Refused while something else decides what comes next (RematchRefusal); a vote cannot be
    /// called while it counts down (RestartRefusal), and a match end or a map start while it counts down ends it.</summary>
    static void RestartMatch(string by, string steamId)
    {
        var now = Now();
        if (RematchRefusal(now) is { } no) { Tell(steamId, M.Refused(no)); return; }
        if (RematchConfirmBy != steamId || now > RematchConfirmUntil)
        {
            RematchConfirmBy = steamId; RematchConfirmUntil = now.AddSeconds(HudBrowser.ConfirmSeconds);
            Tell(steamId, $"This starts the match over for everybody - Click or type it again within {HudBrowser.ConfirmSeconds:0} s to confirm", HudBrowser.ConfirmSeconds);
            return;
        }
        RematchConfirmBy = ""; RematchConfirmUntil = default;
        RematchWaitUntil = now.AddSeconds(RematchWaitSeconds);
        var due = new Rematch(Clip(by, 32), now.AddSeconds(PressedSeconds));
        RematchDue = due;
        Log($"rematch: {by} presses Restart match - mp_restartgame goes in {PressedSeconds} s");
        Post(Messages.MatchRestart, $"{M.N(due.By)} is restarting the match in {M.V(MapVote.Left(PressedSeconds))} - {RematchWords()}", seconds: PressedSeconds);
        After(PressedSeconds * 1000, () => FireRematch(due));
    }

    /// <summary>What Restart match starts over in the mode that is on, as the line for everybody says it.</summary>
    static string RematchWords() => CurrentMode.Id switch
    {
        Modes.ArmsRaceId => "The ladder, the time and catch-up start over",
        Modes.WingmanId => "The rounds and the scores start over",
        _ => "The time and the scores start over",
    };

    /// <summary>The countdown is over: mp_restartgame goes out, unless something ended it first. The game restarts the match a second later.</summary>
    static void FireRematch(Rematch due)
    {
        if (RematchDue != due) return; // a match end, a map start, the server going down
        RematchDue = null;
        if (Pending != null || LoadPending) { Log($"rematch: a map change came first - {due.By}'s restart of the match does not go out"); return; }
        var reply = Send("mp_restartgame 1");
        Log($"rematch by {due.By}: mp_restartgame 1 -> {reply ?? "(rcon failed)"}");
        if (reply == null) Post(Messages.News, M.Refused("The server did not answer - The match did not restart"));
    }

    /// <summary>From Tick: a countdown long past its end without the server going down, or a match's end that never came, is over.</summary>
    static void RestartTick(DateTime now)
    {
        if (RestartDue is not { } r) return;
        var stale = r.GoesAt is { } at ? now > at.AddMinutes(RestartStaleMinutes) : now > r.NoticeAt.AddHours(RestartMatchEndHours);
        if (!stale) return;
        Log($"restart: the {r.Kind} restart announced at {r.NoticeAt:u} did not happen - the warning goes");
        RestartDue = null;
    }

    /// <summary>A real join while the note of an update is fresh: that player hears it too.</summary>
    static void TellUpdated(string steamId)
    {
        if (UpdatedNotice != "" && Now() < UpdatedNoticeUntil) Post(Messages.UpdatedJoiner, UpdatedNotice, steamId);
    }

    /// <summary>The health page's line about restarts.</summary>
    static string RestartHealth() => "restart: " + (RestartDue is { } r ? $"{r.Kind} {r.When}{(r.By != "" ? $" by {r.By}" : "")}, {(r.GoesAt is { } at ? $"goes in {(at - Now()).TotalSeconds:0} s" : "waiting for the match's end")}" : "none announced")
        + (RematchDue is { } m ? $"; restart match by {m.By} goes in {(m.At - Now()).TotalSeconds:0} s" : "")
        + (UpdatedNotice != "" && Now() < UpdatedNoticeUntil ? $"; joiners hear: {M.Plain(UpdatedNotice)}" : "");
}
