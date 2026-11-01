// Random picks (the owner's rules of 2026-10-22): which list a random pick comes from, the maps that are too big, and the reset of the
// server-wide settings once nobody is on. Rules for players: README.md "Random maps"; in the game, the how-it-works words (Hud.How).
//
//  - New maps are list numbers FirstNewRank (1017) and up: the Arms Race maps added on 2026-10-22 and every later addition; the original
//    list is #1-#1016. The "new maps in random picks" setting - server-wide, anybody may switch it in the menu, like the grenade drops -
//    says where a random pick comes from: always (the default) the new maps, half a fair coin per roll, never the original list. A list
//    with no eligible map hands the roll to the other one, so the dice never stop rolling for want of a list.
//  - Too big: a map whose class in sizes.json (beside maps.json, written by the size tool) is XL, or that at least one player marked too
//    big - a special downvote, TooBigVote in votes.json, which counts as that player's dislike everywhere a vote counts. A random pick comes
//    from the too-big maps of its list TooBigChance (1%) of the time, otherwise from the rest; an empty group hands the roll to the other.
//    Within the group the weights and the exclusions are what they always were (Votes.cs).
//  - Once no human has been connected for more than ResetEmptyMinutes (10), the new-maps setting and the grenade drops go back to their
//    defaults: always, and on (GunGameNades' server command gg_nades on, over RCON) - and the fun page's changes are undone (gg_fun reset).
//  - A map a player marked as showing ads (AdsVote, the owner's 15b of 2026-10-24) is never a random pick; anybody may still pick it.
//
// A random pick is any map the dice choose (RandomPick: the start roll, a match end, Next, a map that went from the Workshop) - never a
// map a player picked or queued.
using System.Text.Json;

static partial class Program
{
    public const int FirstNewRank = 1017;      // list numbers from here on are the new maps; #1-#1016 are the original list
    public const double TooBigChance = 0.01;   // a random pick is a too-big map this often
    public const int ResetEmptyMinutes = 10;   // nobody connected for longer than this: the new-maps setting and the grenade drops go back to their defaults
    public const int TooBigVote = HudCatalog.TooBigVote; // a too-big vote in votes.json: a downvote that also marks the map too big
    public const int AdsVote = HudCatalog.AdsVote;       // an ads vote in votes.json: a downvote that also keeps the map out of random picks (the owner's 15b)

    /// <summary>The "new maps in random picks" setting, by the names state.json keeps it under and the menu sends (!newmaps &lt;mode&gt;).</summary>
    public const string NewMapsAlways = "always", NewMapsHalf = "half", NewMapsNever = "never", DefaultNewMaps = NewMapsAlways;
    public static readonly string[] NewMapsModes = [NewMapsAlways, NewMapsHalf, NewMapsNever];

    /// <summary>The owner's rule for the HUD update of 2026-10-22 (content v3_1_0 of item 3823993566): nobody may be kicked or kept out over
    /// the HUD while the update waits for Valve's review or a player's game has not downloaded it. While this holds, the live state carries no
    /// HUD-check proof, so the plugin's check never gets past a dry run (HudCheck.Effective) - it still shows "Click to play" and logs, and
    /// disconnects nobody. **Lifted on 2026-10-24** (the item has been shown to everybody at every check since the update):
    /// the proofs are served again, so HUD_CHECK=dryrun enforces on v3_0_0, whose first real click is on record - a player
    /// not seen clicking on it gets "Click to play" and 90 s, then the steps in chat and the disconnect (HudCheck.cs). The plugin's own
    /// interlocks stay: no disconnect while Steam does not show the item to everybody, nor while joiners are not made to download it.</summary>
    public static readonly bool HudCheckHold = false;

    /// <summary>The grenade drops' default, as GunGameNades' server command takes it (it starts with drops on, and a restart is back to on).</summary>
    public const string NadesDefaultCommand = "gg_nades on";

    /// <summary>The fun page's changes undone (MapSettingsReset's gg_fun reset, Fun.cs): each setting a player changed back to how the map
    /// had it. They last until the map changes anyway; an empty server puts them back sooner, like the other settings.</summary>
    public const string FunResetCommand = Fun.ResetCommand + " \"the server\"";

    static bool SelfTesting; // --selftest: the dice roll tens of thousands of times without a log line each

