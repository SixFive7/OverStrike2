using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE GAME'S OWN MAP POOLS (see Watch.cs): every engine start - the watcher starts with the engine, after the image's SteamCMD run - reads
/// each game mode's official maps out of the installed game's own files (core's GamePools.cs: gamemodes.txt and csgo_english.txt in pak01_dir.vpk,
/// and the maps installed) and sends them to the bridge (POST /pools), which plays a mode's official maps from them and falls back on its own
/// table without them. So a map Valve brings back is in the pools by itself, and one it takes away is out, the engine start after the update. Read
/// once per watcher; sent again every PoolsRetry until the bridge takes it, PoolsTries times at most. A read with a problem sends nothing: the
/// bridge keeps the last report it took.</summary>
public sealed partial class Watch
{
    public const double PoolsRetry = 60;          // the pools report, tried again after this...
    public const int PoolsTries = 30;             // ...this many times: half an hour, for a stack whose bridge comes up after the engine

    /// <summary>The read itself, from the game folder (STEAMAPPDIR/game): GamePools.Read; a test hands in its own.</summary>
    public Func<string, GamePools.Found> ReadGame { get; init; } = GamePools.Read;

    bool poolsRead;
    (string Body, int Tries, double Next)? poolsDue;   // the report the bridge has not taken yet

    /// <summary>Once per watcher, on its first tick: the game's pools read, and their report due at once.</summary>
    void ReadPools(double now)
    {
        if (poolsRead)
            return;
        poolsRead = true;
        GamePools.Found found;
        try
        {
            found = ReadGame(Path.Combine(Config.Paths.GameDir, "game"));
        }
        catch (Exception e)   // GamePools.Read never throws; a bug must not cost the restarts
        {
            Log.Line($"WARNING: reading the game's own map pools crashed ({e.GetType().Name}: {e.Message}) - the bridge keeps the pools it has");
            return;
        }
        if (found.Problem is { } problem)
        {
            Log.Line($"WARNING: the game's own map pools could not be read ({problem}) - the bridge keeps the pools it has");
            return;
        }
        var missing = found.Pools.SelectMany(p => p.Missing).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        Log.Line($"the game's own map pools, CS2 {found.Build}: {string.Join(", ", found.Pools.Select(p => $"{p.Mode} {p.Playable.Count}"))}; {found.Installed.Count} maps installed"
                 + (missing.Count > 0 ? $"; named but not installed: {string.Join(", ", missing)}" : ""));
        poolsDue = (BridgeWire.PoolsBody(found), 0, now);
    }

    /// <summary>The report to the bridge, while one is due: taken, no bridge, or given up after PoolsTries tries.</summary>
    void SendPools(double now)
    {
        ReadPools(now);
        if (poolsDue is not { } due || now < due.Next)
            return;
        var (told, reply) = Bridge.Post(BridgeWire.Pools, due.Body, NoticeTimeout);
        if (told is "ok" or "none" || due.Tries + 1 >= PoolsTries)
        {
            Log.Line(told == "ok" ? $"the bridge took the game's own map pools ({Words.ChatSafe(reply ?? "", 200)})" : told == "none" ? "no bridge to tell the game's own map pools"
                     : $"WARNING: the bridge did not take the game's own map pools {PoolsTries} times ({reply}) - it keeps the pools it has");
            poolsDue = null;
            return;
        }
        if (due.Tries == 0)
            Log.Line($"the bridge did not take the game's own map pools ({reply}) - tried again every {PoolsRetry:0} s, up to {PoolsTries} times");
        poolsDue = (due.Body, due.Tries + 1, now + PoolsRetry);
    }
}
