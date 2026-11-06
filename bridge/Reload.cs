// The data files reload while the bridge runs. maps.json (MAPS_FILE), broken.json (BROKEN_FILE), /data/removed.json and the rest of DataFiles
// are data, and changing one must never restart anything: a cs2 restart kicks everybody, and even a bridge restart loses an open vote and
// any countdown. So the bridge watches them. Every WatchSeconds it compares each file's size and write time with what it last read; a
// file that has changed and then held still for one more look is read again, so a copy still being written is never read half-way. POST
// /reload/<LOG_SECRET> reads them all at once ("?force" also takes a maps.json that cuts the list by more than half).
// A file that does not parse, or a maps.json that is empty or cut by more than half, changes nothing: the bridge keeps what it had, logs
// why once, and looks again when the file changes. The bridge's own writes of removed.json are not read back.
// What a reload does at once: the HUD's map menu refetches (CatalogVersion), a card on an open ballot or a countdown for a map that has just
// gone from the Workshop or onto the blocked list is dropped, and a map that is on and has just gone from the Workshop makes way for
// the dice.
using System.Net;
using System.Text.Json;

static partial class Program
{
    sealed record Stamp(long Length, DateTime Written);

    const string ReloadPath = "/reload/";
    const int WatchSeconds = 5;
    static readonly List<(string Path, Func<string> Reload)> Watched = new();
    static readonly Dictionary<string, Stamp?> ReadStamp = new(), LookStamp = new(); // per file: the version last read / last looked at
    static int watchTicks;

