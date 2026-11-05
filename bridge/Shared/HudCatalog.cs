// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text;

/// <summary>Every map the menu can show - the ranked list of maps.json, the Deathmatch and Wingman lists and every mode's official maps - with what the server knows about each:
/// its size, the broken-list verdict, the live crash counter, the votes and its tags. A text codec like HudState's. The bridge bumps Version
/// whenever any of it changes (a vote, a crash); the live state carries the version, so the plugin fetches this again only then.</summary>
public sealed class HudCatalog
{
    public const string Magic = "gg-catalog\t1";

    /// <summary>Key: what the bridge counts votes and crashes by (an official map's name, otherwise the Workshop id). Arg: what a chat
    /// command takes to name it unambiguously (the rank of a ranked map, an official map's name, the id of an unlisted one).
    /// Broken: crashes on the swept broken list (0 = listed without a count), -1 when not on it. Removed: gone from the Workshop
    /// (Workshop.cs) - nobody can download it, so it is refused rather than confirmed. RankAr, RankDm, RankWm: the map's place in that mode's
    /// list, 0 for an official map of that mode, -1 when that mode does not have it; Rank is the place in the first mode that has it.
    /// Tags: its tags' ids in precedence order, comma-separated (MapTags) - field 24; an older plugin reads up to field 23.</summary>
    public sealed record Entry(string Key, string Arg, int Rank, long Id, string Name, int SizeMb, bool Official, int Broken, int Crashes, int Up, int Down,
        bool Removed = false, bool TooBig = false, string SizeClass = "", int M2 = 0, int Big = 0, int Ads = 0, bool Ours = false, string Reason = "",
        int? RankAr = null, int RankDm = -1, int RankWm = -1, string Tags = "")
    {
        public bool Risky => !Removed && (Broken >= 0 || Crashes > 0);

        /// <summary>The map's place in a mode's list: 0 for an official map of that mode, -1 when that mode does not have it. An entry made without its
        /// places is Arms Race's, at its Rank.</summary>
        public int RankIn(string mode) => mode switch { Modes.ArmsRaceId => RankAr ?? Rank, Modes.DeathmatchId => RankDm, Modes.WingmanId => RankWm, _ => -1 };

        /// <summary>The first mode that has the map, in menu order: what a click plays it in unless the filter or the server's mode says otherwise.</summary>
        public string FirstMode => RankIn(Modes.ArmsRaceId) >= 0 ? Modes.ArmsRaceId : RankDm >= 0 ? Modes.DeathmatchId : Modes.WingmanId;
    }

    Dictionary<string, Entry>? byKey;

    /// <summary>The entry of a map key, or null. Built once per catalog: the vote's cards ask for theirs every frame.</summary>
    public Entry? Find(string key)
    {
        byKey ??= Maps.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        return byKey.GetValueOrDefault(key);
    }

    /// <summary>A too-big vote, as votes.json, the catalog's "v" lines and the live state's "vote" lines carry it: a downvote (an older
    /// plugin reads it as -1, a dislike) that also marks the map too big. TooBig: measured XL, or marked too big by at least one player;
    /// SizeClass and M2 the measurement (sizes.json); Big the too-big votes on it.</summary>
    public const int TooBigVote = -2;

    /// <summary>An ads vote, carried like a too-big vote: a downvote - an older plugin, taking the sign, reads
    /// it as a dislike - that also marks the map as one that shows ads or sends players to another server. A map with one is never a
    /// random pick (players can still pick it). Ads on an entry: the ads votes on it.</summary>
    public const int AdsVote = -3;

    /// <summary>The other reasons a downvote can give (DownReasons), carried the same way: each a dislike and
    /// nothing more, kept per vote.</summary>
    public const int SmallVote = -4, BrokenVote = -5, SlowVote = -6;

    /// <summary>A vote value as the codecs carry it: like, dislike, or one of the special downvotes; anything else by its sign.</summary>
    public static int Carried(long value) => value is >= SlowVote and <= TooBigVote ? (int)value : Math.Sign(value);

    public long Version;
    public List<Entry> Maps = new();
    public Dictionary<long, Dictionary<string, int>> Votes = new(); // account -> map key -> +1 / -1

