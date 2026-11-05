// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>Why a map is where it is, in one word: one table for every surface - the ballot's cards and the map browser's (the first two that
/// apply, as chips), chat and the top line (the first "why" tag, in words), the website and the Sync page. The table is in precedence order:
/// the vote's state, a caution, then why the map is there. The bridge works out each map's tags (Popularity.cs) and sends their ids on the
/// catalog's entries (field 24) and the vote's cards (mvo field 11), comma-separated in this order; the vote's state - next, lead, tie, on -
/// is the screen's own to add. Shared like Hud.cs: the plugin draws them, the bridge's website and --selftest use the same table.</summary>
public static class MapTags
{
    /// <summary>One tag: its id on the wire, its chip (12 characters at most, upper case), its words in a sentence, its family, its colour.</summary>
    public sealed record Tag(string Id, string Chip, string Words, string Family, string Colour);

    public const string VoteFamily = "vote", PlayerFamily = "player", CautionFamily = "caution", UsFamily = "us", FreshFamily = "fresh",
        WorkshopFamily = "workshop", DiscoveryFamily = "discovery";

    /// <summary>Chip text is dark on every chip.</summary>
    public const string ChipText = "#10141A";

    /// <summary>The chips a card shows: the first this many tags that apply.</summary>
    public const int OnCard = 2;

    public const string Next = "next", Lead = "lead", Tie = "tie", On = "on", Picked = "picked", Big = "big", Ads = "ads", Ours = "ours", Liked = "liked",
        Upload = "upload", Updated = "updated", Trending = "trending", TopRated = "toprated", Popular = "popular", NewToUs = "newtous";

    public static readonly Tag[] All =
    [
        new(Next, "NEXT MAP", "the next map", VoteFamily, "#FFD47E"),
        new(Lead, "LEADING", "leading", VoteFamily, "#E9B44C"),
        new(Tie, "TIE", "tied", VoteFamily, "#FFB547"),
        new(On, "ON NOW", "on now", VoteFamily, "#E9B44C"),
        new(Big, "TOO BIG", "too big for few players", CautionFamily, "#F07C6B"),
        new(Ads, "ADS", "shows ads", CautionFamily, "#F07C6B"),
        new(Picked, "PICKED", "picked by a player", PlayerFamily, "#F1EDE5"),
        new(Ours, "OUR MAP", "our own map", UsFamily, "#4FD6A2"),
        new(Liked, "LIKED BY US", "liked by us", UsFamily, "#7BD88F"),
        new(Upload, "NEW UPLOAD", "a recent Workshop upload", FreshFamily, "#63D8E8"),
        new(Updated, "UPDATED", "a recent Workshop update", FreshFamily, "#A6EAF2"),
        new(Trending, "TRENDING", "trending on the Workshop", WorkshopFamily, "#CFD6DE"),
        new(TopRated, "TOP RATED", "top rated on the Workshop", WorkshopFamily, "#CFD6DE"),
        new(Popular, "POPULAR", "Workshop popular", WorkshopFamily, "#CFD6DE"),
        new(NewToUs, "NEW TO US", "new to us", DiscoveryFamily, "#B48CFF"),
    ];

    public static Tag? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>A tag that says why the map is there (not the vote's state, not a caution).</summary>
    public static bool IsWhy(Tag t) => t.Family is PlayerFamily or UsFamily or FreshFamily or WorkshopFamily or DiscoveryFamily;

    static int Place(string id) => Array.FindIndex(All, t => t.Id == id);

    /// <summary>Ids as the wire carries them: known ones only, each once, in precedence order, comma-separated.</summary>
    public static string Join(IEnumerable<string> ids) => string.Join(',', ids.Where(i => Find(i) != null).Distinct().OrderBy(Place));

    /// <summary>A wire value made safe: what Join would make of it (an unknown id or a stray character is dropped).</summary>
    public static string Clean(string? wire) => Join(Split(wire));

    /// <summary>The ids of a wire value, in its order.</summary>
    public static List<string> Split(string? wire) => (wire ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The first tag that says why the map is there, or null. Without <paramref name="picked"/> a player's pick is no reason: the line
    /// that names the player says it already.</summary>
    public static Tag? FirstWhy(string? wire, bool picked = true) =>
        Split(wire).Select(Find).FirstOrDefault(t => t != null && IsWhy(t) && (picked || t.Id != Picked));

    /// <summary>The first "why" tag in words, for chat and the top line: "liked by us", or "" when there is none.</summary>
    public static string WhyWords(string? wire, bool picked = true) => FirstWhy(wire, picked)?.Words ?? "";

    /// <summary>The chips a card shows: the first OnCard tags of the wire value.</summary>
    public static List<Tag> Chips(string? wire) => Split(wire).Select(Find).OfType<Tag>().Take(OnCard).ToList();
}
