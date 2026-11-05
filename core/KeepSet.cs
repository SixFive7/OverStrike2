using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverStrike2.Core;

/// <summary>The keep set's limits. The defaults are the rules; a test may use smaller ones.</summary>
public sealed record KeepRules
{
    public int Top { get; init; } = 100;                         // per mode and list: the best this many playable maps stay
    public double RatingsMaxAge { get; init; } = 3 * 86400;      // ratings older than this (the sync fetches them daily): nothing deleted
    public double RatedShare { get; init; } = 0.9;               // the ratings must score this share of each mode's playable maps
    public int MinTop { get; init; } = 50;                       // a mode's list shorter than this, with more playable maps listed: refused
    public int MinQueue { get; init; } = 10;                     // each random queue must hold this many entries (the bridge keeps 20)
    public static readonly KeepRules Default = new();
}

/// <summary>What the keep set holds, and why it is not to be trusted with a deletion (Problems).</summary>
public sealed class KeepSetResult
{
    public List<string> Up { get; init; } = [];
    public Dictionary<string, List<string>> Top { get; init; } = [];
    public Dictionary<string, (List<string> Rated, List<string> Subscribed, List<string> Popular)> Lists { get; init; } = [];
    public Dictionary<string, (double? Score, long? Subscriptions)> Cutoff { get; init; } = [];
    public List<string> Own { get; init; } = [];
    public List<string> Hud { get; init; } = [];
    public Dictionary<string, List<string>> Queues { get; init; } = [];
    public List<string> Requested { get; init; } = [];
    public HashSet<string> All { get; init; } = [];
    public int Scored { get; init; }
    public List<string> Problems { get; init; } = [];
}

/// <summary>THE KEEP SET: the Workshop maps the game server stores (official maps are part of the game). Its parts:
///   up       every map with at least one player's up vote in votes.json - one vote per player and map, up or down, the opposite
///            replaces it; too-big and ads votes are down votes - however many down votes it has besides;
///   top      per mode, the union of three lists of its Top playable maps (on that mode's list; not broken: broken.json's lists,
///            blocked.json, removed.json's verdicts, the bridge's crash counts), all from ratings.json, which the sync fetches daily:
///            "rated", the best Workshop score (vote_data.score, the Workshop's Top Rated order); "subscribed", the most current
///            subscriptions (Most Subscribed); "popular", Steam's trend order over the last 30 days (Most Popular). Ties by list order;
///   own, hud ours.json's maps and the HUD item;
///   queues   the bridge's random queues and the maps players have queued (queues.json).
/// Problems: why the set is not to be trusted with a deletion - an input missing or unreadable, ratings older than RatingsMaxAge,
/// scores or subscriptions for under RatedShare of a list, no Most Popular order for a mode, any of a mode's three lists under MinTop,
/// a queue under MinQueue, no own map, no HUD item. Pure: the same inputs give the same set. The game server deletes what is outside
/// it; the sync hands over what is in it.</summary>
public static class KeepSet
{
    public static readonly (string Mode, string File)[] Modes = [("armsrace", "maps.json"), ("deathmatch", "maps-deathmatch.json"), ("wingman", "maps-wingman.json")];
    public static readonly string[] QueueNames = ["armsrace-new", "armsrace-original", "deathmatch", "wingman"];
    public static readonly string[] InputFiles = ["maps.json", "maps-deathmatch.json", "maps-wingman.json", "broken.json", "blocked.json", "removed.json",
                                                  "votes.json", "ours.json", "ratings.json", "queues.json", "state.json"];

    /// <summary>The keep set's inputs: the bridge's data files by name, each null when it is missing or does not parse.</summary>
    public static Dictionary<string, JsonNode?> Inputs(string dataDir) =>
        InputFiles.ToDictionary(n => n, n => Files.ReadJson(Path.Combine(dataDir, n)));

    /// <summary>A keep set that holds nothing and says why: what a caller acts on when the set cannot be computed (nothing deleted,
    /// nothing handed over). Its one problem starts "the keep set could not be computed".</summary>
    public static KeepSetResult Empty(string why) => new() { Problems = [$"the keep set could not be computed: {why}"] };

    /// <summary><see cref="Compute"/>, but an input of a shape it does not read is counted as a problem instead of raising: the set is
    /// then empty and says why.</summary>
    public static KeepSetResult Safe(IReadOnlyDictionary<string, JsonNode?> inputs, DateTimeOffset now, string hud = "", Action<string>? log = null,
                                     KeepRules? rules = null)
    {
        try
        {
            return Compute(inputs, now, hud, rules);
        }
        catch (Exception e)   // a reshaped bridge file must never crash a run or an engine start
        {
            log?.Invoke($"keep set: not computed ({e.GetType().Name}: {e.Message}) - an input has a shape this code does not read; nothing is decided on it");
            return Empty($"{e.GetType().Name}: {e.Message}");
        }
    }

