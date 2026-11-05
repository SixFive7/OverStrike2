// The restart watcher's word before and after a restart it decides. Updates restart the server at the end of the match that is on, players on
// or not - the watcher's server program decides when - and the players hear it coming, and hear what it was.
//
//   POST /restart-notice/<secret>, one line, tab-separated - what the server program's watcher sends (server/OverStrike2.Server, Watch.cs):
//     gg-restart\t1\t<kind>\t<when>\t<seconds>
//       kind     game-update (a CS2 update), our-update (an update of ours), daily (the daily restart), downloads (stuck Workshop downloads);
//                update and ours are taken for the first two as well
//       when     match-end (when the match that is on ends; matchend too) or now
//       seconds  whole seconds, 0 or more: with now, the countdown to the quit; with match-end, the time from the match's end to the quit
//                (the watcher sends 0, and at the match's end a second notice: now, with its countdown)
//     gg-restart\t1\tcancel\t<kind>     the restart announced does not happen after all
//     A match-end notice the bridge has already (the watcher sends it again now and then) changes nothing.
//   POST /updated/<secret>, once the new engine is up:
//     gg-updated\t1\t<kind>\t<from>\t<to>   what was installed and what is now: CS2's versions for game-update, our version's fingerprint for
//                                          our-update (the note then names no number)
//     gg-updated\t1\t<kind>\t<words>         or what it is updated to as players read it ("CS2 1.41.9.5"); may be empty
//   Both answer "ok: ..." when taken and "refused: ..." when not, like POST /quitting (CrashWatch.cs), which still comes right before the quit.
//
// Before: a warning on the news line that stays until the restart - a newer line covers it while it lasts, then it comes back - with its
// countdown, and the restart in the live state (HudState's rst line: the kind, when, the seconds left and the moment it goes, the words), in parts
// a later screen can draw in its own way. A restart at the match's end opens no vote at that match end - the server goes down under it - and its
// countdown starts there. While one counts down nobody calls a vote. After: "Updated to ..." on the news line, and to every player who joins in
// the next UpdatedNoticeMinutes. A CS2 update tells players to restart CS2 afterwards; the rest say back in a minute. A new HUD on the Workshop
// needs no restart, so there is no kind for it.
using System.Globalization;
using System.Net;
using System.Text;

static partial class Program
{
    const string RestartNoticePath = "/restart-notice/", UpdatedPath = "/updated/";
    public const int UpdatedNoticeMinutes = 10;   // a player who joins this long after the update hears what it was
    public const int RestartStaleMinutes = 5;     // a countdown this long past its end without the server going down is over
    public const int RestartMatchEndHours = 6;    // a restart waiting this long for a match's end is over too (the watcher sends it again)

    /// <summary>The kinds a restart notice may name, as the wire names them, and the words for each: why, and what players do afterwards.</summary>
    public static readonly (string Kind, string Why, string After)[] RestartKinds =
    [
        ("game-update", "A CS2 update", "restart CS2 afterwards"),
        ("our-update", "An Over Strike 2 update", "back in a minute"),
        ("daily", "The daily restart", "back in a minute"),
        ("downloads", "Stuck map downloads", "back in a minute"),
    ];

    /// <summary>A kind or a when as the wire may spell it, in the one spelling the bridge keeps.</summary>
    static string RestartWord(string word) => word switch { "update" => "game-update", "ours" => "our-update", "matchend" => "match-end", _ => word };

    /// <summary>A restart the watcher announced: its kind, at the match's end or now, the seconds it gave, when it came, and the moment it goes once
    /// that is known (now: at once; match-end: at the match's end).</summary>
    sealed class RestartNotice
    {
        public string Kind = "", When = "";
        public int Seconds;
        public DateTime NoticeAt;
        public DateTime? GoesAt;
    }

    static RestartNotice? RestartDue;
    static string UpdatedNotice = "";
    static DateTime UpdatedNoticeUntil;

    static void RestartNoticeRequest(HttpListenerContext c) => OnLoop(c, ApplyRestartNotice, "restart notice");