    static bool IsNew(MapInfo m) => !m.Official && m.Rank >= FirstNewRank && InArmsRace(m); // list numbers of Arms Race's maps.json only

    static string NewMapsMode => NewMapsModes.Contains(S.NewMaps) ? S.NewMaps : DefaultNewMaps; // a state.json someone edited by hand reads as the default

    /// <summary>What the setting does, in the words of the news line and the health page.</summary>
    static string NewMapsWords(string mode) => mode switch
    {
        NewMapsHalf => "half: a coin flip between the new and the original maps",
        NewMapsNever => $"never: the original #1-#{FirstNewRank - 1} only",
        _ => $"always: every random pick is a new map (#{FirstNewRank} and up)",
    };

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

    // ---- the setting: !newmaps always|half|never, from the menu's Game page ----

    /// <summary>!newmaps &lt;mode&gt;: anybody switches where random picks come from, for everybody, and the news line says who did - the way
    /// the grenade drops are switched.</summary>
    static void SetNewMaps(string by, string steamId, string arg)
    {
        var want = arg.Trim().ToLowerInvariant();
        if (!NewMapsModes.Contains(want)) { Tell(steamId, "New maps in random picks: always, half or never"); return; }
        if (want == NewMapsMode) { Tell(steamId, $"New maps in random picks are {NewMapsWords(want)} already"); return; }
        S.NewMaps = want; Save();
        Log($"selection: {by} {steamId} set new maps in random picks to {want}");
        Announce($"{by} set new maps in random picks to {NewMapsWords(want)}");
    }

    // ---- the reset: nobody on for more than ResetEmptyMinutes, and the server-wide settings go back to their defaults ----

    static DateTime? NobodySince;   // nobody connected - in game, or reloading inside the load grace - since then
    static bool ResetDone;          // this empty spell's reset has been done
    static DateTime ResetProbeAt;   // when to ask RCON status again (once a minute while it says somebody is there, or does not answer)

    /// <summary>From Tick. The log stream says who is connected; a departure's 60 s grace does not count, a reload inside its 10-minute
    /// grace does. Past ResetEmptyMinutes of nobody, RCON status has the last word - a bridge that started mid-map has not seen the joins -
    /// and only "0 humans" resets: the new-maps setting to always, the grenade drops on. Once per empty spell; anybody connecting ends it.</summary>
    static void ResetWhenEmpty(DateTime now)
    {
        var anyone = Players.Values.Any(p => p.GoneSince == null && (p.ReloadSince is not { } r || (now - r).TotalSeconds <= ReloadGraceSeconds));
        if (anyone) { NobodySince = null; ResetDone = false; return; }
        NobodySince ??= now;
        if (VoteHeld) { VoteHeld = false; Log("vote: nobody on - the vote's map is held by nobody now, the next pick replaces it"); } // VoteRound.cs
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
        var was = NewMapsMode;
        if (S.NewMaps != DefaultNewMaps) { S.NewMaps = DefaultNewMaps; Save(); }
        var nades = Send(NadesDefaultCommand);
        var fun = Send(FunResetCommand);
        Log($"reset: nobody on for over {ResetEmptyMinutes} min - new maps in random picks {was} -> {DefaultNewMaps}; {NadesDefaultCommand} -> {nades ?? "(rcon failed)"}; "
            + $"{Fun.ResetCommand} -> {fun ?? "(rcon failed)"}");
        ModeBackToDefault(now);
    }

    /// <summary>The health page's lines about random picks.</summary>
    static string SelectionHealth() =>
        $"random picks: new maps {NewMapsMode} (#{FirstNewRank} and up: {Maps.Count(IsNew)} maps), too big {Maps.Count(TooBig)} (XL {Maps.Count(m => SizeClass(m) == "XL")}, "
        + $"marked {Votes.Count(kv => kv.Value.Values.Any(x => x == TooBigVote))}) - 1 pick in {Math.Round(1 / TooBigChance)}; "
        + $"marked ads {Votes.Count(kv => kv.Value.Values.Any(x => x == AdsVote))} (never rolled); "
        + $"sizes {Sizes.Count} measured (S<={SizeBounds.S}, M<={SizeBounds.M}, L<={SizeBounds.L} m²)";
}
