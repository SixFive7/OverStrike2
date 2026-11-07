using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Sync;

/// <summary>One test load of an item that is in the staging folder, on the test engine: PASS when a level of its own loads, A2S reports
/// it and the engine holds it for the hold time with two bots; FAIL on the error map, a crash, a freeze (A2S silent 45 s), a stock map
/// instead of its own (a stub), no level of its own in time, or the bots thrown off.</summary>
public static partial class TestLoad
{
    [GeneratedRegex("Downloading (\\d+) \\([^/]*/\\s*([\\d.]+)\\s*(KB|MB|GB)\\)")] public static partial Regex DownloadingRx();
    [GeneratedRegex("Level loading started for '([^']*)'")] public static partial Regex LevelRx();
    [GeneratedRegex("kick|Dropped .* from server|was banned", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex KickRx();
    [GeneratedRegex("rcon from|bot_kick|bot_quota|Unknown command|ReplicateConVar|sv_kick_ban_duration|sv_vote_issue_kick|sv_vote_kick_ban|autokick",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex NoiseRx();
    [GeneratedRegex("\\[All Chat\\]\\[[^\\]]*\\]:\\s*(.*)$|DISALLOWED WORKSHOP COMMANDS:\\s*(.*)$")] public static partial Regex ChatRx();

    /// <summary>A2S cuts map names at 31 characters: equal, or A2S's name is a 31+-character prefix of the level.</summary>
    public static bool A2sIs(string? a2sMap, string? level)
    {
        var a = (a2sMap ?? "").ToLowerInvariant();
        var l = (level ?? "").ToLowerInvariant();
        return a == l || (a.Length >= 31 && l.StartsWith(a, StringComparison.Ordinal));
    }

    /// <summary>The mode a test load runs a map in: the first mode its tags name, else Arms Race - (mode, game_type, game_mode).</summary>
    public static (string Mode, int GameType, int GameMode) ModeOf(IEnumerable<string> tags)
    {
        var set = tags.ToList();
        foreach (var (mode, tag, gt, gm, _) in GameModes.All)
            if (set.Contains(tag))
                return (mode, gt, gm);
        return (GameModes.All[0].Mode, GameModes.All[0].GameType, GameModes.All[0].GameMode);
    }

    static void Chat(List<string> chat, string line)
    {
        var c = ChatRx().Match(line);
        if (c.Success)
            chat.Add(Text.Cut((c.Groups[1].Success && c.Groups[1].Value.Length > 0 ? c.Groups[1].Value : c.Groups[2].Value).Trim(), 200));
    }

    /// <summary>One test load. Returns a record with verdict pass | errormap | crash | freeze | rejected | kick | abort (somebody came on the
    /// game server: not counted).</summary>
    public static JsonObject Load(ITestEngine engine, string wid, IReadOnlyList<string> levelsIn, IReadOnlyList<string> tags, int hold, Func<bool> stopCheck,
                                  SyncClock clock)
    {
        var (mode, gt, gm) = ModeOf(tags);
        var rec = new JsonObject { ["id"] = wid, ["mode"] = mode, ["utc"] = SyncClock.At(clock.Now()).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) };
        var levels = levelsIn.Select(l => l.ToLowerInvariant()).ToList();
        engine.Tail?.Lines();
        var t0 = clock.Now();
        engine.Rcon($"game_type {gt}; game_mode {gm}; host_workshop_map {wid}");
        string? level = null;
        var seen = new List<string>();
        int? mounted = null;
        double? silent = null, foreignSince = null;
        var lines = new List<string>();
        var chat = new List<string>();
        var limit = 150.0;
        var loaded = false;
        while (clock.Now() - t0 < limit)
        {
            clock.Sleep(1);
            var fresh = engine.Tail?.Lines() ?? [];
            lines.AddRange(fresh);
            foreach (var line in fresh)
            {
                var d = DownloadingRx().Match(line);
                if (d.Success && d.Groups[1].Value == wid && double.TryParse(d.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                {
                    // the engine's own Steam client fetches a newer copy than SteamCMD's: counted in the day's downloads
                    var size = amount * (d.Groups[3].Value switch { "KB" => 1 << 10, "MB" => 1 << 20, _ => 1 << 30 });
                    rec["engine_download"] = Math.Max((long)Math.Truncate(size), Node.Int(rec["engine_download"]));
                    limit = 300;
                }
                if (mounted is null && line.Contains($"Mounting addon '{wid}'", StringComparison.Ordinal))
                    mounted = seen.Count;
                var m = LevelRx().Match(line);
                if (m.Success)
                {
                    seen.Add(m.Groups[1].Value);
                    if (level is null && (levels.Contains(m.Groups[1].Value.ToLowerInvariant()) || m.Groups[1].Value == "error"))
                        level = m.Groups[1].Value;
                }
                Chat(chat, line);
            }
            rec["levels_seen"] = Node.Strings(seen.Take(6));
            if (level == "error" || fresh.Any(l => l.Contains("Error map!", StringComparison.Ordinal)))
                return Done(rec, "errormap", "the engine loaded its error map: nothing in the item it can load");
            if (!engine.Alive())
                return Done(rec, "crash", "the test engine went down while loading it");
            var info = engine.A2s();
            if (info is null)
            {
                silent ??= clock.Now();
                if (clock.Now() - silent > 45)
                    return Done(rec, "freeze", "A2S silent for 45 s while loading it");
                continue;
            }
            silent = null;
            if (stopCheck())
                return Done(rec, "abort", "somebody came on the game server");
            if (level is not null && A2sIs(info.Map, level))
            {
                rec["load_s"] = Math.Round(clock.Now() - t0, 1);
                rec["level"] = level;
                loaded = true;
                break;
            }
            var other = mounted is { } at ? seen.Skip(at).Where(x => !levels.Contains(x.ToLowerInvariant()) && x != "error").ToList() : [];
            if (other.Count > 0 && level is null && A2sIs(info.Map, other[^1]))
            {
                foreignSince ??= clock.Now();
                if (clock.Now() - foreignSince >= 15)
                    return Done(rec, "rejected", $"the addon mounts, but the engine loads {other[^1]}, not a map of its own ({LevelsSaid(levels)})", other[^1]);
            }
            else
                foreignSince = null;
        }
        if (!loaded)
            return Done(rec, "rejected", $"no level of its own ({LevelsSaid(levels)}) within {(long)limit} s; levels seen {(seen.Count > 0 ? string.Join(", ", seen.Take(4)) : "none")}");
        engine.Rcon("bot_join_after_player 0; bot_quota_mode normal; bot_quota 2");
        var bots = new List<int>();
        var end = clock.Now() + hold;
        while (clock.Now() < end)
        {
            clock.Sleep(2);
            var fresh = engine.Tail?.Lines() ?? [];
            lines.AddRange(fresh);
            foreach (var line in fresh)
                Chat(chat, line);
            if (!engine.Alive())
                return Done(rec, "crash", $"the test engine went down {(long)(hold - (end - clock.Now()))} s into the map");
            var info = engine.A2s();
            if (info is null)
            {
                silent ??= clock.Now();
                if (clock.Now() - silent > 45)
                    return Done(rec, "freeze", "A2S silent for 45 s on the map");
                continue;
            }
            silent = null;
            bots.Add(info.Bots);
            if (stopCheck())
                return Done(rec, "abort", "somebody came on the game server");
        }
        engine.Rcon("bot_quota 0");
        var kicks = lines.Where(l => KickRx().IsMatch(l) && !NoiseRx().IsMatch(l)).Select(l => Text.Cut(l.Trim(), 160)).ToList();
        rec["bots"] = bots.Count > 0 ? bots[^1] : 0;
        rec["chat"] = Node.Strings(chat.Take(8));
        if (kicks.Count > 0 && bots.Count > 0 && bots.Max() > 0 && bots[^1] == 0)
            return Done(rec, "kick", $"the bots were thrown off the server ({Text.Cut(kicks[0], 100)})");
        return Done(rec, "pass", $"loads as {Node.Str(rec["level"])} in {Text.Fixed(Node.Float(rec["load_s"]), 0)} s and held {hold} s (bots {Node.Str(rec["bots"])})");
    }

    static string LevelsSaid(List<string> levels) => levels.Count > 0 ? string.Join(", ", levels.Take(3)) : "none in its VPK";

    static JsonObject Done(JsonObject rec, string verdict, string reason, string? level = null)
    {
        rec["verdict"] = verdict;
        if (level is not null)
            rec["level"] = level;
        rec["reason"] = reason;
        return rec;
    }
}
