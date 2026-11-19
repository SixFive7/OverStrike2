using System.Globalization;

namespace OverStrike2.Core;

/// <summary>What the restart watcher (os2-server), the Workshop sync (os2-sync) and the deploy tools say to the bridge over HTTP: each message's
/// address and its body, written here once for both ends. The bridge's routes are &lt;bridge.url&gt;/&lt;address&gt;/&lt;bridge.secret&gt;; a body is
/// tab-separated lines, the first naming the message and its version (1). The senders build their bodies with these encoders, and the bridge
/// reads them with Fields and its own checks of what follows; every self-test holds the bodies to the words written out (core's above all).</summary>
public static class BridgeWire
{
    // ---- the addresses ----
    public const string Log = "log";                       // the game server's log push (the image's CS2_LOG_HTTP_URL ends in /log/<secret>)
    public const string Health = "health";                 // GET: is the bridge up and hearing the server; its answer starts with HealthWord
    public const string Ready = "ready";                   // the watcher: the queued maps on the disk, and the background download
    public const string Quitting = "quitting";             // the watcher, right before a planned quit, and once more if it did not go out
    public const string RestartNotice = "restart-notice";  // the watcher: a restart coming, or not after all
    public const string Updated = "updated";               // the watcher: the restart is done, and what it changed
    public const string Removed = "removed";               // the sync: the Workshop's verdicts on the listed maps
    public const string Reload = "reload";                 // the sync, or anyone: read the data files again now
    public const string Pools = "pools";                   // the watcher: the installed game's own map pools, at every engine start (GamePools.cs)

    /// <summary>An address as the bridge's routes have it: "/ready/", the secret after it.</summary>
    public static string Route(string address) => $"/{address}/";

    /// <summary>The whole URL of an address: the bridge's root (bridge.url), the address, the secret.</summary>
    public static string Url(string root, string address, string secret) => $"{root.TrimEnd('/')}/{address}/{secret}";

    // ---- the bodies ----
    public const string HealthWord = "os2-health", ReadyWord = "os2-ready", QuitWord = "os2-quit", RestartWord = "os2-restart", UpdatedWord = "os2-updated", RemovedWord = "os2-removed",
        PoolsWord = "os2-pools";
    public const string Version = "1";
    public const string Cancel = "cancel";      // a quit or a restart announced that does not happen after all
    public const string Current = "current";    // the ready report's line of the background download

    /// <summary>The kinds of restart a notice names, as the bridge words them for the players: a CS2 update, an update of ours, the daily restart,
    /// stuck Workshop downloads.</summary>
    public const string GameUpdate = "game-update", OurUpdate = "our-update", Daily = "daily", Downloads = "downloads";

    /// <summary>When a restart goes: at the end of the match that is on, or now (its countdown in the notice's seconds).</summary>
    public const string AtMatchEnd = "match-end", AtOnce = "now";

    /// <summary>A body's first line: its message and the version, "os2-ready\t1".</summary>
    public static string Head(string word) => $"{word}\t{Version}";

    /// <summary>The watcher's report (POST /ready): the maps the queues name that it found on the disk, one Workshop id a line, and the background
    /// download running now, if one is (CurrentLine).</summary>
    public static string ReadyBody(IEnumerable<string> ids, string? current) =>
        Head(ReadyWord) + "\n" + string.Concat(ids.Select(id => id + "\n")) + (current is null ? "" : current + "\n");

    /// <summary>The background download's line: its Workshop id, a whole percent 0-100 or "-" before any progress, and its title, which is the
    /// rest of the line (no tab or line break in it).</summary>
    public static string CurrentLine(string id, int? percent, string title) =>
        $"{Current}\t{id}\t{(percent is { } p ? p.ToString(CultureInfo.InvariantCulture) : "-")}\t{title.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')}";

    /// <summary>The watcher's notice right before a planned quit (POST /quitting), with its own kind (daily, update, version, workshop).</summary>
    public static string QuitBody(string kind) => $"{Head(QuitWord)}\t{kind}\n";

    /// <summary>...and once more when that quit could not go out after all.</summary>
    public static string QuitCancelBody(string kind) => $"{Head(QuitWord)}\t{Cancel}\t{kind}\n";

