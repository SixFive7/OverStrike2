// The crash counter. A change of the X-Server-Instance-Token cannot tell a crash: CS2 does not renew it per process, so a map that took the live
// server down would never be marked. It reads RCON status's "spawn" line - how many maps
// this server PROCESS has loaded, 1 for its boot map - a few seconds after every map start, and once when the bridge starts. Within one
// process every map start raises it by one (measured: ten map starts after a boot, spawn 10), so a reading that has
// not risen since the one before means a new process, and the map that was on (or loading) when the old one went is a crash, unless:
//   - the old process went cleanly: the restart watcher said so right before its planned quit (POST /quitting, below),
//     the bridge sent the quit itself (!restart), or the echo of an RCON quit or _restart came in (an admin; and the backup:
//     the engine segfaults on quit - 5 of 5 planned quits since 2026-11-03 - and the last log batch, with the echo in it, dies with it);
//   - the maintenance flag is set: the map sweeps and the cache work crash and recreate the server on purpose;
//   - the bridge cannot tell which map that was: no reading since it started, or a counter that does not fit the map starts it saw.
// A reading counts only when RCON status shows the map the newest start logged. One taken while that map still loads, or when the server
// is already on a map whose log line has not come in yet, would be compared with the wrong starts - two quick starts would make a crash
// out of nothing. Several starts between two readings are judged together: the new process has loaded `spawn` maps, which are the last
// `spawn` starts, so its boot is the first of those, and what was on just before that boot is what gets the blame.
// A process killed from outside without a quit (a container restart by hand, a freeze kill by the restart watcher) counts too: that is
// what makes a freeze count, and the price is that a restart by hand should set the maintenance flag first. A redeploy of the whole stack
// restarts the bridge as well, which then has no reading of the old process to compare with.
// A recorded crash is what a pick's second click ("... crashed the server N time(s). Click Play again within 30 s to go ahead."), the dice
// (never a map with a crash) and the HUD's cards - the map browser's and the ballot's - read. The top line, chat and the board say it at once, and every join
// hears it for the next CrashNoticeMinutes. A mark - this one, or a player-crash mark (BrokenWhy.cs) - lasts until the map gets a Workshop
// update: the lists carry each map's time_updated ("updated", written by the Workshop sync), every mark keeps the one it was
// set under (S.CrashStamps), and a newer one lifts it (ExpireMarks, at start and whenever a list is read again).
using System.Net;
using System.Text.RegularExpressions;

static partial class Program
{
    sealed record SpawnStart(string? Before, bool Clean, bool Maintenance); // what a map start would blame, and whether that may count

    static int? SpawnSeen;                 // the spawn counter of the last reading; null until the first since the bridge started
    static int SpawnSeenStarts;            // how many map starts the bridge had seen when that reading was taken
    static int MapStarts;                  // the map starts this bridge has seen (a load's second line not counted)
    static string? LastStartName;          // the map the newest of them logged: what RCON status must show for a reading to count
    static readonly List<SpawnStart> SpawnStarts = new(); // the starts since the last reading, oldest first, each with what it would blame
    static string CrashNotice = "";        // what joins hear after a crash...
    static DateTime CrashNoticeUntil;      // ...until then (the reconnect burst after a crash); in memory only, so a restarted bridge says nothing old
    static DateTime GoingDownAt;           // when the bridge last heard that the server is quitting on purpose (ServerGoingDown)
    const int GoingDownSeconds = 120;      // an echo or a notice within this long after that is the same quit
    const int SpawnCheckDelayMs = 5000, SpawnCheckRetryMs = 10000, SpawnBaselineDelayMs = 15000, CrashNoticeMinutes = 10;
    const int SpawnCheckTries = 8; // 5 s, then every 10 s: 75 s covers the longest loads (up to 2.9 GB); a start without a reading is judged with the next one

    [GeneratedRegex(@"^spawn\s*:\s*(\d+)", RegexOptions.Multiline)] private static partial Regex SpawnRx();

    static int? StatusSpawn(string status) => SpawnRx().Match(status) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    static void ResetCrashWatch()
    {
        SpawnSeen = null; SpawnSeenStarts = 0; MapStarts = 0; LastStartName = null; SpawnStarts.Clear(); CrashNotice = ""; CrashNoticeUntil = default; GoingDownAt = default;
    }

    /// <summary>POST /quitting/&lt;secret&gt;: the restart watcher, right before it sends a planned quit - "os2-quit\t1\t&lt;kind&gt;" (daily,
    /// update, version, workshop: BridgeWire.QuitBody) - and once more if that quit could not be delivered after all - "os2-quit\t1\tcancel\t&lt;kind&gt;".
    /// The notice is the echo: the process is going down cleanly (ServerGoingDown). The watcher sends no planned quit the bridge has not taken;
    /// a freeze kill it sends no notice for, so a freeze still counts as the crash it is. Runs on the event loop.</summary>
    static void QuitNoticeRequest(HttpListenerContext c) => OnLoop(c, ApplyQuitNotice, "quitting");