    public static KeepSetResult Compute(IReadOnlyDictionary<string, JsonNode?> inputs, DateTimeOffset now, string hud = "", KeepRules? rules = null)
    {
        rules ??= KeepRules.Default;
        var problems = new List<string>();

        JsonObject NeedObject(string name)
        {
            if (inputs.TryGetValue(name, out var v) && v is JsonObject o)
                return o;
            problems.Add($"{name} is missing or unreadable");
            return [];
        }

        JsonArray NeedList(string name)
        {
            if (inputs.TryGetValue(name, out var v) && v is JsonArray a)
                return a;
            problems.Add($"{name} is missing or unreadable");
            return [];
        }

        var lists = new Dictionary<string, List<string>>();
        foreach (var (mode, file) in Modes)
            lists[mode] = NeedList(file).OfType<JsonObject>().Select(e => Digits(e["id"])).OfType<string>().Distinct().ToList();
        if (lists["armsrace"].Count == 0 && !problems.Contains("maps.json is missing or unreadable"))
            problems.Add("maps.json lists no map");

        var bad = new HashSet<string>();
        var broken = NeedObject("broken.json");
        foreach (var section in new[] { "maps", "packs", "removed", "blocked" })
            foreach (var entry in broken[section] as JsonArray ?? [])
                if (Digits(entry is JsonObject o ? o["id"] : entry) is { } wid)
                    bad.Add(wid);
        var blocked = inputs.TryGetValue("blocked.json", out var bl) ? bl : null;   // optional: without it, more stays
        var blockedEntries = blocked is JsonObject bo ? bo["blocked"] : blocked;
        foreach (var entry in blockedEntries as JsonArray ?? [])
            if (Digits(entry is JsonObject o ? o["id"] : entry) is { } wid)
                bad.Add(wid);
        foreach (var (wid, verdict) in NeedObject("removed.json")["verdicts"] as JsonObject ?? [])
            if (PyStr(verdict) != "ok")
                bad.Add(wid);
        var bridge = NeedObject("state.json");
        foreach (var (wid, count) in bridge["crashes"] as JsonObject ?? [])
            if (Integer(count) is > 0)
                bad.Add(wid);
        foreach (var (wid, _) in bridge["playerCrashes"] as JsonObject ?? [])
            bad.Add(wid);

        var up = new HashSet<string>();
        foreach (var (key, value) in NeedObject("votes.json"))
            if (IsDigits(key) && value is JsonObject votes && votes.Any(v => Integer(v.Value) is > 0))
                up.Add(key);

        var ratings = NeedObject("ratings.json");
        var scores = new Dictionary<string, double>();
        var subs = new Dictionary<string, long>();
        if (ratings["scores"] is JsonObject scoreTable)
            foreach (var (wid, value) in scoreTable)
            {
                var score = value is JsonArray a && a.Count > 0 ? a[0] : value;
                if (IsDigits(wid) && Number(score) is { } d)
                    scores[wid] = d;
                var count = value is JsonArray b && b.Count > 3 ? b[3] : null;
                if (IsDigits(wid) && Integer(count) is { } n)
                    subs[wid] = n;
            }
        var trend = ratings["trend"] as JsonObject ?? [];
        if (ratings.Count > 0)
        {
            var fetched = Number(ratings["fetched"]);
            double? age = fetched is { } f ? now.ToUnixTimeMilliseconds() / 1000.0 - f : null;
            if (age is null || age > rules.RatingsMaxAge)
                problems.Add($"ratings.json is {(age is { } a ? $"{(long)Math.Floor(a / 3600)} h" : "of no known age")} old (at most {(long)(rules.RatingsMaxAge / 3600)} h)");
        }

        var top = new Dictionary<string, List<string>>();
        var cutoff = new Dictionary<string, (double?, long?)>();
        var three = new Dictionary<string, (List<string>, List<string>, List<string>)>();
        foreach (var (mode, _) in Modes)
        {
            var playable = lists[mode].Where(w => !bad.Contains(w)).ToList();
            var place = new Dictionary<string, int>();
            for (var i = 0; i < playable.Count; i++)
                place[playable[i]] = i;
            var rated = playable.Where(scores.ContainsKey).ToList();
            var counted = playable.Where(subs.ContainsKey).ToList();
            foreach (var (what, have) in new[] { ("scores", rated), ("subscriptions", counted) })
                if (playable.Count > 0 && have.Count < rules.RatedShare * playable.Count)
                    problems.Add($"ratings.json has the {what} of {have.Count} of the {playable.Count} playable {mode} maps ({Math.Round(rules.RatedShare * 100, MidpointRounding.ToEven)}% needed)");
            var order = trend[mode] as JsonArray;
            if (order is null)
                problems.Add($"ratings.json has no Most Popular order for {mode}");
            var bestRated = rated.OrderBy(w => -scores[w]).ThenBy(w => place[w]).Take(rules.Top).ToList();
            var bestSubscribed = counted.OrderBy(w => -subs[w]).ThenBy(w => place[w]).Take(rules.Top).ToList();
            var bestPopular = (order ?? []).Select(PyStr).Distinct().Where(place.ContainsKey).Take(rules.Top).ToList();
            three[mode] = (bestRated, bestSubscribed, bestPopular);
            top[mode] = bestRated.Concat(bestSubscribed).Concat(bestPopular).Distinct().ToList();
            cutoff[mode] = (bestRated.Count >= rules.Top ? scores[bestRated[^1]] : null, bestSubscribed.Count >= rules.Top ? subs[bestSubscribed[^1]] : null);
            foreach (var (what, ids) in new[] { ("rated", bestRated), ("subscribed", bestSubscribed), ("popular", bestPopular) })
                if (ids.Count < Math.Min(rules.MinTop, playable.Count))
                    problems.Add($"the {mode} {what} list holds {ids.Count} maps ({playable.Count} playable listed)");
        }

        var own = new HashSet<string>((NeedObject("ours.json")["maps"] as JsonArray ?? []).OfType<JsonObject>().Select(e => Digits(e["id"])).OfType<string>());
        if (own.Count == 0)
            problems.Add("ours.json names no own map");
        var hudIds = IsDigits(hud) ? new HashSet<string> { hud } : [];
        if (hudIds.Count == 0)
            problems.Add("no HUD item is set");
        var queues = NeedObject("queues.json");
        if (queues.Count > 0 && Number(queues["version"]) != 1)
            problems.Add("queues.json is not version 1");
        var random = queues["random"] as JsonObject ?? [];
        var qs = new Dictionary<string, List<string>>();
        foreach (var name in QueueNames)
        {
            var entries = random[name] is JsonArray q ? q.Select(PyStr).ToList() : [];
            if (entries.Count < rules.MinQueue)
                problems.Add($"the {name} queue holds {entries.Count} entries (at least {rules.MinQueue})");
            qs[name] = entries.Where(IsDigits).ToList();
        }
        var requested = queues["requested"] is JsonArray req ? req.Select(Digits).OfType<string>().ToList() : [];
        var every = new HashSet<string>(up);
        every.UnionWith(own);
        every.UnionWith(hudIds);
        every.UnionWith(requested);
        foreach (var ids in top.Values.Concat(qs.Values))
            every.UnionWith(ids);
        return new KeepSetResult
        {
            Up = up.OrderBy(x => x, StringComparer.Ordinal).ToList(), Top = top,
            Lists = three.ToDictionary(kv => kv.Key, kv => kv.Value), Cutoff = cutoff.ToDictionary(kv => kv.Key, kv => kv.Value),
            Own = own.OrderBy(x => x, StringComparer.Ordinal).ToList(), Hud = hudIds.OrderBy(x => x, StringComparer.Ordinal).ToList(), Queues = qs,
            Requested = requested, All = every, Scored = scores.Count, Problems = problems,
        };
    }