    /// <summary>A restart coming (POST /restart-notice): its kind (GameUpdate, OurUpdate, Daily, Downloads), when (AtMatchEnd or AtOnce) and the
    /// seconds: with AtOnce the countdown to the quit, with AtMatchEnd from the match's end to the quit.</summary>
    public static string RestartBody(string kind, string when, int seconds) => $"{Head(RestartWord)}\t{kind}\t{when}\t{seconds.ToString(CultureInfo.InvariantCulture)}\n";

    /// <summary>The restart announced does not happen after all.</summary>
    public static string RestartCancelBody(string kind) => $"{Head(RestartWord)}\t{Cancel}\t{kind}\n";

    /// <summary>The restart is done (POST /updated): its kind, and what was installed before and is now - CS2's versions for GameUpdate, our
    /// parts' fingerprints for OurUpdate.</summary>
    public static string UpdatedBody(string kind, string from, string to) => $"{Head(UpdatedWord)}\t{kind}\t{from}\t{to}\n";

    /// <summary>The sync's verdicts on the listed maps (POST /removed): a Workshop id and its verdict a line (ok, removed, hidden, banned).</summary>
    public static string RemovedBody(IEnumerable<(string Id, string Verdict)> verdicts) =>
        Head(RemovedWord) + "\n" + string.Concat(verdicts.Select(v => $"{v.Id}\t{v.Verdict}\n"));

    /// <summary>The installed game's map pools (POST /pools), read out of its own files (GamePools.Read): its version after the head; then a line per
    /// game mode - "pool", its type and mode, the numbers game_type and game_mode select it by, its maps and those of them not installed, each list
    /// comma-separated and either one possibly empty; "installed" and every installed map; "addons" and the installed maps the game ships as community
    /// addons (GamePools.AddonsIn: the bridge keeps them out, the HUD does not show on them - a bridge before release 221a passes the line over); and a line
    /// "name", a map, Valve's name of it.</summary>
    public static string PoolsBody(GamePools.Found found)
    {
        static string One(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
        static string List(IEnumerable<string> maps) => string.Join(',', maps.Select(m => One(m).Replace(',', ' ')));
        var b = new System.Text.StringBuilder(Head(PoolsWord)).Append('\t').Append(One(found.Build)).Append('\n');
        foreach (var p in found.Pools)
            b.Append(CultureInfo.InvariantCulture, $"pool\t{One(p.Type)}\t{One(p.Mode)}\t{p.TypeValue}\t{p.ModeValue}\t{List(p.Maps)}\t{List(p.Missing)}\n");
        b.Append("installed\t").Append(List(found.Installed)).Append('\n');
        b.Append("addons\t").Append(List(found.Addons)).Append('\n');
        foreach (var (map, name) in found.Names.OrderBy(n => n.Key, StringComparer.Ordinal))
            b.Append("name\t").Append(One(map)).Append('\t').Append(One(name)).Append('\n');
        return b.ToString();
    }

    /// <summary>A pools body read back - the bridge's side of PoolsBody: null when its first line is not os2-pools in version 1 or it holds no pool.
    /// A line it does not know is passed over.</summary>
    public static GamePools.Found? ReadPools(string body)
    {
        if (Fields(body, PoolsWord) is not { } head)
            return null;
        static List<string> List(string s) => [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        var pools = new List<GamePools.Pool>();
        var installed = new List<string>();
        var addons = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in body.Replace("\r", "").Split('\n').Skip(1))
            switch (line.Split('\t'))
            {
                case ["pool", var type, var mode, var tv, var mv, var maps, var missing]:
                    pools.Add(new(type, mode, Int(tv), Int(mv), List(maps), List(missing)));
                    break;
                case ["installed", var all]:
                    installed = List(all);
                    break;
                case ["addons", var shipped]:
                    addons = List(shipped);
                    break;
                case ["name", var map, var name] when map.Length > 0 && name.Trim().Length > 0:
                    names[map] = name.Trim();
                    break;
            }
        return pools.Count == 0 ? null : new(head.FirstOrDefault() is { Length: > 0 } build ? build : "?", pools, installed, names, null) { Addons = addons };
    }

    /// <summary>The fields after the message and its version on a body's first line, each trimmed; null when the first line is not this message in
    /// version 1.</summary>
    public static string[]? Fields(string body, string word)
    {
        var f = body.Replace("\r", "").Split('\n')[0].Split('\t').Select(x => x.Trim()).ToArray();
        return f.Length >= 2 && f[0] == word && f[1] == Version ? f[2..] : null;
    }
}
