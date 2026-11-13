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
// own countdown (HudState's rematch line) and then the plugin's os_restart (mp_restartgame 1 to a plugin that has none): the same map, the same mode and the fun settings stay, and the
// game's round start reads mp_roundtime and starts the ladder and catch-up's count over.
using System.Globalization;
using System.Net;

static partial class Program
{
    public const int UpdatedNoticeMinutes = 10;   // a player who joins this long after the update hears what it was
    public const int RestartStaleMinutes = 5;     // a countdown this long past its end without the server going down is over
    public const int RestartMatchEndHours = 6;    // a restart waiting this long for a match's end is over too (the watcher sends it again)
    public const int PressedSeconds = HudMenu.PendingSeconds; // Restart the server and Restart match count down this long after their second click: their buttons count it
    public const int RematchWaitSeconds = 60;     // the least time between two Restart matches, from one's second click: anyone may press it
    public const string PlayerKind = HudState.PlayerRestartKind; // Restart the server: the watcher has no such kind (RestartKinds), and may not name it
    /// <summary>The two restarts' first-click asks begin so (RestartServer, RestartMatch): Withdraw knows them by it.</summary>
    public const string RestartAsk = "A restart kicks everyone for about a minute", RematchAsk = "This starts the match over for everybody";
    static bool IsRestartAsk(string note) => note.StartsWith(RestartAsk, StringComparison.Ordinal);
    static bool IsRematchAsk(string note) => note.StartsWith(RematchAsk, StringComparison.Ordinal);
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

    /// <summary>A restart coming, the watcher's or a player's: its kind, at the match's end or now, who pressed it and their SteamID (a player's alone), the seconds it gave, when it
    /// came, and the moment it goes once that is known (now: at once; match-end: at the match's end).</summary>
    sealed class RestartNotice
    {
        public string Kind = "", When = "", By = "", ById = "";
        public int Seconds;
        public DateTime NoticeAt;
        public DateTime? GoesAt;
    }

    /// <summary>Restart match counting down: who pressed it, and the moment it goes. Fair: the fairness restart's (EarlyJoin, FairRestart) - By is then the player
    /// who came in late, not who pressed it. SteamId: who pressed it, for the chat's !cancel (CancelMine); none for the fairness restart.</summary>
    sealed record Rematch(string By, DateTime At, bool Fair = false, string SteamId = "");

    /// <summary>Who a fairness restart that counts down names, first in first (FairRestart): a join meanwhile folds into it.</summary>
    static readonly List<string> FairNames = new();