    // ---- JSON as the keep set reads it: an id is a string of digits or a positive whole number

    static bool IsDigits(string? s) => !string.IsNullOrEmpty(s) && s.All(char.IsAsciiDigit);

    /// <summary>An id: a JSON string of digits, or a positive whole number; else null.</summary>
    public static string? Digits(JsonNode? v)
    {
        if (v is not JsonValue jv)
            return null;
        if (jv.GetValueKind() == JsonValueKind.String)
            return IsDigits(jv.GetValue<string>()) ? jv.GetValue<string>() : null;
        return Integer(v) is long n && n > 0 ? n.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A whole JSON number (written without a point or an exponent), else null. True and false are no numbers.</summary>
    public static long? Integer(JsonNode? v)
    {
        if (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number && jv.TryGetValue<JsonElement>(out var el))
        {
            var raw = el.GetRawText();
            if (raw.IndexOfAny(['.', 'e', 'E']) < 0 && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                return n;
            return null;
        }
        if (v is JsonValue made && made.GetValueKind() == JsonValueKind.Number)   // a value made in code, not parsed: its .NET type says
        {
            if (made.TryGetValue<int>(out var i))
                return i;
            if (made.TryGetValue<long>(out var l))
                return l;
            if (made.TryGetValue<uint>(out var u))
                return u;
        }
        return null;
    }

    /// <summary>Any JSON number, else null.</summary>
    public static double? Number(JsonNode? v)
    {
        if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number)
            return null;
        if (jv.TryGetValue<double>(out var d))
            return d;
        return Integer(v) is long n ? n : jv.TryGetValue<float>(out var f) ? f : jv.TryGetValue<decimal>(out var m) ? (double)m : null;
    }

    /// <summary>A JSON value as text: a string as it is, a number as written, true and false, null as "None".</summary>
    public static string PyStr(JsonNode? v) => v switch
    {
        null => "None",
        JsonValue jv when jv.GetValueKind() == JsonValueKind.String => jv.GetValue<string>(),
        JsonValue jv when jv.GetValueKind() == JsonValueKind.True => "True",
        JsonValue jv when jv.GetValueKind() == JsonValueKind.False => "False",
        JsonValue jv when jv.TryGetValue<JsonElement>(out var el) => el.GetRawText(),
        _ => v.ToJsonString(),
    };
}
