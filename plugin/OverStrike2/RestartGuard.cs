using System.Globalization;

namespace OverStrike2;

/// <summary>Only our own code restarts the game (release 195). awp_lego_2 (3817962966) sets <c>mp_restartgame 1</c> from a logic_auto's OnMapSpawn, which
/// fires again after every restart: from the first team join the match restarted every 3 s, until the server was quit.
///
/// Nothing here writes the convar inside its own change callback: plugin 5.4.2 put it back to 0 right there, and the engine segfaulted some 170-200 ms
/// later, twice in two refusals (release 195's rehearsal, 2026-11-08). The callback only notes a set (Noted); the looks the plugin makes at every frame -
/// before the world updates, before the entities think, and at the tick - read the convar and put a value that is not ours back to 0 (Look).
///
/// What is ours: the bridge's Restart match (!rematch) - the live state names its moment, and the bridge sends mp_restartgame over RCON when its
/// countdown ends - let through once per moment, from AllowBeforeSeconds before it to AllowAfterSeconds after it, on the bridge's clock; and os_restart,
/// the console's and the server's tools' restart, let through once within OwnSeconds. A value let through stays ours until the game has taken it - it
/// reads 0 again - or a round has started on it.
///
/// A set the game took before any look saw it (noted, gone at the next look, not ours) has escaped: its restart goes through - one - and the plugin then
/// holds the convar for the rest of the map (a cheat setting: the game itself refuses the map's next set). The same hold follows a loop the round
/// starts show: LoopStarts round starts, each within LoopGapSeconds of the one before and none of them the start of a restart of ours. No
/// CounterStrikeSharp type in it: the self-test drives it with a handed clock.</summary>
public sealed class RestartGuard
{
    public const string ConVar = "mp_restartgame";
    public const double AllowBeforeSeconds = 5, AllowAfterSeconds = 15; // around the Restart match's moment, on the bridge's clock
    public const double OwnSeconds = 3;                                  // os_restart's own write, and the frame it may wait for
    public const int LoopStarts = 4;                                     // round starts...
    public const double LoopGapSeconds = 5;                              // ...each this close to the one before: no round of any mode is that short

    /// <summary>Quiet: no restart in the value, and none came and went. LetThrough: a restart of ours, seen for the first time (or taken by the game
    /// already). Ours: the same, still waiting for the game. Refused: not ours - it goes back to 0 now. Escaped: not ours, and the game took it before
    /// this look - its restart goes through, and the convar is to be held for the rest of the map.</summary>
    public enum Verdict { Quiet, LetThrough, Ours, Refused, Escaped }

    DateTime ownUntil = DateTime.MinValue, oursAt = DateTime.MinValue;
    long usedRematchMs;  // the Restart match whose restart went through: one per moment
    bool letThrough;     // the value now is a restart of ours the game has not taken yet
    string? noted;       // a restarting value the change callback saw since the last look
    readonly List<DateTime> starts = new();

    /// <summary>On this map: the restarts refused, those that escaped, and the loops seen.</summary>
    public int Refused { get; private set; }
    public int Escaped { get; private set; }
    public int Loops { get; private set; }

    /// <summary>Whose the last restart let through was: "Restart match" or "os_restart".</summary>
    public string LastOurs { get; private set; } = "none yet";

    /// <summary>os_restart: the console's restart, let through once in the next OwnSeconds.</summary>
    public void Own(DateTime now) => ownUntil = now.AddSeconds(OwnSeconds);

    /// <summary>A value that makes the game restart: a number of at least 1, as the game reads it ("1", " 3 ", "2.5"); 0, a fraction below 1,
    /// empty or no number at all: none.</summary>
    public static bool Restarts(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 1;

    /// <summary>The change callback's whole part: it notes a restarting value, and writes nothing. The game's own reset to 0 and the plugin's
    /// put-back clear no note - the next look does.</summary>
    public void Noted(string? value)
    {
        if (Restarts(value)) noted = value;
    }

    /// <summary>A look at mp_restartgame, which reads value now. rematchAtMs: the bridge's Restart match moment (0: none heard of); bridgeNowMs:
    /// the bridge's clock now. Every Refused and Escaped counts.</summary>
    public Verdict Look(string? value, DateTime now, long rematchAtMs, long bridgeNowMs)
    {
        var came = noted;
        noted = null;
        if (!Restarts(value))
        {
            letThrough = false;
            if (came == null) return Verdict.Quiet;
            if (Allowed(now, rematchAtMs, bridgeNowMs)) return Verdict.LetThrough; // ours, and the game has taken it already
            Escaped++;
            return Verdict.Escaped;
        }
        if (letThrough) return Verdict.Ours;
        if (Allowed(now, rematchAtMs, bridgeNowMs))
        {
            letThrough = true;
            return Verdict.LetThrough;
        }
        Refused++;
        return Verdict.Refused;
    }

    /// <summary>Whether a restart now is ours - os_restart's within its seconds, or the Restart match's around its moment, each once: true takes it.</summary>
    bool Allowed(DateTime now, long rematchAtMs, long bridgeNowMs)
    {
        if (now <= ownUntil)
        {
            ownUntil = DateTime.MinValue;
            return Ours(now, "os_restart");
        }
        if (rematchAtMs > 0 && rematchAtMs != usedRematchMs && bridgeNowMs >= rematchAtMs - (long)(AllowBeforeSeconds * 1000)
            && bridgeNowMs <= rematchAtMs + (long)(AllowAfterSeconds * 1000))
        {
            usedRematchMs = rematchAtMs;
            return Ours(now, "Restart match");
        }
        return false;
    }

    bool Ours(DateTime now, string whose)
    {
        oursAt = now;
        LastOurs = whose;
        starts.Clear();
        return true;
    }

    /// <summary>A round start, with what mp_restartgame reads at it. PutBack: it still reads a restart - ours, which the game has carried out by now, or
    /// one of the loop - and goes back to 0 (from the round start's handler, never the change callback). Loop: true once per run of LoopStarts quick
    /// round starts we did not cause - the convar is to be held for the rest of the map.</summary>
    public (bool PutBack, bool Loop) Started(DateTime now, string? value)
    {
        var lingering = letThrough && Restarts(value);
        letThrough = false;
        if ((now - oursAt).TotalSeconds <= LoopGapSeconds + OwnSeconds) // the round start of a restart of ours
        {
            starts.Clear();
            return (lingering, false);
        }
        if (starts.Count > 0 && (now - starts[^1]).TotalSeconds > LoopGapSeconds) starts.Clear();
        starts.Add(now);
        if (starts.Count < LoopStarts) return (lingering, false);
        starts.Clear();
        Loops++;
        return (lingering || Restarts(value), true);
    }

    /// <summary>A new map: its counts start over; a restart of ours waiting for the game, and a note, are the old map's.</summary>
    public void MapStarted()
    {
        Refused = Escaped = Loops = 0;
        letThrough = false;
        noted = null;
        ownUntil = DateTime.MinValue;
        starts.Clear();
    }

    /// <summary>For os_settings: "refused 3, escaped 0 on this map, no loop; the last let through: Restart match".</summary>
    public string Describe => $"refused {Refused}, escaped {Escaped} on this map, {(Loops == 0 ? "no loop" : $"{Loops} loop(s)")}; the last let through: {LastOurs}";
}
