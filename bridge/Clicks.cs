// What a player's click or typed command asks for: the plugin's POST (HudClick, HudAct), the HUD check's verification, and the menu's actions
// (Command).
using System.Net;
using System.Text;

static partial class Program
{
    /// <summary>The plugin's POST of a click. Runs on the event loop like a chat line, and answers with the state after it, so the
    /// HUD shows the new vote at once. A loop that is busy for over 2 s gets the old state back instead; the next poll catches up.</summary>
    static void HudClick(HttpListenerContext c)
    {
        var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
        var versioned = HudVersionAsked(c.Request.Url?.Query) != null; // a plugin that long-polls gets the state in the long poll's form (HudTransport.cs)
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { HudAct(body); } finally { RefreshHud(); PublishHudSafe(); done.TrySetResult(versioned ? StateAnswer() : HudText); } });
        Reply(c, done.Task.Wait(2000) ? done.Task.Result : versioned ? StateAnswer() : HudText);
    }

    /// <summary>"[U:1:n]\tname\t!up": the player clicked (or typed a chat command, which the plugin turns into the same click). Only what
    /// Hud.IsAction passes gets through - likes, a map by rank, Workshop id or official name, Start a vote, the vote's clicks, !time, !restart, !rematch -
    /// so the menu can never reach !rcon or a free-form name search.</summary>
    static bool HudAct(string body)
    {
        var f = body.Trim().Split('\t');
        if (f.Length != 3 || !SteamId3Rx().IsMatch(f[0]) || !(Hud.IsAction(f[2]) || MapVote.IsAction(f[2]))) { Log($"hud: refused '{Hud.Clean(body, 80)}'"); return false; }
        if (f[2].StartsWith("!hudok ", StringComparison.Ordinal)) f[2] = HudCheck.Command(HudCheck.Normal(f[2][7..])); // an older plugin's word, in the one form from here on
        var name = Hud.Clean(f[1], 64) is { Length: > 0 } n ? n : "?";
        Log($"hud: {name} {f[0]} clicked {f[2]}");
        Present(f[0], name);
        if (f[2].StartsWith("!hudok ", StringComparison.Ordinal)) { HudVerify(f[0], name, f[2][7..]); return true; } // never a chat command: only a click says it
        Command(name, f[0], f[2]);
        return true;
    }

    /// <summary>The HUD check's verifications, in memory and per connection: SteamID -> the HUD its game showed (HudCheck.IsVersionWord). A real join
    /// and a real departure forget the player (OnJoin, Depart): a reconnecting player may have a new HUD. The live state carries the players on the
    /// server only; nothing of it is written to a file.</summary>
    static readonly Dictionary<string, string> HudSeen = new();

    /// <summary>The HUD check's click (HudCheck.cs): that player's game shows the HUD, and which content - a version button's (v4.6.0), Older (an
    /// older content's gold button), or Hud.Release (which one not known: another button, or a plugin before v4.6.0, which sends its release). A
    /// known version is never forgotten for an unknown one. The first click after a real join that names the version says which HUD the player brought
    /// (Messages.Joined: the centre text, chat and the board); an outdated HUD shows its own banner. Only the plugin's OnCustomHudClicked sends it - typing it
    /// in chat does nothing.</summary>
    static void HudVerify(string steamId, string name, string version)
    {
        if (AccountId(steamId) <= 0) return;
        var had = HudSeen.GetValueOrDefault(steamId);
        if (had != null && (had == version || version == Hud.Release)) return;
        HudSeen[steamId] = version;
        var known = version != Hud.Release;
        Log($"hud check: {name} {steamId} verified{(known ? $", HUD {version}" : "")}");
        if (!known) return;
        Post(Messages.Joined, $"{M.N(Clip(name, 32))} joined with {(version == HudCheck.Older ? "an older HUD" : $"HUD {M.V(version)}")}");
    }

    /// <summary>The verifications of the players on the server, as the live state carries them: account -> the HUD its game showed.</summary>
    static Dictionary<long, string> HudVerifiedNow(DateTime now)
    {
        var present = PresentIds(now);
        return HudSeen.Where(kv => present.Contains(kv.Key) && AccountId(kv.Key) > 0).ToDictionary(kv => AccountId(kv.Key), kv => kv.Value);
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
        else if (cmd == "!time") SetMatchLength(name, steamId, arg);
        else if (cmd == "!next") StartVote(name, steamId, arg == "random"); // VoteRound.cs: Start a vote; "!next random" is !random's
        else if (cmd == "!previous") Previous(name, steamId);
        else if (cmd == "!up") Vote(name, steamId, arg, +1);
        else if (cmd == "!down") { var (map, why) = DownReasons.Split(arg); Vote(name, steamId, map, why?.Value ?? DownReasons.Plain, why != null); } // Likes.cs: with its reason, if it names one
        else if (cmd == "!newmaps") NewMapsGone(name, steamId);           // NewMapsGone: the setting is gone; an older content's buttons hear so
        else if (cmd == MapVote.Verb) CastBallot(name, steamId, arg);    // VoteRound.cs: a vote on the ballot (a click only)
        else if (cmd == MapVote.BusyVerb) VoteBusy(name, steamId, arg);  // VoteRound.cs: the map browser at work during the vote: the inactivity timer starts again
    }

    /// <summary>What the new-maps buttons and !newmaps answer: the setting is gone, and what took its place.</summary>
    public const string NewMapsGoneNote = "The new-maps setting is gone: every vote now has a fresh Workshop map and a surprise new to us";

    // ---- the new-maps setting is gone: a content that still has its buttons sends !newmaps always|half|never ----

    /// <summary>!newmaps from one of the Game page's old buttons: it changes nothing, and that player hears what took its place.</summary>
    static void NewMapsGone(string by, string steamId)
    {
        Log($"selection: {by} {steamId} clicked a new-maps button - the setting is gone");
        Tell(steamId, M.Refused(NewMapsGoneNote));
    }
}
