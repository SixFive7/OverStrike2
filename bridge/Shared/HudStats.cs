// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text;

/// <summary>THE STATS PAGE (content v4.15.0; the owner's 304 a, 2026-11-19): the server's records, worked out by the bridge (Stats.cs) out of the match history,
/// the likes, our ballots and the crashes, and sent to the plugin as this block (the bridge's /stats/, fetched when HudState.StatsVersion moves) with every word
/// final. The page's words are dialog variables and its states classes, the same for everybody - on os_global, set once for all - but You, a player's own line,
/// on their os_screen: the block carries a line of You for every player in the history. The bars are a class w0-w10 each on their panel, the two pictures a map
/// (the plugin's catalog gives its picture). The plugin sends them only while somebody's window shows the page, and only what changed.</summary>
public sealed class HudStats
{
    /// <summary>The STATS tab in the top row: the STATS page.</summary>
    public const string Tab = "os_tab_stats";

    /// <summary>You's words: matches, hours, the favourite map, likes given, since when, the place by matches.</summary>
    public static readonly string[] YouVars = ["ossy1", "ossy2", "ossy3", "ossy4", "ossy5", "ossy6"];

    /// <summary>The page's classes, on os_global: stnone while there is no history, stf1-6 a fun record that has data, str6 and str7 the two player records
    /// that are counted from the frags on.</summary>
    public static readonly string[] ClassNames = ["stnone", "stf1", "stf2", "stf3", "stf4", "stf5", "stf6", "str6", "str7"];

    /// <summary>The bars: the most played maps' (each against the first) and the modes' shares.</summary>
    public static readonly string[] BarPanels = ["os_st_bar1", "os_st_bar2", "os_st_bar3", "os_st_bar4", "os_st_bar5", "os_st_mar", "os_st_mdm", "os_st_mwm"];

    /// <summary>The pictures: the most played map's and the most liked one's.</summary>
    public static readonly string[] PicturePanels = ["os_st_pic1", "os_st_pic2"];

    public long Version;
    public List<(string Name, string Text)> Vars = new();      // os_global's words: osst, osstnw, osstmd, ossfnote and the tiles' rows
    public Dictionary<string, bool> Classes = new();           // os_global's classes (ClassNames)
    public Dictionary<string, int> Bars = new();               // a bar's step, 0-10 (BarPanels)
    public Dictionary<string, string> Pictures = new();        // a picture's map, as the map list keys it ("" none)
    public Dictionary<long, string[]> You = new();             // a player's own line, by account (YouVars' words)
    public string[] Nobody = ["No match yet", "", "", "", "", ""]; // You for a player with no match

    // ---- the block: a version line, then a line each, tab-separated ----

    static string One(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    public string Encode()
    {
        var sb = new StringBuilder();
        sb.Append("stats\t").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (name, text) in Vars) sb.Append("v\t").Append(name).Append('\t').Append(One(text)).Append('\n');
        foreach (var (cls, on) in Classes) sb.Append("c\t").Append(cls).Append('\t').Append(on ? '1' : '0').Append('\n');
        foreach (var (panel, step) in Bars) sb.Append("b\t").Append(panel).Append('\t').Append(Math.Clamp(step, 0, 10).ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (panel, key) in Pictures) sb.Append("p\t").Append(panel).Append('\t').Append(One(key)).Append('\n');
        sb.Append("n\t").Append(string.Join('\t', Nobody.Select(One))).Append('\n');
        foreach (var (account, words) in You) sb.Append("y\t").Append(account.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(string.Join('\t', words.Select(One))).Append('\n');
        return sb.ToString();
    }

    public static HudStats? Decode(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("stats\t", StringComparison.Ordinal)
            || !long.TryParse(lines[0][6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)) return null;
        var st = new HudStats { Version = version };
        static string[] Six(string[] f, int from) => [.. Enumerable.Range(from, YouVars.Length).Select(i => i < f.Length ? f[i] : "")];
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split('\t');
            switch (f[0])
            {
                case "v" when f.Length >= 3: st.Vars.Add((f[1], f[2])); break;
                case "c" when f.Length >= 3: st.Classes[f[1]] = f[2] == "1"; break;
                case "b" when f.Length >= 3 && int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var step): st.Bars[f[1]] = Math.Clamp(step, 0, 10); break;
                case "p" when f.Length >= 3: st.Pictures[f[1]] = f[2]; break;
                case "n": st.Nobody = Six(f, 1); break;
                case "y" when f.Length >= 2 && long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var account): st.You[account] = Six(f, 2); break;
            }
        }
        return st;
    }

    // ---- what the frame puts on screen ----

    /// <summary>Everybody's part: the words on os_global, the page's classes there, and each bar's step as its w0-w10 class.</summary>
    public (List<(string Name, string Text)> Vars, List<(string Id, string Class, bool On)> Classes) Global()
    {
        var classes = new List<(string, string, bool)>();
        foreach (var name in ClassNames) classes.Add((HudView.Global, name, Classes.GetValueOrDefault(name)));
        foreach (var panel in BarPanels)
        {
            var step = Bars.GetValueOrDefault(panel);
            for (var k = 0; k <= 10; k++) classes.Add((panel, $"w{k.ToString(CultureInfo.InvariantCulture)}", k == step));
        }
        return (Vars, classes);
    }

    /// <summary>A player's own line, on their os_screen.</summary>
    public List<(string Name, string Text)> YouOf(long account)
    {
        var words = You.TryGetValue(account, out var w) ? w : Nobody;
        return [.. YouVars.Select((v, i) => (v, i < words.Length ? words[i] : ""))];
    }
}
