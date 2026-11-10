// What a player's click or typed command asks for: the plugin's POST (HudClick, HudAct), the HUD check's verification, and the menu's actions
// (Command).
using System.Net;
using System.Text;

static partial class Program
{
    /// <summary>The plugin's POST of a click. Runs on the event loop like a chat line, and answers with the state after it in the long poll's
    /// form (HudTransport.cs), so the HUD shows the new vote at once. A loop that is busy for over 2 s gets the state as it is instead; the long
    /// poll brings the rest.</summary>
    static void HudClick(HttpListenerContext c)
    {
        var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { HudAct(body); } finally { AfterWork(); done.TrySetResult(StateAnswer()); } });
        Reply(c, done.Task.Wait(2000) ? done.Task.Result : StateAnswer());
    }

    /// <summary>"[U:1:n]\tname\t!up": the player clicked (or typed a chat command, which the plugin turns into the same click). Only what
    /// Hud.IsAction passes gets through - likes, a map by rank, Workshop id or official name, Start a vote, the vote's clicks, !time, !restart, !rematch -
    /// so the menu can never reach !rcon or a free-form name search.</summary>
    static bool HudAct(string body)
    {
        var f = body.Trim().Split('\t');
        if (f.Length != 3 || !SteamId3Rx().IsMatch(f[0]) || !(Hud.IsAction(f[2]) || MapVote.IsAction(f[2]))) { Log($"hud: refused '{Hud.Clean(body, 80)}'"); return false; }
        var name = Hud.Clean(f[1], 64) is { Length: > 0 } n ? n : "?";
        Log($"hud: {name} {f[0]} clicked {f[2]}");
        Present(f[0], name);
        if (f[2].StartsWith("!hudok ", StringComparison.Ordinal)) { HudVerify(f[0], name, f[2][7..]); return true; } // never a chat command: only a click says it
        if (f[2] == EarlyJoin.Command) { FairRestart(f[0], name); return true; } // never a chat command nor a click: the plugin's own ask (EarlyJoin.cs)
        Command(name, f[0], f[2]);
        return true;
    }

    /// <summary>The HUD check's verifications, in memory and per connection: SteamID -> the HUD its game showed (HudVersion.IsWord). A real join
    /// and a real departure forget the player (OnJoin, Depart): a reconnecting player may have a new HUD. The live state carries the players on the
    /// server only; nothing of it is written to a file.</summary>
    static readonly Dictionary<string, string> HudSeen = new();

    /// <summary>The HUD check's click (HudCheck.cs): that player's game shows the HUD, and which content - a version button's (v4.8.0), or HudVersion.Unknown
    /// (which one not known: a click on another button); Older only from a plugin before release 193. A known version is never forgotten for an unknown one. The first click after a real join that names the version says which HUD the player brought
    /// (Messages.Joined: the centre text, chat and the board); an outdated HUD shows its own banner. Only the plugin's OnCustomHudClicked sends it - typing it
    /// in chat does nothing.</summary>
    static void HudVerify(string steamId, string name, string version)
    {
        if (AccountId(steamId) <= 0) return;
        var had = HudSeen.GetValueOrDefault(steamId);
        if (had != null && (had == version || version == HudVersion.Unknown)) return;
        HudSeen[steamId] = version;
        var known = version != HudVersion.Unknown;
        Log($"hud check: {name} {steamId} verified{(known ? $", HUD {version}" : "")}");
        if (!known) return;
        Post(Messages.Joined, $"{M.N(Clip(name, 32))} joined with {(version == HudVersion.Older ? "an older HUD" : $"HUD {M.V(version)}")}");
    }

    /// <summary>The verifications of the players on the server, as the live state carries them: account -> the HUD its game showed.</summary>
    static Dictionary<long, string> HudVerifiedNow(DateTime now)
    {
        var present = PresentIds(now);
        return HudSeen.Where(kv => present.Contains(kv.Key) && AccountId(kv.Key) > 0).ToDictionary(kv => AccountId(kv.Key), kv => kv.Value);
    }

    /// <summary>!cancel restart, !cancel rematch, !cancel previous or !cancel pick &lt;key&gt;: a click on a button whose countdown runs stops it (Restarts.cs, Picks.cs).</summary>
    static void Cancel(string name, string steamId, string what)
    {
        if (what == "restart") CancelRestart(name, steamId);
        else if (what == "rematch") CancelRematch(name, steamId);
        else if (what == "previous") CancelPick(name, steamId, what);
        else if (what.StartsWith("pick ", StringComparison.Ordinal)) CancelPick(name, steamId, what[5..]);
    }

    // ---- the menu's actions: what a click asks for (HudAct), written like the chat commands they stand for ----
    static void Command(string name, string steamId, string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        if (cmd == "!map" && arg != "") { var (map, mode) = Hud.SplitMode(arg); PickMap(name, steamId, map, mode); }
        else if (cmd == "!restart") RestartServer(name, steamId);
        else if (cmd == "!rematch") RestartMatch(name, steamId);
        else if (cmd == "!cancel") Cancel(name, steamId, arg); // a countdown a click started: its button's Cancel
        else if (cmd == "!time") SetMatchLength(name, steamId, arg);
        else if (cmd == "!next") StartVote(name, steamId, arg == "random"); // VoteRound.cs: Start a vote; "!next random" is !random's
        else if (cmd == "!previous") Previous(name, steamId);
        else if (cmd == "!up") Like(name, steamId, arg, +1);
        else if (cmd == "!down") { var (map, why) = DownReasons.Split(arg); Like(name, steamId, map, why?.Value ?? DownReasons.Plain, why != null); } // Likes.cs: with its reason, if it names one
        else if (cmd == MapVote.Verb) CastBallot(name, steamId, arg);    // VoteRound.cs: a vote on the ballot (a click only)
        else if (cmd == MapVote.BusyVerb) VoteBusy(name, steamId, arg);  // VoteRound.cs: the map browser at work during the vote: the inactivity timer starts again
    }
}
