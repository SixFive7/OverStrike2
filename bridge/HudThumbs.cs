// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;

/// <summary>How a card shows its map's picture. A custom HUD cannot take a picture's address from the server and the game refuses it web
/// pictures outright (its window is the one that may not load http or https, 47 forms tested on 2026-11-01), so every thumbnail is in the
/// Workshop addon - each map's Workshop preview as a small JPEG, which the resource compiler stores as it is, or for an official map the
/// game's own screenshot - and the card picks one with classes. Maps that are not listed yet ship theirs too (hud/thumbs.py --ahead), so
/// a map the sync lists later has its picture without a new HUD; a map uploaded after that release has the card's plain backdrop until the next one.
///
/// Two classes per picture, not one per map: the game interns every class name the server ever sets on the layout entity, for the
/// entity's whole life and for all players together, and takes no more than 1,024 (the server logs "The maximum number of class names
/// has been reached" and what the client then does is unknown). Picture n is the pair th{n / 64} and tl{n % 64}, so 9,000 pictures
/// cost about 200 names. n is the map's place in HudThumbList (hud/thumbs.py appends, nothing moves); gungame_hud_thumbs.css has the rule
/// ".th{n / 64}.tl{n % 64}" for each, and a map without a number shows the card's plain backdrop.</summary>
public static class HudThumbs
{
    public const int Base = 64;

    /// <summary>th0 ... th255: 16,384 pictures (hud/thumbs.py's MAX_HIGH), 320 class names at the very most. Tested in a real client on 2026-11-01:
    /// the stylesheet of all 16,384 loads and flips pages without a hitch.</summary>
    public const int MaxHigh = 256;

    static readonly Dictionary<string, int> numbers = Number();

    static Dictionary<string, int> Number()
    {
        var d = new Dictionary<string, int>(HudThumbList.Keys.Length, StringComparer.Ordinal);
        for (var i = 0; i < HudThumbList.Keys.Length; i++) d.TryAdd(HudThumbList.Keys[i], i);
        return d;
    }

    /// <summary>How many pictures are numbered (touching it builds the lookup, which the plugin does at load).</summary>
    public static int Count => numbers.Count;

    /// <summary>What a map is numbered by: an official map's name, otherwise its Workshop id.</summary>
    public static string Key(HudCatalog.Entry m) => m.Official ? m.Name : m.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Picture n's two classes.</summary>
    public static string[] Classes(int n) => [$"th{n / Base}", $"tl{n % Base}"];

    /// <summary>A map's picture classes, or none when it has no picture.</summary>
    public static string[] Classes(HudCatalog.Entry m) => numbers.TryGetValue(Key(m), out var n) ? Classes(n) : [];

    /// <summary>Whether the addon has a picture for this map key.</summary>
    public static bool Has(string key) => numbers.ContainsKey(key);

    public static bool IsThumbClass(string cls) => cls.Length > 2 && cls[0] == 't' && cls[1] is 'h' or 'l' && cls[2..].All(char.IsAsciiDigit);
}
