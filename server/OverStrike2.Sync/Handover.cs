using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>Steam's workshop acf as the sync reads and edits it, the game server's maps as this read-only mount shows them, and THE
/// HAND-OVER: a staged download moved to the game server's hand-over folder instead of being deleted, which the restart watcher takes in
/// at the server's next engine start (os2-server's MapsFolder: its marker check, its take-in).</summary>
public static class Handover
{
    public const long MaxBytes = 200L << 30;         // hand-overs the server has not taken in yet: past this much, no more until it does

    public static string WorkshopDir(string root) => Path.Combine(root, "steamapps", "workshop");

    /// <summary>{id: timeupdated} of a workshop acf; {} when there is none or it does not parse.</summary>
    public static Dictionary<string, long> AcfItems(string path)
    {
        try
        {
            return KeyValues.Installed(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return [];
        }
    }

    /// <summary>Drop `ids` from a workshop acf (both sections), parsed back and checked before an atomic replace. Throws FormatException or
    /// IOException.</summary>
    public static void AcfForget(string path, IEnumerable<string> ids)
    {
        var text = KeyValues.Forget(File.ReadAllText(path), ids);
        Files.WriteAtomic(path, text, FileModes.Private);
    }

    /// <summary>({key: value} of WorkshopItemsInstalled, of WorkshopItemDetails) of one item in a workshop acf; null when it has no entry.</summary>
    public static (Dictionary<string, string> Installed, Dictionary<string, string> Details)? AcfEntries(string path, string wid)
    {
        try
        {
            return KeyValues.Entries(File.ReadAllText(path), wid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>(files, bytes) of everything under a folder - what a hand-over's marker names, counted exactly as the restart watcher counts
    /// it before it takes the hand-over in (MapsFolder.FolderStats; the self-test holds the two to each other).</summary>
    public static (long Files, long Bytes) FolderStats(string path)
    {
        long files = 0, total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try
                {
                    total += new FileInfo(f).Length;
                    files++;
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return (files, total);
    }

    public static long FolderBytes(string path) => FolderStats(path).Bytes;

    public static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool Lexists(string path) => IsLink(path) || File.Exists(path) || Directory.Exists(path);

    /// <summary>{id: timeupdated} of the Workshop maps the game server stores: in its acf, with a folder behind content/730/&lt;id&gt; (its
    /// relative links resolve on this read-only mount of the same tree).</summary>
    public static Dictionary<string, long> ServerMaps(string gameDir)
    {
        var ws = Path.Combine(gameDir, "game", "bin", "linuxsteamrt64", "steamapps", "workshop");
        return AcfItems(Path.Combine(ws, "appworkshop_730.acf")).Where(kv => Directory.Exists(Path.Combine(ws, "content", "730", kv.Key)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>{id: bytes} of the complete hand-overs the game server has not taken in yet (their markers).</summary>
    public static Dictionary<string, long> Pending(string folder)
    {
        var out_ = new Dictionary<string, long>();
        IEnumerable<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return out_;
        }
        foreach (var name in names)
            if (name.EndsWith(".json", StringComparison.Ordinal) && Text.Digits(name[..^5]))
                out_[name[..^5]] = Node.ReadJson(Path.Combine(folder, name)) is JsonObject rec ? Node.Int(rec["bytes"]) : 0;
        return out_;
    }

    /// <summary>What serve fetches for the game server, in order: (missing, outdated). missing: the keep set's maps it does not store - the
    /// upvoted maps, each mode's best, the own maps. outdated: the ones it stores in an older version than the one this sync accepted -
    /// state.json's baseline, the version a test passed (or one unchanged since sync.changedSince); for our own maps, listed by hand and never
    /// tested here, the newest the check saw. Never what waits as a hand-over, an excluded id, a map whose new version waits for its test
    /// (once it passes, the staging clean-up hands that one over) or one whose last test failed.</summary>
    public static (List<string> Missing, List<string> Outdated) ServeWants(KeepSetResult keep, IReadOnlyDictionary<string, long> has,
                                                                           IReadOnlyDictionary<string, long> pending, JsonObject state, SyncConfig cfg)
    {
        var baseline = Node.Obj(state["baseline"]);
        var items = Node.Obj(state["items"]);
        var own = new HashSet<string>(keep.Own);
        var only = cfg.OnlyIds;
        var all = new List<string>(keep.Up);
        foreach (var (mode, _, _, _, _) in GameModes.All)
            all.AddRange(keep.Top.TryGetValue(mode, out var top) ? top : []);
        all.AddRange(keep.Own);
        var ids = all.Distinct().Where(w => !pending.ContainsKey(w) && !cfg.Exclude.Contains(w) && (only is null || only.Contains(w))).ToList();
        var missing = ids.Where(w => !has.ContainsKey(w)).ToList();
        var outdated = new List<string>();
        foreach (var wid in ids)
        {
            if (!has.TryGetValue(wid, out var stored))
                continue;
            var rec = Node.Obj(items[wid]);
            if (!own.Contains(wid) && Node.Str(rec["why"]) == "changed" && Node.Str(rec["status"]) is "queued" or "retry" or "downloaded" or "tested")
                continue;                                     // its new version waits for its test
            if (!own.Contains(wid) && rec["verdict"] is not null && Node.Str(rec["verdict"]) != "pass")
                continue;                                     // its last test failed: the Workshop's version now is the one that did
            var accepted = Node.Int(baseline[wid]);
            if (own.Contains(wid))
                accepted = Math.Max(accepted, Node.Int(rec["updated"]));
            if (accepted > stored)
                outdated.Add(wid);
        }
        return (missing, outdated);
    }

    static void RemoveTree(string path)
    {
        if (IsLink(path))
            File.Delete(path);
        else
            Directory.Delete(path, true);
    }

    /// <summary>THE HAND-OVER: a staged download moved to the game server's hand-over folder instead of being deleted. &lt;folder&gt;/&lt;id&gt;/ is
    /// its content (one rename: the staging folder and the hand-over folder are on the game volume, the server's maps too), &lt;id&gt;.manifest
    /// the Steam client's verified manifest of it (SteamCMD keeps it in its depotcache, byte-identical to the one the game server's own
    /// client keeps), and last &lt;id&gt;.json, the marker: version 1, its acf entries as SteamCMD wrote them and what the folder holds. The
    /// watcher takes it in at the server's next engine start. Returns (ok, words); throws IOException when a move fails.</summary>
    public static (bool Ok, string Words) HandOver(string staging, string folder, string wid, string? title, string depotcache, double now,
                                                   Action<string> log)
    {
        var root = WorkshopDir(staging);
        var content = Path.Combine(root, "content", "730", wid);
        var acf = Path.Combine(root, "appworkshop_730.acf");
        var entries = AcfEntries(acf, wid);
        if (entries is not { } e || !new[] { "size", "timeupdated", "manifest" }.All(k => Text.Digits(e.Installed.GetValueOrDefault(k))))
            return (false, "SteamCMD's acf has no complete entry for it");
        if (!Directory.Exists(content) || IsLink(content))
            return (false, "its folder is not in the staging folder");
        var (files, bytes) = FolderStats(content);
        if (files == 0)
            return (false, "its folder is empty");
        Directory.CreateDirectory(folder);
        foreach (var stale in new[] { Path.Combine(folder, wid + ".json"), Path.Combine(folder, wid), Path.Combine(folder, wid + ".manifest") })
        {
            if (Directory.Exists(stale) && !IsLink(stale))
                Directory.Delete(stale, true);                     // an older hand-over the server never took in
            else if (Lexists(stale))
                File.Delete(stale);
        }
        Directory.Move(content, Path.Combine(folder, wid));
        var gid = e.Installed["manifest"];
        var manifest = Path.Combine(depotcache, $"730_{gid}.manifest");
        var withManifest = File.Exists(manifest);
        if (withManifest)
            File.Copy(manifest, Path.Combine(folder, wid + ".manifest"), true);
        var details = new JsonObject();
        foreach (var (k, v) in e.Details)
            details[k] = v;
        if (details.Count == 0)
            details = new JsonObject { ["manifest"] = gid, ["timeupdated"] = e.Installed["timeupdated"] };
        var marker = new JsonObject
        {
            ["version"] = 1, ["id"] = wid, ["title"] = title ?? "", ["files"] = files, ["bytes"] = bytes,
            ["installed"] = new JsonObject { ["size"] = e.Installed["size"], ["timeupdated"] = e.Installed["timeupdated"], ["manifest"] = gid },
            ["details"] = details, ["created"] = (long)Math.Truncate(now),
        };
        Files.WriteAtomic(Path.Combine(folder, wid + ".json"), Node.Dumps(marker) + "\n", FileModes.Private);
        try
        {
            AcfForget(acf, [wid]);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or FormatException)
        {
            log($"hand-over {wid}: SteamCMD's acf still names it ({x.Message})");
        }
        return (true, $"handed over ({Text.Mb(bytes)} in {files} files{(withManifest ? "" : ", no manifest")})");
    }

    /// <summary>Before every SteamCMD session. SteamCMD writes its acf from memory, so it names again a map already handed over; at its next
    /// login it finds that map's folder gone and downloads it again by itself (68 maps, 11.9 GB once, after three restarts of the sync ended
    /// sessions before the serve step's clean-up). So: every acf entry without a staged folder is forgotten, and a staged copy of a map that
    /// waits as a hand-over goes (folder and entry) unless it waits for its test. Returns (forgotten, dropped); throws IOException or
    /// FormatException when the acf cannot be edited.</summary>
    public static (int Forgotten, int Dropped) TidyStaging(string staging, string handoverDir, JsonObject items, Action<string> log)
    {
        var root = WorkshopDir(staging);
        var acf = Path.Combine(root, "appworkshop_730.acf");
        var content = Path.Combine(root, "content", "730");
        if (!File.Exists(acf))
            return (0, 0);
        var named = AcfItems(acf);
        var pending = Pending(handoverDir);
        var ghosts = named.Keys.Where(w => !Directory.Exists(Path.Combine(content, w))).ToList();
        var copies = (Directory.Exists(content) ? Directory.EnumerateFileSystemEntries(content).Select(p => Path.GetFileName(p)!).ToList() : [])
            .Where(w => pending.ContainsKey(w) && Node.Str(Node.Get(items[w], "status")) != "downloaded" && !IsLink(Path.Combine(content, w))).ToList();
        if (ghosts.Count == 0 && copies.Count == 0)
            return (0, 0);
        AcfForget(acf, ghosts.Concat(copies.Where(named.ContainsKey)));
        foreach (var wid in copies)
        {
            try
            {
                RemoveTree(Path.Combine(content, wid));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        log($"staging: {ghosts.Count} acf entr{(ghosts.Count == 1 ? "y" : "ies")} without a folder forgotten, {copies.Count} staged cop{(copies.Count == 1 ? "y" : "ies")} "
            + "of waiting hand-overs removed - SteamCMD would fetch them again by itself");
        return (ghosts.Count, copies.Count);
    }
}