    static void UpdatedRequest(HttpListenerContext c) => OnLoop(c, ApplyUpdated, "updated");

    /// <summary>A POST body handed to the event loop, where the state lives, and its answer sent back within 5 s.</summary>
    static void OnLoop(HttpListenerContext c, Func<string, string> apply, string what)
    {
        var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { done.TrySetResult(apply(body)); } catch (Exception e) { Log($"{what}: " + e); done.TrySetResult("error: " + e.Message); } });
        Reply(c, done.Task.Wait(5000) ? done.Task.Result : "busy: the bridge did not get to it within 5 s");
    }

    static (string Kind, string Why, string After)? RestartKind(string kind) => RestartKinds.FirstOrDefault(k => k.Kind == RestartWord(kind)) is { Kind: not null } k ? k : null;

    static string ApplyRestartNotice(string body)
    {
        var f = body.Replace("\r", "").Split('\n')[0].Split('\t').Select(x => x.Trim()).ToArray();
        if (f.Length < 4 || f[0] != "gg-restart" || f[1] != "1") return "refused: not a gg-restart 1 notice";
        var now = Now();
        if (f[2] == "cancel")
        {
            if (RestartDue is not { } due) return "ok: nothing was announced";
            if (RestartWord(f[3]) != due.Kind) return $"refused: the restart announced is {due.Kind}, not {Clip(f[3], 20)}";
            Log($"restart: the restart watcher takes back its {due.Kind} restart");
            RestartDue = null;
            Announce($"{RestartKind(due.Kind)!.Value.Why}: no restart after all - the server stays up");
            return "ok: cancelled";
        }
        if (RestartKind(f[2]) is not { } kind) return $"refused: the kind is {string.Join(", ", RestartKinds.Select(k => k.Kind))}, not {Clip(f[2], 20)}";
        var when = RestartWord(f[3]);
        if (when is not ("match-end" or "now")) return $"refused: when is match-end or now, not {Clip(f[3], 20)}";
        if (f.Length < 5 || !int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return "refused: the seconds are a whole number";
        seconds = Math.Min(seconds, 86400);
        if (when == "match-end" && RestartDue is { When: "match-end", GoesAt: null } same && same.Kind == kind.Kind)
        {
            (same.Seconds, same.NoticeAt) = (seconds, now); // the watcher's reminder: nothing new to say
            return $"ok: {kind.Kind} restart at the end of this match";
        }
        RestartDue = new RestartNotice { Kind = kind.Kind, When = when, Seconds = seconds, NoticeAt = now, GoesAt = when == "now" ? now.AddSeconds(seconds) : null };
        Log($"restart: the restart watcher announces a {kind.Kind} restart {(when == "now" ? $"in {seconds} s" : $"at the end of this match, {seconds} s after it")}");
        if (when == "now" && Ballot is { Open: true }) EndBallot("the server restarts"); // the warning says why
        Announce(RestartLine(now)!, 10);
        return $"ok: {kind.Kind} restart {(when == "now" ? $"in {seconds} s" : "at the end of this match")}";
    }

    static string ApplyUpdated(string body)
    {
        var f = body.Replace("\r", "").Split('\n')[0].Split('\t');
        if (f.Length < 3 || f[0].Trim() != "gg-updated" || f[1].Trim() != "1") return "refused: not a gg-updated 1 notice";
        if (RestartKind(f[2].Trim()) is not { } kind) return $"refused: the kind is {string.Join(", ", RestartKinds.Select(k => k.Kind))}, not {Clip(f[2].Trim(), 20)}";
        // from and to: CS2's versions name what it is now; our version's fingerprint names nothing a player reads. Else the words as sent.
        var words = Hud.Clean(f.Length >= 5 ? (kind.Kind == "game-update" ? $"CS2 {f[4].Trim()}" : "") : f.Length > 3 ? string.Join(' ', f.Skip(3)) : "", 60);
        var now = Now();
        UpdatedNotice = words != "" ? $"Updated to {words}" : kind.Kind == "our-update" ? "Over Strike 2 is updated - welcome back" : $"{kind.Why} is done - welcome back";
        UpdatedNoticeUntil = now.AddMinutes(UpdatedNoticeMinutes);
        RestartDue = null;
        Log($"restart: the restart watcher says the {kind.Kind} restart is done{(words != "" ? $" - {words}" : "")}");
        Announce(UpdatedNotice, 60);
        return $"ok: {UpdatedNotice}";
    }

    /// <summary>The warning as the news line says it, with its countdown once it counts down; null without a restart coming.</summary>
    static string? RestartLine(DateTime now)
    {
        if (RestartDue is not { } r || RestartKind(r.Kind) is not { } k) return null;
        if (r.GoesAt is not { } at) return $"{k.Why}: the server restarts at the end of this match - {k.After}";
        var left = (int)Math.Ceiling((at - now).TotalSeconds);
        return left > 0 ? $"{k.Why}: the server restarts in {MapVote.Left(left)} - {k.After}" : $"{k.Why}: the server restarts in a moment - {k.After}";
    }

    /// <summary>Into the live state: the restart coming, in its parts, and its warning on the news line while no newer line covers it - with the
    /// countdown of this moment, also where the line on is the warning as it was announced.</summary>
    static void PutRestart(HudState h, DateTime now)
    {
        if (RestartDue is not { } r || RestartLine(now) is not { } line || RestartKind(r.Kind) is not { } k) return;
        h.RestartKind = r.Kind; h.RestartWhen = r.When; h.RestartWords = line;
        if (r.GoesAt is { } at) (h.RestartIn, h.RestartAt) = (Math.Max(0, (int)Math.Ceiling((at - now).TotalSeconds)), new DateTimeOffset(at).ToUnixTimeSeconds());
        if (h.News == "" || h.News.StartsWith(k.Why + ": the server restarts", StringComparison.Ordinal)) (h.News, h.NewsFor) = (line, Math.Max(h.NewsFor, 2)); // refreshed every tick while it stands
    }

    /// <summary>A match ended (OnMatchEnd): a restart waiting for it starts its countdown, and no vote opens - an open one is dropped. True when it did.</summary>
    static bool RestartAtMatchEnd(DateTime now)
    {
        if (RestartDue is not { When: "match-end", GoesAt: null } r) return false;
        r.GoesAt = now.AddSeconds(r.Seconds);
        EndBallot("the server restarts at this match's end");
        Log($"restart: the match is over - the {r.Kind} restart goes in {r.Seconds} s; no vote at this match end");
        Announce(RestartLine(now)!, Math.Max(10, r.Seconds));
        return true;
    }

    /// <summary>Why nobody may call a vote now because of a restart, or null: one counts down.</summary>
    static string? RestartRefusal(DateTime now) => RestartDue is { GoesAt: { } at } && at > now.AddSeconds(-RestartStaleMinutes * 60)
        ? $"The server restarts {(at > now ? $"in {MapVote.Left((int)Math.Ceiling((at - now).TotalSeconds))}" : "now")} - no vote until it is back" : null;

    /// <summary>From ServerGoingDown: the restart announced is happening.</summary>
    static void RestartGoing() { if (RestartDue != null) Log($"restart: the {RestartDue.Kind} restart announced is happening"); RestartDue = null; }

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
        if (UpdatedNotice != "" && Now() < UpdatedNoticeUntil) Tell(steamId, UpdatedNotice, 15);
    }

    /// <summary>The health page's line about restarts.</summary>
    static string RestartHealth() => "restart: " + (RestartDue is { } r ? $"{r.Kind} {r.When}, {(r.GoesAt is { } at ? $"goes in {(at - Now()).TotalSeconds:0} s" : "waiting for the match's end")}" : "none announced")
        + (UpdatedNotice != "" && Now() < UpdatedNoticeUntil ? $"; joiners hear: {UpdatedNotice}" : "");
}