    /// <summary>What a quit notice clears for the server's restart (ServerGoingDown). If the notice is taken back the server stays up, on its map and in
    /// its mode, so all of it comes back; a change that was counting down and a vote that was open stay gone. A map start after the notice ends the
    /// clean quit, and with it any way back: it says for itself what is on.</summary>
    sealed record QuitSnapshot(MapInfo? Map, string? PickedBy, string? LogName, string Mode, Modes.Mode? ModeBefore, RestartNotice? Restart,
        bool RollOwed, bool RollNeedsProof, DateTime StartMapAt);

    static QuitSnapshot? BeforeQuit;

    static string ApplyQuitNotice(string body)
    {
        if (BridgeWire.Fields(body, BridgeWire.QuitWord) is not [var first, ..] f || first == "") return $"refused: not a {BridgeWire.QuitWord} {BridgeWire.Version} notice";
        var kind = Clip(first, 20);
        if (kind == BridgeWire.Cancel)
        {
            var what = Clip(f.Length > 1 ? f[1] : "planned", 20);
            if (!S.CleanShutdown) return "ok: nothing was announced";
            S.CleanShutdown = false; GoingDownAt = default;
            if (BeforeQuit is { } was)
            {
                (S.Map, S.PickedBy, S.LogName, S.Mode, ModeBeforeChange, RestartDue) = (was.Map, was.PickedBy, was.LogName, was.Mode, was.ModeBefore, was.Restart);
                (StartRollOwed, StartRollNeedsProof, StartMapAt) = (was.RollOwed, was.RollNeedsProof, was.StartMapAt);
            }
            Save();
            Log($"restart: the restart watcher's {what} quit did not go out - the server stays up on {CurrentLabel()}; a new process from here is no clean quit");
            return "ok: cancelled";
        }
        ServerGoingDown($"the restart watcher's notice: {kind} restart");
        return "ok: going down";
    }

    /// <summary>OnMapStart, for every start: the map it logged, and what was true just before it - the map that was on or loading,
    /// whether the old process went cleanly, the maintenance flag. The reading follows a few seconds later, when the load is done.</summary>
    static void WatchSpawn(string name, string? before, bool clean, bool maintenance)
    {
        var number = ++MapStarts;
        LastStartName = name;
        SpawnStarts.Add(new(before, clean, maintenance));
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

    /// <summary>A mark set now remembers the Workshop update it was set under.</summary>
    static void StampMark(string key) { if (UpdatedOf(key) is > 0 and var t) S.CrashStamps[key] = t; else S.CrashStamps.Remove(key); }

    /// <summary>A crash mark or a player-crash mark ends when its map gets a Workshop update - the author may have fixed it. A mark
    /// set before its map's update time was known is stamped with the first one that is known, and lifted only by a later one.</summary>
    static void ExpireMarks()
    {
        var lifted = new List<string>();
        foreach (var key in S.Crashes.Keys.Concat(S.PlayerCrashes.Keys).Distinct().ToList())
        {
            var updated = UpdatedOf(key);
            if (updated <= 0) continue;
            if (!S.CrashStamps.TryGetValue(key, out var stamp)) { S.CrashStamps[key] = updated; continue; }
            if (updated <= stamp) continue;
            S.Crashes.Remove(key); S.PlayerCrashes.Remove(key); S.CrashStamps.Remove(key);
            lifted.Add(key);
            Log($"crash marks: {NameOfKey(key)} ({key}) got a Workshop update - its mark is lifted");
        }
        foreach (var key in S.CrashStamps.Keys.Where(k => !S.Crashes.ContainsKey(k) && !S.PlayerCrashes.ContainsKey(k)).ToList()) S.CrashStamps.Remove(key);
        if (lifted.Count == 0) return;
        CatalogVersion++;
        Save();
        Announce(lifted.Count == 1 ? $"The map {Clip(NameOfKey(lifted[0]), 44)} got a Workshop update - It can come up at random again" : $"{lifted.Count} marked maps got a Workshop update - They can come up at random again");
    }

    /// <summary>A crash of the map with this key: counted (persisted), shown on its HUD cards, said on the top line, in chat and on the board, and told to joiners for a while.</summary>
    static void RecordCrash(string key, string why)
    {
        S.Crashes[key] = S.Crashes.GetValueOrDefault(key) + 1;
        StampMark(key);
        CatalogVersion++;
        var name = NameOfKey(key);
        CrashNotice = $"Server restarted at {DateTime.Now:HH:mm} after a crash on {Clip(name, 44)}";
        CrashNoticeUntil = Now().AddMinutes(CrashNoticeMinutes);
        Log($"crash recorded: {name} ({key}), {S.Crashes[key]} in all - {why}");
        Post(Messages.ServerDown, $"The server crashed on {M.A(Clip(name, 44))} and has restarted - That map no longer comes up at random, and voting for it takes a second click");
        Save();
    }
}
