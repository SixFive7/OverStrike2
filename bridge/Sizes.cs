// Random picks: the maps that are too big and the ones never rolled. Rules for players: docs/reference.md "Random picks"; in the game, the
// how-it-works words (Hud.How).
//
//  - Too big: a map whose class in sizes.json (beside maps.json, written by the size tool) is XL, or that at least one player marked too
//    big - a special downvote, TooBigVote in votes.json, which counts as that player's dislike everywhere a vote counts. A random pick comes
//    from the too-big maps of its list TooBigChance (1%) of the time, otherwise from the rest; an empty group hands the roll to the other.
//    Within the group the points and the exclusions decide (Dice.cs).
//  - A map a player marked as showing ads (AdsVote) is never a random pick; anybody may still pick it.
//
// A random pick is any map the dice choose (the vote's random cards and its Random, and the automatic changes: the start roll, the way back
// to Arms Race, a map gone from the Workshop or crashing the players' games) - never a map a player selected. It takes a map on the server's
// disk (RandomQueue.cs: RandomNext), so it loads at once; the rules are these.
using System.Text.Json;

static partial class Program
{
    public const double TooBigChance = 0.01;   // a random pick is a too-big map this often
    public const int TooBigVote = HudCatalog.TooBigVote; // a too-big vote in votes.json: a downvote that also marks the map too big
    public const int AdsVote = HudCatalog.AdsVote;       // an ads vote in votes.json: a downvote that also keeps the map out of random picks

    // ---- sizes.json: the size tool's verdict on each map (contract version 1, written by the size tool beside maps.json) ----

    /// <summary>One map's measurement: its walkable area (null when it could not be measured) and its class (S, M, L, XL - null when
    /// unmeasured).</summary>
    public sealed record SizeEntry(int? M2, string? Class);

    static Dictionary<string, SizeEntry> Sizes = new();                                      // map key -> its measurement
    static (int S, int M, int L) SizeBounds;                                                 // the classes' upper bounds in m², for the health page
    static string SizesFile => Path.Combine(DataDir, "sizes.json");

    /// <summary>sizes.json read: version 1, "maps" an object of map key (a Workshop id, or an official map's name) -> {"m2", "class",
    /// "method"}. A class other than S, M, L or XL reads as unmeasured; an entry that is not an object is skipped. Another version, or a
    /// file that is not an object, throws to the caller - which then treats nothing as too big.</summary>
    static (Dictionary<string, SizeEntry> Maps, (int S, int M, int L) Bounds) ParseSizes(string json)
    {
        var d = new Dictionary<string, SizeEntry>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not an object");
        if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("not version 1 of the sizes contract");
        static int Bound(JsonElement b, string name) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number
            && x.TryGetDouble(out var n) ? (int)Math.Round(n) : 0;
        var bounds = root.TryGetProperty("boundaries", out var bd) ? (Bound(bd, "S"), Bound(bd, "M"), Bound(bd, "L")) : (0, 0, 0);
        if (root.TryGetProperty("maps", out var maps) && maps.ValueKind == JsonValueKind.Object)
            foreach (var p in maps.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object || p.Name.Trim() == "") continue;
                int? m2 = p.Value.TryGetProperty("m2", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetDouble(out var n) ? (int)Math.Round(n) : null;
                var cls = p.Value.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                d[p.Name.Trim()] = new SizeEntry(m2, cls is "S" or "M" or "L" or "XL" ? cls : null);
            }
        return (d, bounds);
    }

    /// <summary>sizes.json again (at start, and whenever it changes: Reload.cs). Missing or unreadable means no map is too big by its size;
    /// the too-big votes still count.</summary>
    static string ReloadSizes(string path)
    {
        CatalogVersion++; // the menu's cards carry each map's class
        if (!File.Exists(path)) { (Sizes, SizeBounds) = (new(), (0, 0, 0)); return $"sizes: {path} not found - no map is too big by its size"; }
        try { (Sizes, SizeBounds) = ParseSizes(File.ReadAllText(path)); }
        catch (Exception e) { (Sizes, SizeBounds) = (new(), (0, 0, 0)); return $"sizes: {path} unreadable ({Clip(e.Message, 120)}) - no map is too big by its size"; }
        return $"sizes: {Sizes.Count} maps from {path}, {Sizes.Values.Count(x => x.Class == "XL")} too big (XL above {SizeBounds.L} m²), {Sizes.Values.Count(x => x.Class == null)} unmeasured";
    }

    static string? SizeClass(MapInfo m) => Sizes.TryGetValue(Key(m), out var e) ? e.Class : null;
    static int? AreaOf(MapInfo m) => Sizes.TryGetValue(Key(m), out var e) ? e.M2 : null;

    /// <summary>How many players marked this map too big.</summary>
    static int TooBigVotes(string key) => Votes.TryGetValue(key, out var v) ? v.Values.Count(x => x == TooBigVote) : 0;

    /// <summary>How many players marked this map as showing ads or sending players away: one is enough to keep it out of random picks.</summary>
    static int AdsVotes(string key) => Votes.TryGetValue(key, out var v) ? v.Values.Count(x => x == AdsVote) : 0;

    /// <summary>Too big: measured XL, or marked too big by at least one player. An unmeasured map is not too big.</summary>
    static bool TooBig(MapInfo m) => SizeClass(m) == "XL" || TooBigVotes(Key(m)) > 0;

    /// <summary>The health page's lines about random picks.</summary>
    static string SelectionHealth() =>
        $"random picks: too big {Maps.Count(TooBig)} (XL {Maps.Count(m => SizeClass(m) == "XL")}, "
        + $"marked {Votes.Count(kv => kv.Value.Values.Any(x => x == TooBigVote))}) - 1 pick in {Math.Round(1 / TooBigChance)}; "
        + $"marked ads {Votes.Count(kv => kv.Value.Values.Any(x => x == AdsVote))} (never rolled); "
        + $"sizes {Sizes.Count} measured (S<={SizeBounds.S}, M<={SizeBounds.M}, L<={SizeBounds.L} m²)";
}