    /// <summary>When the last fairness restart went out: a join in the next FairFoldSeconds comes in as the game starts the match over (a second after
    /// os_restart 1), so the restart starts them over too and nothing more is asked.</summary>
    static DateTime FairSentAt = DateTime.MinValue;
    const int FairFoldSeconds = 3;

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
        if (RematchDue is { } rematch)
            (h.RematchAtMs, h.RematchBy, h.RematchFair, h.RematchCount) = rematch.Fair && FairNames.Count > 0
                ? (HudState.Ms(rematch.At), EarlyJoin.Names(FairNames), true, FairNames.Count)
                : (HudState.Ms(rematch.At), rematch.By, rematch.Fair, 1);
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
            if (RestartConfirmBy != "" && RestartConfirmBy != steamId) Withdraw(RestartConfirmBy, IsRestartAsk); // this click takes the window over: theirs is gone
            RestartConfirmBy = steamId; RestartConfirmUntil = now.AddSeconds(HudBrowser.ConfirmSeconds);
            Tell(steamId, $"{RestartAsk} - Click or type it again within {HudBrowser.ConfirmSeconds:0} s to confirm", HudBrowser.ConfirmSeconds);
            return;
        }
        RestartConfirmBy = ""; RestartConfirmUntil = default;
        Withdraw(steamId, IsRestartAsk); // confirmed: the ask is settled
        RestartCooldownUntil = now.AddSeconds(RestartCooldown);
        var due = new RestartNotice { Kind = PlayerKind, When = AtOnce, By = Clip(by, 32), ById = steamId, Seconds = PressedSeconds, NoticeAt = now, GoesAt = now.AddSeconds(PressedSeconds) };
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
        if (RematchDue is { } rematch) return rematch.Fair ? $"The match restarts for fairness already - It goes in {LeftWords(rematch.At, now)}"
            : $"{rematch.By} is restarting the match already - It goes in {LeftWords(rematch.At, now)}";
        if (RematchBlocked(now) is { } no) return no;
        if (now < RematchWaitUntil) return $"The match was just restarted - Wait {(int)Math.Ceiling((RematchWaitUntil - now).TotalSeconds)} s before asking again";
        return null;
    }

    /// <summary>Why no restart of the match may go now, whoever asks - a player's Restart match or the fairness restart: the server is about to go down, the map
    /// is about to change or a vote is deciding that, or the match has ended and its screen is still up (EndScreenUp). Null: it may.</summary>
    static string? RematchBlocked(DateTime now)
    {
        if (RestartDue is { GoesAt: { } at } && at > now.AddSeconds(-RestartStaleMinutes * 60)) return $"The server restarts {(at > now ? $"in {LeftWords(at, now)}" : "now")} - No restart of the match";
        if (Pending is { } p) return $"Already changing to {Clip(Label(p.Map), 40)} - Wait for it";
        if (LoadPending) return "The next map is still loading - Wait for it";
        if (Ballot != null) return "The vote for the next map is on - It decides first";
        if (EndScreenUp(now)) return "This match has just ended - The next one starts by itself";
        return null;
    }

    /// <summary>A match-end screen is never up longer than this: the cfgs' 90 s and a margin.</summary>
    public const int EndScreenMostSeconds = 150;

    /// <summary>The match-end screen is up - the game ignores a restart of the match then: from a match end until the game leaves it, as the plugin's os_phase
    /// says when asked (MatchPhase.cs) - 8 s after the vote played the map again (NextMatch.cs), or after the screen's full 90 s - and never past
    /// EndScreenMostSeconds. A plugin that cannot say (5.15.1 and older) or no answer: the 30 s after the match end (MatchEndUntil), as before, which the
    /// vote's cut ends at its countdown's end (CloseBallot).</summary>
    static bool EndScreenUp(DateTime now)
    {
        if (EndScreenAt is not { } at) return false;
        if (now >= at.AddSeconds(EndScreenMostSeconds)) { EndScreenAt = null; return false; }
        var reply = Send(MatchPhase.Command);
        if (MatchPhase.Read(reply) is not { } phase) return now < MatchEndUntil;
        if (phase == MatchPhase.Ended) return true;
        EndScreenAt = null;
        Log($"match end: the next match is on, {(now - at).TotalSeconds:0} s after the end - {MatchPhase.Command} -> {reply}");
        return false;
    }

    /// <summary>!rematch: the match starts over, 10 s after the second click, naming who pressed it. Refused while something else decides what comes next (RematchRefusal); a vote cannot be
    /// called while it counts down (RestartRefusal), and a match end or a map start while it counts down ends it.</summary>
    static void RestartMatch(string by, string steamId)
    {
        var now = Now();
        if (RematchRefusal(now) is { } no) { Tell(steamId, M.Refused(no)); return; }
        if (RematchConfirmBy != steamId || now > RematchConfirmUntil)
        {
            if (RematchConfirmBy != "" && RematchConfirmBy != steamId) Withdraw(RematchConfirmBy, IsRematchAsk); // this click takes the window over: theirs is gone
            RematchConfirmBy = steamId; RematchConfirmUntil = now.AddSeconds(HudBrowser.ConfirmSeconds);
            Tell(steamId, $"{RematchAsk} - Click or type it again within {HudBrowser.ConfirmSeconds:0} s to confirm", HudBrowser.ConfirmSeconds);
            return;
        }
        RematchConfirmBy = ""; RematchConfirmUntil = default;
        Withdraw(steamId, IsRematchAsk); // confirmed: the ask is settled
        RematchWaitUntil = now.AddSeconds(RematchWaitSeconds);
        var due = new Rematch(Clip(by, 32), now.AddSeconds(PressedSeconds), SteamId: steamId);
        RematchDue = due;
        Log($"rematch: {by} presses Restart match - the restart goes in {PressedSeconds} s");
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

    /// <summary>!cancel restart: Restart the server's countdown stops - a player's restart; the watcher's are not the players' to stop. Anybody may, as anybody may press the
    /// button; the news line says who. Its cooldown goes with it: a restart that never happened. What its second click ended - an open vote, a change - stays ended.</summary>
    static void CancelRestart(string by, string steamId)
    {
        var now = Now();
        if (RestartDue is not { Kind: PlayerKind, GoesAt: { } goes } due || goes <= now) { Tell(steamId, M.Refused("No restart of the server counts down")); return; }
        RestartDue = null;
        RestartCooldownUntil = default;
        Log($"restart: {by} {steamId} cancels {due.By}'s restart of the server, {LeftWords(goes, now)} before it went");
        Announce($"{M.N(Clip(by, 24))} cancelled {(Clip(by, 32) == due.By ? "their" : $"{M.N(due.By)}'s")} restart of the server - It plays on");
    }

    /// <summary>!cancel rematch: Restart match's countdown stops - a player's; the fairness restart is no button's. Anybody may; the news line says who. Its wait goes with it,
    /// so the next one can be pressed at once.</summary>
    static void CancelRematch(string by, string steamId)
    {
        var now = Now();
        if (RematchDue is not { Fair: false } due || due.At <= now) { Tell(steamId, M.Refused("No restart of the match counts down")); return; }
        RematchDue = null;
        RematchWaitUntil = default;
        Log($"rematch: {by} {steamId} cancels {due.By}'s restart of the match, {LeftWords(due.At, now)} before it went");
        Announce($"{M.N(Clip(by, 24))} cancelled {(Clip(by, 32) == due.By ? "their" : $"{M.N(due.By)}'s")} restart of the match - It plays on");
    }

    /// <summary>The countdown is over: the restart goes out, unless something ended it first. The game restarts the match a second later.</summary>
    static void FireRematch(Rematch due)
    {
        if (RematchDue != due) return; // a match end, a map start, the server going down
        RematchDue = null;
        if (Pending != null || LoadPending) { Log($"rematch: a map change came first - {due.By}'s restart of the match does not go out"); return; }
        if (MatchLength.InRounds(CurrentMode.Id)) ApplyMatchLength(); // MatchTime.cs: the match starts over, its rounds too - Wingman's chosen count takes it whole
        if (due.Fair) FairSentAt = Now();
        var (sent, reply) = RematchSend(Send);
        Log($"rematch by {due.By}: {sent} -> {reply ?? "(rcon failed)"}");
        if (RematchRefused(reply) is { } why) Post(Messages.News, M.Refused(why));
    }

    /// <summary>The fairness restart (EarlyJoin.cs): the plugin's ask, for every human who joins a match in its first minute while somebody else plays it, in every
    /// mode. One that counts down already takes the join in - one restart, the banner and a line naming them all - and so does the one that has
    /// just gone out (FairFoldSeconds), or a player's Restart match counting down: each starts the newcomer over too. Else the match restarts, unless the server
    /// is about to go down, the map is about to change, a vote decides or the match has just ended (RematchBlocked; the minute after a restart does not hold it,
    /// since every join may restart again): EarlyJoin.CountdownSeconds on the countdown banner, naming who came in, then the plugin's os_restart (FireRematch).
    /// It starts no wait: a player's Restart match may go right after it - only a pressed one makes the next wait RematchWaitSeconds.</summary>
    static void FairRestart(string steamId, string name)
    {
        var now = Now();
        name = Clip(name, 32);
        if (RematchDue is { Fair: true } pending)
        {
            if (!FairNames.Contains(name)) FairNames.Add(name);
            var left = Math.Max(1, (int)Math.Ceiling((pending.At - now).TotalSeconds));
            Log($"fair: {name} {steamId} joined while the fairness restart counts down - one restart, naming {FairNames.Count}");
            Post(Messages.MatchRestart, EarlyJoin.Line(EarlyJoin.Names(FairNames), FairNames.Count, left), seconds: left);
            return;
        }
        if (RematchDue is { } other) { Log($"fair: {name} {steamId} joined while {other.By}'s restart of the match counts down - it starts them over too"); return; }
        if ((now - FairSentAt).TotalSeconds < FairFoldSeconds) { Log($"fair: {name} {steamId} joined as the fairness restart went out - it starts them over too"); return; }
        if (RematchBlocked(now) is { } no) { Log($"fair: {name} {steamId} joined - no restart: {M.Plain(no)}"); return; }
        FairNames.Clear();
        FairNames.Add(name);
        var due = new Rematch(name, now.AddSeconds(EarlyJoin.CountdownSeconds), Fair: true);
        RematchDue = due;
        Log($"fair: {name} {steamId} joined a match in its first {EarlyJoin.WindowSeconds} s - it restarts for fairness in {EarlyJoin.CountdownSeconds} s");
        Post(Messages.MatchRestart, EarlyJoin.Line(name, 1), seconds: EarlyJoin.CountdownSeconds);
        After(EarlyJoin.CountdownSeconds * 1000, () => FireRematch(due));
    }

    /// <summary>What the Restart match sends: the plugin's os_restart (release 196), which its restart guard lets through as ours - also while it holds
    /// mp_restartgame after a map's escaped restart or a loop, when a bare mp_restartgame is cheat protected and the game ignores it.</summary>
    public const string RematchCommand = "os_restart 1";
    /// <summary>...and to a plugin that has no os_restart (5.4.1 and older: "Unknown command"), mp_restartgame 1 as before.</summary>
    public const string RematchFallback = "mp_restartgame 1";

    /// <summary>The restart's RCON: what went out last, and the game's answer to it (null: none).</summary>
    static (string Sent, string? Reply) RematchSend(Func<string, string?> send)
    {
        var reply = send(RematchCommand);
        return reply != null && reply.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ? (RematchFallback, send(RematchFallback)) : (RematchCommand, reply);
    }

    /// <summary>Why the match did not restart, as everybody is told, or null when it does: no answer, or a change the game ignored ("SV: Convar
    /// 'mp_restartgame' is cheat protected, change ignored.").</summary>
    static string? RematchRefused(string? reply) =>
        reply == null ? "The server did not answer - The match did not restart"
        : reply.Contains("change ignored", StringComparison.OrdinalIgnoreCase) || reply.Contains("cheat protected", StringComparison.OrdinalIgnoreCase)
            ? "The server refused the restart - The match did not restart"
        : null;

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
