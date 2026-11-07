using System.Globalization;
using System.Text.Json.Nodes;
using static OverStrike2.TestKit.T;

namespace OverStrike2.Core.SelfTest;

static class KeepSetTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793700000);
    static double NowS => Now.ToUnixTimeSeconds();

    static JsonArray Maps(int from, int count, string prefix, int size) =>
        new(Enumerable.Range(1, count).Select(r => (JsonNode)new JsonObject { ["rank"] = r, ["id"] = from + r, ["name"] = prefix + r, ["sizeMb"] = size }).ToArray());

    static JsonArray Strs(IEnumerable<string> ids) => new(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());

    /// <summary>The bridge's data for the keep set: Arms Race 1001-1150, Deathmatch 2001-2120, Wingman 3001-3060, with their ratings.</summary>
    static Dictionary<string, JsonNode?> Kin(Action<Dictionary<string, JsonNode?>>? over = null)
    {
        var scores = new JsonObject();
        for (var r = 1; r <= 150; r++)                    // Arms Race: subscriptions grow with the rank
            scores[(1000 + r).ToString(CultureInfo.InvariantCulture)] = new JsonArray(1.0 - r / 1000.0, r, 0, r);
        for (var r = 1; r <= 120; r++)
            scores[(2000 + r).ToString(CultureInfo.InvariantCulture)] = new JsonArray(r / 1000.0, r, 0, 5);
        for (var r = 1; r <= 60; r++)                     // Wingman: all equal - list order breaks the ties
            scores[(3000 + r).ToString(CultureInfo.InvariantCulture)] = new JsonArray(0.5, r, 0, 5);
        var trend = new JsonObject
        {
            ["armsrace"] = Strs(new[] { "9999" }.Concat(Enumerable.Range(1, 150).Reverse().Select(r => (1000 + r).ToString(CultureInfo.InvariantCulture)))),
            ["deathmatch"] = Strs(Enumerable.Range(1, 120).Select(r => (2000 + r).ToString(CultureInfo.InvariantCulture))),
            ["wingman"] = Strs(Enumerable.Range(1, 60).Select(r => (3000 + r).ToString(CultureInfo.InvariantCulture))),
        };
        var d = new Dictionary<string, JsonNode?>
        {
            ["maps.json"] = Maps(1000, 150, "ar", 10),
            ["maps-deathmatch.json"] = Maps(2000, 120, "dm", 20),
            ["maps-wingman.json"] = Maps(3000, 60, "wm", 30),
            ["broken.json"] = JsonNode.Parse("""{"maps": [{"id": 1001}], "packs": [{"id": 1002}], "removed": [], "blocked": [{"id": 1003}]}"""),
            ["removed.json"] = JsonNode.Parse("""{"verdicts": {"1004": "removed", "1005": "ok"}}"""),
            ["votes.json"] = JsonNode.Parse("""{"1149": {"[U:1:1]": 1, "[U:1:2]": -1, "[U:1:3]": -1, "[U:1:4]": -1}, "1148": {"[U:1:1]": -2}, "1147": {"[U:1:1]": -3}, "1146": {"[U:1:1]": -1}, "ar_baggage": {"[U:1:1]": 1}}"""),
            ["ours.json"] = JsonNode.Parse("""{"maps": [{"id": 4001, "name": "own"}]}"""),
            ["ratings.json"] = new JsonObject { ["version"] = 1, ["fetched"] = NowS - 3600, ["scores"] = scores, ["trend"] = trend, ["trend_days"] = 30 },
            ["queues.json"] = new JsonObject
            {
                ["version"] = 2,
                ["ballot"] = Strs(["5001"]),
                ["stock"] = new JsonObject
                {
                    ["armsrace"] = Strs(Enumerable.Range(0, 15).Select(i => (1100 + i).ToString(CultureInfo.InvariantCulture))
                                        .Concat(Enumerable.Range(0, 15).Select(i => (1010 + i).ToString(CultureInfo.InvariantCulture)))),
                    ["deathmatch"] = Strs(Enumerable.Range(0, 14).Select(i => (2001 + i).ToString(CultureInfo.InvariantCulture)).Append("de_dust2")),
                    ["wingman"] = Strs(Enumerable.Range(0, 15).Select(i => (3001 + i).ToString(CultureInfo.InvariantCulture))),
                },
            },
            ["state.json"] = JsonNode.Parse("""{"crashes": {"1006": 2, "1007": 0}, "playerCrashes": {"1008": "crashed 3 players' games"}}"""),
        };
        over?.Invoke(d);
        return d;
    }

    static JsonObject Ratings(Dictionary<string, JsonNode?> d) => (JsonObject)d["ratings.json"]!;

    public static void Run()
    {
        var ks = KeepSet.Compute(Kin(), Now, "500");
        Check("keep: a map with 1 up vote and 3 down votes stays; a too-big (-2), an ads (-3) or a plain down vote alone does not; an official name never counts",
              ks.Up.SequenceEqual(["1149"]));
        var rated = ks.Lists["armsrace"].Rated;
        Check($"keep: Arms Race's 100 best-rated, leaving out the broken, the non-map, the blocked, the removed, the crashed and the player-crash maps ({string.Join(" ", rated.Take(6))} ...)",
              rated.Take(4).SequenceEqual(["1005", "1007", "1009", "1010"]) && rated.Count == 100
              && !new[] { "1001", "1002", "1003", "1004", "1006", "1008" }.Intersect(ks.Top["armsrace"]).Any() && ks.Cutoff["armsrace"].Score == 1.0 - 106 / 1000.0);
        Check("keep: Arms Race's 100 most subscribed and 100 most popular (Steam's order, an unlisted id skipped) are the other end of its list",
              ks.Lists["armsrace"].Subscribed.Take(2).SequenceEqual(["1150", "1149"]) && ks.Lists["armsrace"].Popular.Take(2).SequenceEqual(["1150", "1149"])
              && ks.Lists["armsrace"].Subscribed.Count == 100 && ks.Cutoff["armsrace"].Subscriptions == 51);
        Check($"keep: the three lists together - {ks.Top["armsrace"].Count} maps of Arms Race's 144 playable, each once",
              ks.Top["armsrace"].Count == 144 && ks.Top["armsrace"].Distinct().Count() == 144);
        Check("keep: Deathmatch's best 100 of 120; Wingman's 60 all stay (fewer than 100), ties in list order, no cutoff",
              ks.Lists["deathmatch"].Rated[0] == "2120" && ks.Lists["deathmatch"].Rated.Count == 100
              && ks.Lists["wingman"].Rated.SequenceEqual(Enumerable.Range(1, 60).Select(r => (3000 + r).ToString(CultureInfo.InvariantCulture)))
              && ks.Cutoff["wingman"].Score is null && ks.Top["deathmatch"].Count == 120);
        Check("keep: own maps, the HUD, the stock's lanes (official names left out) and the ballot's maps are in it; nothing is wrong",
              ks.Own.SequenceEqual(["4001"]) && ks.Hud.SequenceEqual(["500"]) && !ks.Stock["deathmatch"].Contains("de_dust2") && ks.Stock["deathmatch"].Count == 14
              && ks.Stock.Keys.SequenceEqual(KeepSet.StockLanes) && KeepSet.StockLanes.SequenceEqual(["armsrace", "deathmatch", "wingman"]) && ks.Ballot.SequenceEqual(["5001"])
              && new[] { "4001", "500", "5001", "1100", "1010", "3015" }.All(ks.All.Contains) && ks.Problems.Count == 0);
        var again = KeepSet.Compute(Kin(), Now, "500");
        Check("keep: the same inputs, the same set", again.All.SetEquals(ks.All) && again.Top["armsrace"].SequenceEqual(ks.Top["armsrace"]) && again.Problems.Count == 0);

        var cases = new (string Label, Action<Dictionary<string, JsonNode?>>? Over, string Hud, string Want)[]
        {
            ("ratings older than 3 days", d => Ratings(d)["fetched"] = NowS - 4 * 86400, "500", "ratings.json is 96 h old"),
            ("ratings of no known age", d => Ratings(d).Remove("fetched"), "500", "of no known age"),
            ("ratings scoring half a list", d =>
            {
                var s = (JsonObject)Ratings(d)["scores"]!;
                foreach (var k in s.Select(p => p.Key).Where(k => int.Parse(k, CultureInfo.InvariantCulture) is > 2000 and < 2061).ToList())
                    s.Remove(k);
            }, "500", "has the scores of 60 of the 120 playable deathmatch maps"),
            ("no votes.json", d => d["votes.json"] = null, "500", "votes.json is missing or unreadable"),
            ("no maps.json", d => d["maps.json"] = null, "500", "maps.json is missing or unreadable"),
            ("an empty maps.json", d => d["maps.json"] = new JsonArray(), "500", "maps.json lists no map"),
            ("a short lane", d => ((JsonObject)((JsonObject)d["queues.json"]!)["stock"]!)["armsrace"] = Strs(["1100"]), "500", "the armsrace stock lane holds 1 entries"),
            ("no queues.json", d => d["queues.json"] = null, "500", "queues.json is missing or unreadable"),
            ("an empty queues.json object", d => d["queues.json"] = new JsonObject(), "500", "queues.json is not version 2"),
            ("a queues.json of a version this code does not know", d => d["queues.json"]!["version"] = 3, "500", "queues.json is not version 2"),
            ("version 1, an older bridge's, under the new reader: full lanes, but not read", d => d["queues.json"] = OldQueues(15), "500", "queues.json is not version 2"),
            ("version 1 with its version raised: still none of the lanes this code reads", d => { var q = OldQueues(15); q["version"] = 2; d["queues.json"] = q; }, "500",
             "the armsrace stock lane holds 0 entries"),
            ("no own map", d => d["ours.json"] = JsonNode.Parse("""{"maps": []}"""), "500", "ours.json names no own map"),
            ("no HUD item", null, "", "no HUD item is set"),
            ("a tiny top list", d => d["ratings.json"] = new JsonObject { ["version"] = 1, ["fetched"] = NowS, ["scores"] = JsonNode.Parse("""{"1005": 1.0}"""), ["trend"] = Ratings(Kin())["trend"]!.DeepClone() },
             "500", "the armsrace rated list holds 1 maps"),
            ("no Most Popular order", d => Ratings(d).Remove("trend"), "500", "no Most Popular order for armsrace"),
            ("no subscriptions", d =>
            {
                var s = (JsonObject)Ratings(d)["scores"]!;
                foreach (var (_, v) in s)
                    ((JsonArray)v!).RemoveAt(3);
            }, "500", "has the subscriptions of 0 of"),
        };
        foreach (var (label, over, hud, want) in cases)
        {
            var got = KeepSet.Compute(Kin(over), Now, hud).Problems;
            Check($"keep: refused for {label} ({string.Join("; ", got).PadRight(1)[..Math.Min(150, Math.Max(1, string.Join("; ", got).Length))]})", got.Any(p => p.Contains(want, StringComparison.Ordinal)));
        }

        // queues.json, both ways across release 191: the file of version 1 an older bridge writes is nothing to the new keep set (above: it refuses),
        // and the new file is nothing to the old one - which wanted version 1 and the four lanes under "random", each at least MinQueue long
        // (core/KeepSet.cs before release 191). OldReaderRefuses is that reader's gate, word for word; the new file fails it twice over.
        var fresh = Kin();
        Check("keep: queues.json version 2, as the bridge writes it, is the only one read: every lane and the ballot in the set, no problem",
              KeepSet.Compute(fresh, Now, "500").Problems.Count == 0 && KeepSet.QueuesVersion == 2);
        var oldFull = OldQueues(15);
        Check("keep: version 1 with every lane full - a set an older keep set would trust - refuses under the new one, and nothing is in it for deleting",
              KeepSet.Compute(Kin(d => d["queues.json"] = oldFull), Now, "500") is { Problems.Count: > 0 } r1 && r1.Problems.Contains("queues.json is not version 2")
              && !OldReaderRefuses(oldFull));
        Check("keep: the new file under the old reader's gate: not version 1, and none of its four lanes - refused, so an older os2-server or sync deletes nothing on it",
              OldReaderRefuses(fresh["queues.json"]!.AsObject()) && OldReaderRefuses(fresh["queues.json"]!.AsObject(), minQueue: 0)
              && fresh["queues.json"]!["version"]!.GetValue<int>() != 1 && fresh["queues.json"]!["random"] is null);

        // blocked.json: the maps blocked for good, in a file of their own - out of the top lists; without the file they are in
        var kdata = KData();
        var k0 = KeepSet.Compute(kdata, Now, "500");
        var withBlocked = KData();
        withBlocked["blocked.json"] = JsonNode.Parse("""[{"id": 105}, 106]""");
        var kb = KeepSet.Compute(withBlocked, Now, "500");
        Check("keep: blocked.json's maps are left out of the top lists; without the file they are in, and neither is a problem",
              k0.Top["armsrace"].Contains("105") && k0.Top["armsrace"].Contains("106") && !kb.Top["armsrace"].Intersect(["105", "106"]).Any()
              && kb.Problems.SequenceEqual(k0.Problems));
        var asObject = KData();
        asObject["blocked.json"] = JsonNode.Parse("""{"blocked": [105]}""");
        var kb2 = KeepSet.Compute(asObject, Now, "500");
        Check("keep: blocked.json may also hold its list under \"blocked\"", !kb2.Top["armsrace"].Contains("105") && kb2.Top["armsrace"].Contains("106"));

        // the shapes it does not expect never crash it
        var odd = new Dictionary<string, JsonNode?>();
        foreach (var name in KeepSet.InputFiles)
            odd[name] = JsonValue.Create(42);
        var oddSet = KeepSet.Safe(odd, Now, "500");
        Check($"keep: every input of the wrong kind - problems, no exception ({oddSet.Problems.Count})", oddSet.Problems.Count > 0 && oddSet.All.Count == 1);
        Check("keep: the empty set says why, so nothing is deleted or handed over on it", KeepSet.Empty("x").Problems.SequenceEqual(["the keep set could not be computed: x"])
              && KeepSet.Empty("x").All.Count == 0);
        Check("keep: ids - digit strings and positive whole numbers; not 0, -5, 1.0, true or text",
              Node.Id(JsonValue.Create("123")) == "123" && Node.Id(JsonNode.Parse("123")) == "123" && Node.Id(JsonNode.Parse("0")) is null
              && Node.Id(JsonNode.Parse("-5")) is null && Node.Id(JsonNode.Parse("1.0")) is null && Node.Id(JsonNode.Parse("true")) is null
              && Node.Id(JsonValue.Create("ar_baggage")) is null && Node.Id(JsonValue.Create("0")) == "0");

        // the inputs from a data folder: each file by name, null when it is missing or does not parse
        var dir = Temp("keepdata");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "maps.json"), "[{\"id\": 1}]");
        File.WriteAllText(Path.Combine(dir, "votes.json"), "{ broken");
        var inputs = KeepSet.Inputs(dir);
        Check("keep: Inputs reads every file by name; a missing or broken one is null", inputs.Count == KeepSet.InputFiles.Length && inputs["maps.json"] is JsonArray
              && inputs["votes.json"] is null && inputs["ratings.json"] is null);
        var (topRated, topSubscribed, topPopular) = KeepSet.TopLists(["a", "b", "c", "d"], w => w switch { "a" => 1, "b" => 3, "c" => 3, _ => null },
                                                            w => w == "d" ? 5 : null, ["c", "x", "c", "a", "b"], 2);
        Check("keep: the top lists the keep set and the bridge share - by score, ties by place on the mode's list; by subscriptions; the trend order, each map once and only a listed one",
              topRated is ["b", "c"] && topSubscribed is ["d"] && topPopular is ["c", "a"]);
    }

    /// <summary>queues.json as a bridge from before release 191 writes it: version 1, the lanes under "random" by the four old names (Arms Race's twice),
    /// the open ballot's maps under "requested".</summary>
    static JsonObject OldQueues(int each) => new()
    {
        ["version"] = 1, ["length"] = each,
        ["random"] = new JsonObject
        {
            ["armsrace-new"] = Strs(Enumerable.Range(0, each).Select(i => (1100 + i).ToString(CultureInfo.InvariantCulture))),
            ["armsrace-original"] = Strs(Enumerable.Range(0, each).Select(i => (1100 + i).ToString(CultureInfo.InvariantCulture))),
            ["deathmatch"] = Strs(Enumerable.Range(0, each).Select(i => (2001 + i).ToString(CultureInfo.InvariantCulture))),
            ["wingman"] = Strs(Enumerable.Range(0, each).Select(i => (3001 + i).ToString(CultureInfo.InvariantCulture))),
        },
        ["requested"] = Strs(["5001"]),
    };

    /// <summary>The keep set's gate on queues.json before release 191, word for word: a problem unless it was version 1, and one for each of the four
    /// lanes under "random" shorter than MinQueue. True when that reader would have refused the file.</summary>
    static bool OldReaderRefuses(JsonObject queues, int minQueue = 10)
    {
        var problems = new List<string>();
        if (queues.Count > 0 && Node.Number(queues["version"]) != 1)
            problems.Add("queues.json is not version 1");
        var random = queues["random"] as JsonObject ?? [];
        foreach (var name in new[] { "armsrace-new", "armsrace-original", "deathmatch", "wingman" })
            if ((random[name] is JsonArray q ? q.Count : 0) < minQueue)
                problems.Add($"the {name} queue is short");
        return problems.Count > 0;
    }

    /// <summary>The sync's smaller fixture: Arms Race 101-229, Deathmatch 301-329.</summary>
    static Dictionary<string, JsonNode?> KData()
    {
        var scores = new JsonObject();
        for (var r = 1; r < 130; r++)
            scores[(100 + r).ToString(CultureInfo.InvariantCulture)] = new JsonArray(r / 200.0, 1, 0, 1000 - r);
        return new Dictionary<string, JsonNode?>
        {
            ["maps.json"] = new JsonArray(Enumerable.Range(1, 129).Select(r => (JsonNode)new JsonObject { ["rank"] = r, ["id"] = 100 + r, ["name"] = "a" + r, ["sizeMb"] = 1 }).ToArray()),
            ["maps-deathmatch.json"] = new JsonArray(Enumerable.Range(1, 29).Select(r => (JsonNode)new JsonObject { ["rank"] = r, ["id"] = 300 + r, ["name"] = "d" + r, ["sizeMb"] = 1 }).ToArray()),
            ["maps-wingman.json"] = new JsonArray(),
            ["broken.json"] = JsonNode.Parse("""{"maps": [{"id": 101}]}"""),
            ["removed.json"] = JsonNode.Parse("""{"verdicts": {"102": "removed"}}"""),
            ["votes.json"] = JsonNode.Parse("""{"110": {"[U:1:1]": 1, "[U:1:2]": -1}, "111": {"[U:1:1]": -2}}"""),
            ["ours.json"] = JsonNode.Parse("""{"maps": [{"id": 900}]}"""),
            ["ratings.json"] = new JsonObject
            {
                ["version"] = 1, ["fetched"] = NowS - 60, ["scores"] = scores,
                ["trend"] = new JsonObject { ["armsrace"] = Strs(Enumerable.Range(60, 70).Select(r => (100 + r).ToString(CultureInfo.InvariantCulture))), ["deathmatch"] = Strs(["310", "311", "999"]), ["wingman"] = new JsonArray() },
            },
            ["queues.json"] = JsonNode.Parse("""{"version": 2, "stock": {"armsrace": ["120"], "deathmatch": ["301"]}, "ballot": ["150"]}"""),
            ["state.json"] = JsonNode.Parse("""{"crashes": {"103": 1}}"""),
        };
    }
}
