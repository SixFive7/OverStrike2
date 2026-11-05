// Random picks: the maps that are too big, the ones never rolled, and the reset of the server-wide settings once nobody is on. Rules for players:
// README.md "Random maps"; in the game, the how-it-works words (Hud.How).
//
//  - Too big: a map whose class in sizes.json (beside maps.json, written by the size tool) is XL, or that at least one player marked too
//    big - a special downvote, TooBigVote in votes.json, which counts as that player's dislike everywhere a vote counts. A random pick comes
//    from the too-big maps of its list TooBigChance (1%) of the time, otherwise from the rest; an empty group hands the roll to the other.
//    Within the group the points and the exclusions decide (Votes.cs).
//  - Once no human has been connected for more than ResetEmptyMinutes (10), the Fun page's changes are undone - the grenade drops and catch-up
//    among them, ordinary fun settings - with gg_fun reset over RCON, and the server goes back to Arms Race (ModeSwitch.cs).
//  - A map a player marked as showing ads (AdsVote) is never a random pick; anybody may still pick it.
//  - The new-maps setting is gone: every ballot has a fresh map and a surprise new to us (VoteRound.cs). The Game page's three buttons of a
//    content that still has them answer that (NewMapsGone).
//
// A random pick is any map the dice choose (the vote's random cards and its Random, and the automatic changes: the start roll, the way back
// to Arms Race, a map gone from the Workshop or crashing the players' games) - never a map a player selected. It takes a map on the server's
// disk (RandomQueue.cs: RandomNext), so it loads at once; the rules are these.
using System.Text.Json;

static partial class Program
{
    public const double TooBigChance = 0.01;   // a random pick is a too-big map this often
    public const int ResetEmptyMinutes = 10;   // nobody connected for longer than this: the fun settings go back to their defaults
    public const int TooBigVote = HudCatalog.TooBigVote; // a too-big vote in votes.json: a downvote that also marks the map too big
    public const int AdsVote = HudCatalog.AdsVote;       // an ads vote in votes.json: a downvote that also keeps the map out of random picks

    /// <summary>What the new-maps buttons and !newmaps answer: the setting is gone, and what took its place.</summary>
    public const string NewMapsGoneNote = "The new-maps setting is gone: every vote now has a fresh Workshop map and a surprise new to us";

    /// <summary>The Fun page's changes undone (the settings module's gg_fun reset, Fun.cs): every row back to its first choice - the grenade drops
    /// and catch-up too. They last until the map changes anyway; an empty server puts them back sooner, like the other settings.</summary>
    public const string FunResetCommand = Fun.ResetCommand + " \"the server\"";

    static bool SelfTesting; // --selftest: the dice roll tens of thousands of times without a log line each

    // ---- sizes.json: the size tool's verdict on each map (contract version 1, written by the size tool beside maps.json) ----

    /// <summary>One map's measurement: its walkable area (null when it could not be measured), its class (S, M, L, XL - null when
    /// unmeasured) and how it was measured (nav, walk, none).</summary>
    public sealed record SizeEntry(int? M2, string? Class, string Method);

    static Dictionary<string, SizeEntry> Sizes = new();                                      // map key -> its measurement
    static (int S, int M, int L) SizeBounds;                                                 // the classes' upper bounds in m², for the health page
    static string SizesFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(MapsFile)) ?? ".", "sizes.json");

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
                var method = p.Value.TryGetProperty("method", out var md) && md.ValueKind == JsonValueKind.String ? md.GetString() ?? "" : "";
                d[p.Name.Trim()] = new SizeEntry(m2, cls is "S" or "M" or "L" or "XL" ? cls : null, method);
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

    // ---- the new-maps setting is gone: a content that still has its buttons sends !newmaps always|half|never ----

    /// <summary>!newmaps from one of the Game page's old buttons: it changes nothing, and that player hears what took its place.</summary>
    static void NewMapsGone(string by, string steamId)
    {
        Log($"selection: {by} {steamId} clicked a new-maps button - the setting is gone");
        Tell(steamId, NewMapsGoneNote);
    }

    // ---- the reset: nobody on for more than ResetEmptyMinutes, and the server-wide settings go back to their defaults ----

    static DateTime? NobodySince;   // nobody connected - in game, or reloading inside the load grace - since then
    static bool ResetDone;          // this empty spell's reset has been done
    static DateTime ResetProbeAt;   // when to ask RCON status again (once a minute while it says somebody is there, or does not answer)

    /// <summary>From Tick. The log stream says who is present (VoteRound.cs: in game, or reloading inside its 10-minute grace). Past
    /// ResetEmptyMinutes of nobody, RCON status has the last word - a bridge that started mid-map has not seen the joins - and only "0 humans"
    /// resets: the fun settings back. Once per empty spell; anybody connecting ends it.</summary>
    static void ResetWhenEmpty(DateTime now)
    {
        if (PresentIds(now).Count > 0) { NobodySince = null; ResetDone = false; return; }
        NobodySince ??= now;
        if (ResetDone) { ModeBackToDefault(now); return; } // ModeSwitch.cs: back to Arms Race too, once nothing is in the way
        if ((now - NobodySince.Value).TotalMinutes <= ResetEmptyMinutes || now < ResetProbeAt) return;
        ResetProbeAt = now.AddMinutes(1);
        if (Send("status") is not { } status) { Log("reset: RCON not answering - asking again in a minute"); return; }
        if (HumansRx().Match(status) is { Success: true } h && int.Parse(h.Groups[1].Value) > 0)
        {
            Log($"reset: the log stream saw nobody for {ResetEmptyMinutes} min, but RCON status says {h.Groups[1].Value} human(s) - no reset");
            return;
        }
        ResetDone = true;
        var fun = Send(FunResetCommand);
        Log($"reset: nobody on for over {ResetEmptyMinutes} min - {Fun.ResetCommand} -> {fun ?? "(rcon failed)"}");
        ModeBackToDefault(now);
    }

    /// <summary>The health page's lines about random picks.</summary>
    static string SelectionHealth() =>
        $"random picks: too big {Maps.Count(TooBig)} (XL {Maps.Count(m => SizeClass(m) == "XL")}, "
        + $"marked {Votes.Count(kv => kv.Value.Values.Any(x => x == TooBigVote))}) - 1 pick in {Math.Round(1 / TooBigChance)}; "
        + $"marked ads {Votes.Count(kv => kv.Value.Values.Any(x => x == AdsVote))} (never rolled); "
        + $"sizes {Sizes.Count} measured (S<={SizeBounds.S}, M<={SizeBounds.M}, L<={SizeBounds.L} m²)";
}
