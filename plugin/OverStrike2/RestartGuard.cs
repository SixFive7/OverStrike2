using System.Globalization;

namespace OverStrike2;

/// <summary>Only our own code restarts the game (release 195). awp_lego_2 (3817962966) sets <c>mp_restartgame 1</c> from a logic_auto's OnMapSpawn, which
/// fires again after every restart: from the first team join the match restarted every 3 s, until the server was quit. So a value of mp_restartgame
/// that would restart the game is put back to 0 the moment it is set (the settings module's OnConVarChanged, with its look every tick behind it),
/// unless it is ours:
/// - the bridge's Restart match (!rematch): the live state names its moment, and the bridge sends mp_restartgame over RCON when its countdown ends -
///   let through once per moment, from AllowBeforeSeconds before it to AllowAfterSeconds after it, on the bridge's clock;
/// - os_restart, the console's and the server's tools' restart: let through once, within OwnSeconds.
/// A value let through stays ours until the game has taken it - it reads 0 again - or a round has started on it. Behind all that a guard: LoopStarts
/// round starts, each within LoopGapSeconds of the one before and none of them the start of a restart of ours, are a loop we did not cause - logged,
/// and mp_restartgame put back. No CounterStrikeSharp type in it: the self-test drives it with a handed clock.</summary>
public sealed class RestartGuard
{
    public const string ConVar = "mp_restartgame";
    public const double AllowBeforeSeconds = 5, AllowAfterSeconds = 15; // around the Restart match's moment, on the bridge's clock
    public const double OwnSeconds = 3;                                  // os_restart's own write, and the frame it may wait for
    public const int LoopStarts = 4;                                     // round starts...
    public const double LoopGapSeconds = 5;                              // ...each this close to the one before: no round of any mode is that short

    /// <summary>Quiet: no restart in the value. LetThrough: a restart of ours, seen for the first time. Ours: the same, still waiting for the game.
    /// Refused: not ours - it goes back to 0.</summary>
    public enum Verdict { Quiet, LetThrough, Ours, Refused }

    DateTime ownUntil = DateTime.MinValue, oursAt = DateTime.MinValue;
    long usedRematchMs;  // the Restart match whose restart went through: one per moment
    bool letThrough;     // the value now is a restart of ours the game has not taken yet
    readonly List<DateTime> starts = new();

    /// <summary>On this map: the restarts refused, and the loops seen.</summary>
    public int Refused { get; private set; }
    public int Loops { get; private set; }

    /// <summary>Whose the last restart let through was: "Restart match" or "os_restart".</summary>
    public string LastOurs { get; private set; } = "none yet";

    /// <summary>os_restart: the console's restart, let through once in the next OwnSeconds.</summary>
    public void Own(DateTime now) => ownUntil = now.AddSeconds(OwnSeconds);

    /// <summary>A value that makes the game restart: a number of at least 1, as the game reads it ("1", " 3 ", "2.5"); 0, a fraction below 1,
    /// empty or no number at all: none.</summary>
    public static bool Restarts(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 1;

    /// <summary>mp_restartgame reads value now - at a change, or at the look every tick. rematchAtMs: the bridge's Restart match moment (0: none heard
    /// of); bridgeNowMs: the bridge's clock now. Every Refused counts.</summary>
    public Verdict Look(string? value, DateTime now, long rematchAtMs, long bridgeNowMs)
    {
        if (!Restarts(value))
        {
            letThrough = false;
            return Verdict.Quiet;
        }
        if (letThrough) return Verdict.Ours;
        if (now <= ownUntil)
        {
            ownUntil = DateTime.MinValue;
            return Through(now, "os_restart");
        }
        if (rematchAtMs > 0 && rematchAtMs != usedRematchMs && bridgeNowMs >= rematchAtMs - (long)(AllowBeforeSeconds * 1000)
            && bridgeNowMs <= rematchAtMs + (long)(AllowAfterSeconds * 1000))
        {
            usedRematchMs = rematchAtMs;
            return Through(now, "Restart match");
        }
        Refused++;
        return Verdict.Refused;
    }

    Verdict Through(DateTime now, string whose)
    {
        letThrough = true;
        oursAt = now;
        LastOurs = whose;
        starts.Clear();
        return Verdict.LetThrough;
    }

    /// <summary>A round start, with what mp_restartgame reads at it. PutBack: it still reads a restart - ours, which the game has carried out by now, or
    /// one of the loop - and goes back to 0. Loop: true once per run of LoopStarts quick round starts we did not cause.</summary>
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

    /// <summary>A new map: its counts start over; a restart of ours waiting for the game is the old map's.</summary>
    public void MapStarted()
    {
        Refused = Loops = 0;
        letThrough = false;
        ownUntil = DateTime.MinValue;
        starts.Clear();
    }

    /// <summary>For os_settings: "refused 3 on this map, no loop; the last let through: Restart match".</summary>
    public string Describe => $"refused {Refused} on this map, {(Loops == 0 ? "no loop" : $"{Loops} loop(s)")}; the last let through: {LastOurs}";
}
