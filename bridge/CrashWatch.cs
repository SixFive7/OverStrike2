// The crash counter, working at last (open since 2026-10-05). It used to wait for a change of the X-Server-Instance-Token, which CS2 does
// not renew per process, so a map that took the live server down was never marked. Now it reads RCON status's "spawn" line - how many maps
// this server PROCESS has loaded, 1 for its boot map - a few seconds after every map start, and once when the bridge starts. Within one
// process every map start raises it by one (measured: ten map starts after a boot, spawn 10), so a reading that has
// not risen since the one before means a new process, and the map that was on (or loading) when the old one went is a crash, unless:
//   - the old process went cleanly: the echo of an RCON quit or _restart (!restart, the 07:00 restart watcher, an admin) was seen;
//   - the maintenance flag is set: the map sweeps and the cache work crash and recreate the server on purpose;
//   - the bridge cannot tell which map that was: no reading since it started, or a counter that does not fit the map starts it saw.
// A reading counts only when RCON status shows the map the newest start logged. One taken while that map still loads, or when the server
// is already on a map whose log line has not come in yet, would be compared with the wrong starts - two quick starts would make a crash
// out of nothing. Several starts between two readings are judged together: the new process has loaded `spawn` maps, which are the last
// `spawn` starts, so its boot is the first of those, and what was on just before that boot is what gets the blame.
// A process killed from outside without a quit (a container restart by hand, a freeze kill by the restart watcher) counts too: that is
// what makes a freeze count, and the price is that a restart by hand should set the maintenance flag first. A redeploy of the whole stack
// restarts the bridge as well, which then has no reading of the old process to compare with.
// A recorded crash is what !map's confirmation ("crashed the server N time(s) - are you sure?"), the dice (never a map with a crash) and
// the HUD's card ("Crashed the server Nx") already read. Chat says it at once, and every join hears it for the next CrashNoticeMinutes.
using System.Text.RegularExpressions;

static partial class Program
{
    sealed record SpawnStart(int Number, string Name, string? Before, bool Clean, bool Maintenance);

    static int? SpawnSeen;                 // the spawn counter of the last reading; null until the first since the bridge started
    static int SpawnSeenStarts;            // how many map starts the bridge had seen when that reading was taken
    static int MapStarts;                  // the map starts this bridge has seen (a load's second line not counted)
    static string? LastStartName;          // the map the newest of them logged: what RCON status must show for a reading to count
    static readonly List<SpawnStart> SpawnStarts = new(); // the starts since the last reading, oldest first, each with what it would blame
    static DateTime CrashNoticeUntil;      // joins hear S.CrashNotice until then (the reconnect burst after a crash)
    const int SpawnCheckDelayMs = 5000, SpawnCheckRetryMs = 10000, SpawnBaselineDelayMs = 15000, CrashNoticeMinutes = 10;
    const int SpawnCheckTries = 8; // 5 s, then every 10 s: 75 s covers the longest loads (up to 2.9 GB); a start without a reading is judged with the next one

    [GeneratedRegex(@"^spawn\s*:\s*(\d+)", RegexOptions.Multiline)] private static partial Regex SpawnRx();

    static int? StatusSpawn(string status) => SpawnRx().Match(status) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    static void ResetCrashWatch()
    {
        SpawnSeen = null; SpawnSeenStarts = 0; MapStarts = 0; LastStartName = null; SpawnStarts.Clear(); CrashNoticeUntil = default;
    }

    /// <summary>OnMapStart, for every start: the map it logged, and what was true just before it - the map that was on or loading,
    /// whether the old process went cleanly, the maintenance flag. The reading follows a few seconds later, when the load is done.</summary>
    static void WatchSpawn(string name, string? before, bool clean, bool maintenance)
    {
        var number = ++MapStarts;
        LastStartName = name;
        SpawnStarts.Add(new(number, name, before, clean, maintenance));
        if (SpawnStarts.Count > 50) SpawnStarts.RemoveAt(0);
        After(SpawnCheckDelayMs, () => ReadSpawn(number, 1));
    }

    /// <summary>When the bridge starts: a first reading of the process that is running, so that a crash of the map it is on counts too.</summary>
    static void SpawnBaseline() => ReadSpawn(0, 1);

    static void ReadSpawn(int start, int attempt)
    {
        if (SpawnSeen != null && SpawnSeenStarts >= start) return; // a later reading has covered this start already
        var status = Send("status");
        var spawn = status == null ? null : StatusSpawn(status);
        var on = status == null ? null : StatusMap(status);
        var expect = LastStartName ?? S.LogName; // before any start: the map the bridge last knew to be on
        if (spawn is not { } reading || (expect != null && !expect.Equals(on, StringComparison.OrdinalIgnoreCase)))
        {
            if (attempt < SpawnCheckTries) { After(SpawnCheckRetryMs, () => ReadSpawn(start, attempt + 1)); return; }
            Log($"crash watch: no reading - RCON status {(status == null ? "did not answer" : spawn == null ? "has no spawn line" : $"shows {on ?? "no map"}, not {expect}")}" +
                (start > 0 ? $"; map start {start} is judged with the next reading" : ""));
            return;
        }
        JudgeSpawn(reading);
    }

    static void JudgeSpawn(int spawn)
    {
        var (last, since) = (SpawnSeen, SpawnStarts.ToList());
        SpawnSeen = spawn; SpawnSeenStarts = MapStarts; SpawnStarts.Clear();
        if (last is not { } previous) { Log($"crash watch: spawn {spawn} - the first reading since this bridge started"); return; }
        if (spawn > previous) return; // the same process: every load counts up
        var what = $"a new server process (spawn {previous} -> {spawn})";
        if (since.Count == 0 || spawn < 1 || spawn > since.Count)
        {
            Log($"crash watch: {what} that does not fit the {since.Count} map start(s) seen since the last reading - not judged");
            return;
        }
        var boot = since[since.Count - spawn]; // its `spawn` loads are the last `spawn` starts; the first of them was its boot
        if (boot.Clean) Log($"crash watch: {what} after a clean quit - not a crash");
        else if (boot.Maintenance || InMaintenance()) Log($"crash watch: {what} during maintenance - not recorded");
        else if (boot.Before == null) Log($"crash watch: {what}, but the bridge did not know which map was on - not recorded");
        else RecordCrash(boot.Before, $"the server came back without a quit ({what})");
    }

    static string NameOfKey(string key) =>
        Maps.FirstOrDefault(m => Key(m) == key) is { } m ? Label(m) : Officials.FirstOrDefault(o => Key(o) == key) is { } o ? Label(o) : key;

    /// <summary>A crash of the map with this key: counted (persisted), shown on its HUD card, said in chat, and told to joiners for a while.</summary>
    static void RecordCrash(string key, string why)
    {
        S.Crashes[key] = S.Crashes.GetValueOrDefault(key) + 1;
        CatalogVersion++;
        var name = NameOfKey(key);
        S.CrashNotice = $"Server restarted at {DateTime.Now:HH:mm} after a crash on {Clip(name, 44)}";
        CrashNoticeUntil = Now().AddMinutes(CrashNoticeMinutes);
        Log($"crash recorded: {name} ({key}), {S.Crashes[key]} in all - {why}");
        Announce($"The server went down on {Clip(name, 44)} - it is marked: Play asks first, the dice skip it.", 30);
        Save();
    }
}