    /// <summary>Workshop ids the server refuses outright that the map list does not show - the non-maps, the blocked maps and unlisted
    /// maps gone from the Workshop: id -> its name ("" when unknown) and why, in the card's words ("isn't a playable map - ..."). A typed
    /// or pasted one gets its refusal on the card at once (WorkshopCard.Refuse): no Workshop question, nothing downloaded.</summary>
    public Dictionary<long, (string Name, string Why)> Refused = new();

    public int MyVote(long account, string key) => Votes.TryGetValue(account, out var mine) ? mine.GetValueOrDefault(key) : 0;

    public string Encode()
    {
        var s = new StringBuilder(Magic).Append('\n');
        s.Append("ver\t").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var m in Maps)
            s.Append(string.Join('\t', "m", Hud.Clean(m.Key, 40), Hud.Clean(m.Arg, 40), I(m.Rank), I(m.Id), Hud.Clean(m.Name, 64), I(m.SizeMb), m.Official ? "1" : "0",
                I(m.Broken), I(m.Crashes), I(m.Up), I(m.Down), m.Removed ? "1" : "0", m.TooBig ? "1" : "0", Hud.Clean(m.SizeClass, 2), I(m.M2), I(m.Big), I(m.Ads), m.Ours ? "1" : "0", Hud.Clean(m.Reason, 64), I(m.RankIn(Modes.ArmsRaceId)), I(m.RankDm), I(m.RankWm),
                MapTags.Clean(m.Tags))).Append('\n'); // an older plugin reads the first twelve (3.1.0: seventeen, 3.2: eighteen, 4.3: twenty, 4.5: twenty-three)
        foreach (var (account, mine) in Votes)
            foreach (var (key, value) in mine)
                s.Append(string.Join('\t', "v", I(account), Hud.Clean(key, 40), I(Carried(value)))).Append('\n'); // an older plugin takes the sign (3.1.0 keeps -2 and takes the sign of -3)
        foreach (var (id, (name, why)) in Refused)
            s.Append(string.Join('\t', "n", I(id), Hud.Clean(name, 60), Hud.Clean(why, 100))).Append('\n'); // an older plugin skips these
        return s.ToString();
    }

    static string I(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The catalog out of Encode's text, or null when it is not one. Unknown lines are skipped, malformed ones dropped.</summary>
    public static HudCatalog? Decode(string? text)
    {
        if (text == null || !text.StartsWith(Magic)) return null;
        var c = new HudCatalog();
        static long L(string[] f, int i) => i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            switch (f[0])
            {
                case "ver": c.Version = L(f, 1); break;
                case "m" when f.Length >= 12 && f[1] != "":
                    c.Maps.Add(new Entry(f[1], f[2], (int)L(f, 3), L(f, 4), f[5], (int)L(f, 6), f[7] == "1", (int)L(f, 8), (int)L(f, 9), (int)L(f, 10), (int)L(f, 11),
                        f.Length > 12 && f[12] == "1", f.Length > 13 && f[13] == "1", f.Length > 14 ? f[14] : "", (int)L(f, 15), (int)L(f, 16), (int)L(f, 17),
                        f.Length > 18 && f[18] == "1", f.Length > 19 ? f[19] : "", f.Length > 22 ? (int)L(f, 20) : (int)L(f, 3), f.Length > 22 ? (int)L(f, 21) : -1, f.Length > 22 ? (int)L(f, 22) : -1,
                        f.Length > 23 ? MapTags.Clean(f[23]) : ""));
                    break;
                case "v" when f.Length >= 4 && L(f, 1) > 0 && f[2] != "" && L(f, 3) != 0:
                    var account = L(f, 1);
                    if (!c.Votes.TryGetValue(account, out var mine)) c.Votes[account] = mine = new();
                    mine[f[2]] = Carried(L(f, 3));
                    break;
                case "n" when f.Length >= 4 && L(f, 1) > 0 && f[3] != "":
                    c.Refused[L(f, 1)] = (f[2], f[3]);
                    break;
            }
        }
        return c;
    }
}