    static Stamp? StampOf(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? new(f.Length, f.LastWriteTimeUtc) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The files the running bridge watches, each with how it is read again - the same list POST /reload reads (force: a
    /// maps.json or mode list that cuts the list by more than half is taken too).</summary>
    static List<(string, Func<string>)> DataFiles(bool force = false)
    {
        var files = new List<(string, Func<string>)> { (MapsFile, () => ReloadMaps(MapsFile, force)) };
        if (BrokenFile != "") files.Add((BrokenFile, () => ReloadBroken(BrokenFile)));
        files.Add((RemovedPath, ReloadRemoved));
        files.Add((SearchFile, () => ReloadSearch(SearchFile)));
        files.Add((SizesFile, () => ReloadSizes(SizesFile)));
        foreach (var mode in Modes.All.Where(m => !m.ArmsRace)) files.Add((ModeMapsPath(mode), () => ReloadModeMaps(mode, force))); // ModeSwitch.cs
        files.Add((OursFile, () => ReloadOurs(OursFile)));
        files.Add((BlockedMapsFile, ReloadBlocked)); // Blocked.cs: the maps blocked for good, a file of their own
        files.Add((RatingsFile, () => ReloadRatings(RatingsFile)));   // Popularity.cs: the Workshop's ratings and trend
        files.Add((WorkshopFile, () => ReloadWorkshop(WorkshopFile))); // Popularity.cs: the Workshop's numbers and the sync's last passing tests
        return files;
    }

    /// <summary>Starts watching these files, as they are now (what is on disk was read at start).</summary>
    static void WatchStart(IEnumerable<(string Path, Func<string> Reload)> files)
    {
        WatchStop();
        foreach (var (path, reload) in files)
        {
            Watched.Add((path, reload));
            ReadStamp[path] = LookStamp[path] = StampOf(path);
        }
    }

    static void WatchStop() { Watched.Clear(); ReadStamp.Clear(); LookStamp.Clear(); watchTicks = 0; }

    /// <summary>From Tick, once a second; looks every WatchSeconds. Nothing is watched in --selftest unless a test starts it.</summary>
    static void WatchTick()
    {
        if (Watched.Count == 0 || ++watchTicks % WatchSeconds != 0) return;
        WatchFiles();
    }

    static void WatchFiles()
    {
        foreach (var (path, reload) in Watched.ToList())
        {
            var now = StampOf(path);
            if (now == ReadStamp.GetValueOrDefault(path)) { LookStamp[path] = now; continue; } // as read
            if (now != LookStamp.GetValueOrDefault(path)) { LookStamp[path] = now; continue; } // changed since the last look: still being written?
            ReadStamp[path] = now; // changed, and held still for one look: read it once, whatever comes of it
            Log(now == null ? $"reload: {path} is gone - the bridge keeps what it read" : "reload: " + reload());
        }
    }

    /// <summary>The bridge wrote this file itself: what is on disk now is what it has.</summary>
    static void NoteOwnWrite(string path)
    {
        if (!ReadStamp.ContainsKey(path)) return;
        ReadStamp[path] = LookStamp[path] = StampOf(path);
    }

    /// <summary>maps.json again. Kept as it was when the file does not parse, holds no maps, or (unless forced) has less than half of them.</summary>
    static string ReloadMaps(string path, bool force)
    {
        List<MapInfo>? list;
        try { list = JsonSerializer.Deserialize(File.ReadAllText(path), Ctx.Default.ListMapInfo); }
        catch (Exception e) { return $"maps: {path} unreadable ({Clip(e.Message, 120)}) - kept the {Maps.Count} maps loaded"; }
        if (list is not { Count: > 0 }) return $"maps: {path} holds no maps - kept the {Maps.Count} maps loaded";
        if (!force && list.Count * 2 < Maps.Count)
            return $"maps: {path} has {list.Count} maps, less than half of the {Maps.Count} loaded - kept them (POST {ReloadPath}<secret>?force takes it)";
        var (was, now) = (Maps.Select(m => m.Id).ToHashSet(), list.Select(m => m.Id).ToHashSet());
        var (added, gone) = (now.Count(id => !was.Contains(id)), was.Count(id => !now.Contains(id)));
        var count = Maps.Count;
        Maps = list;
        CatalogVersion++;
        ExpireMarks(); // CrashWatch.cs: a map's Workshop update lifts its crash mark
        return $"maps: {list.Count} maps from {path} (were {count}: {added} new, {gone} gone)";
    }

    /// <summary>broken.json again - every list it carries (BrokenList.cs): the crashers, their reasons, the non-maps and the blocked maps.</summary>
    static string ReloadBroken(string path)
    {
        BrokenLists lists;
        try { lists = ParseBrokenFile(File.ReadAllText(path)); }
        catch (Exception e) { return $"broken list: {path} unreadable ({Clip(e.Message, 120)}) - kept the lists loaded"; }
        var before = Unplayables();
        TakeBrokenLists(lists);
        CatalogVersion++;
        var acted = AfterListChange(before);
        return $"broken list: {lists.Broken.Count} crashers, {lists.Packs.Count} non-maps, {lists.Blocked.Count} blocked from {path}{acted}";
    }

    /// <summary>/data/removed.json again, when something other than the bridge changed it.</summary>
    static string ReloadRemoved()
    {
        RemovedFile file;
        try { file = File.Exists(RemovedPath) ? JsonSerializer.Deserialize(File.ReadAllText(RemovedPath), Ctx.Default.RemovedFile) ?? new() : new(); }
        catch (Exception e) { return $"removed list: {RemovedPath} unreadable ({Clip(e.Message, 120)}) - kept the verdicts loaded"; }
        var before = Unplayables();
        Checked = file;
        RecomputeRemoved();
        CatalogVersion++;
        var acted = AfterListChange(before);
        return $"removed list: {Checked.Verdicts.Count} verdict(s) from {RemovedPath}, {Removed.Count} map(s) gone from the Workshop{acted}";
    }

    /// <summary>The Workshop ids no player can play now: gone from the Workshop, blocked, or no map (a pack).</summary>
    static HashSet<string> Unplayables() => Removed.Keys.Concat(Blocked.Keys).Concat(Packs.Keys).ToHashSet();

    /// <summary>What changes at once when a list moved: every map that has just become unplayable - gone, blocked or found to be no map - leaves an
    /// open ballot and stops a countdown, and the map that is on, if it has just gone from the Workshop, makes way for the dice (Removed.cs,
    /// ActOnUnplayable). Returns what happened, for the log line.</summary>
    static string AfterListChange(HashSet<string> before)
    {
        var now = Unplayables();
        var newly = now.Where(id => !before.Contains(id)).ToHashSet();
        var back = before.Count(id => !now.Contains(id));
        if (newly.Count > 0) ActOnUnplayable(newly);
        return newly.Count + back == 0 ? "" : $"; {newly.Count} newly unplayable, {back} playable again";
    }

    /// <summary>Everything at once, whether it changed or not: POST /reload/&lt;secret&gt;, for whoever does not want to wait for the watch.
    /// Every file the watch covers (DataFiles) - search.json was once left out here but marked as read, so a new one waited
    /// for a restart (the sync's find).</summary>
    static string ReloadAll(bool force) => ReloadFiles(DataFiles(force));

    /// <summary>Reads each of these files now, and marks as read for the watch only the files it read.</summary>
    static string ReloadFiles(List<(string Path, Func<string> Reload)> files)
    {
        var lines = new List<string>();
        foreach (var (path, reload) in files)
        {
            lines.Add(reload());
            if (ReadStamp.ContainsKey(path)) ReadStamp[path] = LookStamp[path] = StampOf(path);
        }
        foreach (var line in lines) Log("reload (asked): " + line);
        return string.Join("\n", lines);
    }

    static void ReloadRequest(HttpListenerContext c)
    {
        var force = c.Request.Url?.Query.Contains("force", StringComparison.OrdinalIgnoreCase) == true;
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { done.TrySetResult(ReloadAll(force)); } catch (Exception e) { Log("reload: " + e); done.TrySetResult("error: " + e.Message); } });
        Reply(c, done.Task.Wait(10000) ? done.Task.Result : "busy: the bridge did not get to it within 10 s");
    }
}
