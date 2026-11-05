// The menu's Sync page (SERVER, Sync): the Workshop sync's dashboard for everybody in game - what it does now,
// what waits in its queue, and what it did. Shared like Hud.cs (plain BCL): the bridge writes every word of it (SyncDash.cs builds the block it
// serves on /dash/<secret>), the plugin pages it and puts it on screen, and --selftest drives both halves.
//
// The plugin passes the block through without knowing what it says. Every word is a dialog variable on gg_screen whose name starts with osd,
// which the page's labels inherit ({s:osdstate}); every state is a class on gg_screen (osdbusy, osdp35, osdq3 ...). Nothing is sent to a panel
// of the page itself, so a game that holds an older content - which has neither the tab nor the labels - goes on as it was, as with content
// v4.1.0's popup. The block's lists are paged per player: the plugin keeps each player's page of each list and sends that player the rows on it,
// as the variables <list><row><field> (osdq3b: the third row on this player's page of the queue, its second field), the class <list><row> while
// that row holds something and <list><row><tone> for its colour. The pagers' buttons (gg_osdq_prev ...) are all it reads back. So a new word,
// number or row is a bridge update alone; only a new panel needs a HUD release.
using System.Globalization;
using System.Text;

public sealed class HudDash
{
    public const string Magic = "gg-dash\t1";

    /// <summary>Every variable and class the block may set starts with this, so it can never touch one of the menu's own.</summary>
    public const string Prefix = "osd";

    /// <summary>The page's tab (content v4.3.0).</summary>
    public const string Tab = "gg_tab_sync";

    /// <summary>What the plugin takes from one block at most: it is the bridge's, but a broken one must not flood the engine.</summary>
    public const int MaxWords = 120, MaxClasses = 200, MaxLists = 6, MaxRows = 400, MaxFields = 6, MaxPageSize = 12, MaxText = 200;

    /// <summary>A row's colour: a class <list><row><tone> on gg_screen while that row shows it.</summary>
    public static readonly string[] Tones = ["ok", "bad", "dim", "hot"];

    /// <summary>A pager's buttons: gg_<list>_<op>.</summary>
    public static readonly string[] PagerOps = ["first", "prev", "next", "last"];

    /// <summary>The lists the layout of content v4.3.0 has rows for: the queue's next maps, the runs and the map events (SyncWords fills them).</summary>
    public const string QueueList = "osdq", RunList = "osdr", MapList = "osdm";

    /// <summary>The buttons content v4.3.0 added: the tab and the pagers the layout has. A click on one proves that player's game holds v4.3.0;
    /// nothing is sent to them.</summary>
    public static readonly string[] LayoutButtons =
        [Tab, Button(QueueList, "prev"), Button(QueueList, "next"), Button(RunList, "prev"), Button(RunList, "next"),
         Button(MapList, "first"), Button(MapList, "prev"), Button(MapList, "next"), Button(MapList, "last")];

    public sealed record Row(string Tone, string[] Fields);

    public sealed record DashList(string Name, int PageSize, List<Row> Rows)
    {
        public int Pages => Math.Max(1, (Rows.Count + PageSize - 1) / PageSize);
    }

    public long Version;
    public readonly Dictionary<string, string> Words = new(StringComparer.Ordinal);
    public readonly Dictionary<string, bool> Classes = new(StringComparer.Ordinal);
    public readonly List<DashList> Lists = new();

    /// <summary>A word's or a class's name: osd and up to 13 lower-case letters and digits.</summary>
    public static bool IsName(string s) => s.Length is > 3 and <= 16 && s.StartsWith(Prefix, StringComparison.Ordinal) && s.Skip(3).All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>A list's name: osd and up to 4 lower-case letters, no digits - its rows add theirs (osdq3, osdq3b).</summary>
    public static bool IsListName(string s) => s.Length is > 3 and <= 7 && s.StartsWith(Prefix, StringComparison.Ordinal) && s.Skip(3).All(c => c is >= 'a' and <= 'z');

    /// <summary>Field k of a row (0-based) as its variable's last letter: a, b, c ...</summary>
    public static string Field(int k) => ((char)('a' + k)).ToString();

    /// <summary>A pager button's id.</summary>
    public static string Button(string list, string op) => $"gg_{list}_{op}";

    public string Encode()
    {
        var s = new StringBuilder(Magic).Append('\n');
        s.Append("ver\t").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (name, text) in Words) s.Append("w\t").Append(name).Append('\t').Append(Hud.Clean(text, MaxText)).Append('\n');
        foreach (var (name, on) in Classes) s.Append("c\t").Append(name).Append('\t').Append(on ? '1' : '0').Append('\n');
        foreach (var l in Lists)
        {
            s.Append("l\t").Append(l.Name).Append('\t').Append(l.PageSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var r in l.Rows)
                s.Append("r\t").Append(l.Name).Append('\t').Append(r.Tone).Append('\t').Append(string.Join('\t', r.Fields.Select(f => Hud.Clean(f, MaxText)))).Append('\n');
        }
        return s.ToString();
    }

    /// <summary>The block out of Encode's text, or null when it is not one. Unknown lines are skipped and bad ones dropped, so a newer bridge
    /// can add lines without breaking an older plugin.</summary>
    public static HudDash? Decode(string? text)
    {
        if (text == null || !text.StartsWith(Magic, StringComparison.Ordinal)) return null;
        var d = new HudDash();
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            switch (f[0])
            {
                case "ver" when f.Length >= 2: d.Version = long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0; break;
                case "w" when f.Length >= 3 && IsName(f[1]) && d.Words.Count < MaxWords: d.Words[f[1]] = Hud.Clean(f[2], MaxText); break;
                case "c" when f.Length >= 3 && IsName(f[1]) && d.Classes.Count < MaxClasses: d.Classes[f[1]] = f[2] == "1"; break;
                case "l" when f.Length >= 3 && IsListName(f[1]) && d.Lists.Count < MaxLists && d.Lists.All(x => x.Name != f[1])
                              && int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= MaxPageSize:
                    d.Lists.Add(new DashList(f[1], size, new List<Row>()));
                    break;
                case "r" when f.Length >= 4 && d.Lists.FirstOrDefault(x => x.Name == f[1]) is { } list && list.Rows.Count < MaxRows:
                    list.Rows.Add(new Row(Tones.Contains(f[2]) ? f[2] : "", f.Skip(3).Take(MaxFields).Select(x => Hud.Clean(x, MaxText)).ToArray()));
                    break;
            }
        }
        return d;
    }

    /// <summary>A click on a pager's button: that player's page of that list moves (pages: list -> page, 0-based). True when it was one.</summary>
    public static bool Click(HudDash? d, string buttonId, Dictionary<string, int> pages)
    {
        if (d == null || !buttonId.StartsWith("gg_", StringComparison.Ordinal)) return false;
        var under = buttonId.IndexOf('_', 3);
        if (under < 0) return false;
        var (name, op) = (buttonId[3..under], buttonId[(under + 1)..]);
        if (d.Lists.FirstOrDefault(x => x.Name == name) is not { } list || !PagerOps.Contains(op)) return false;
        var page = Math.Clamp(pages.GetValueOrDefault(name), 0, list.Pages - 1);
        pages[name] = op switch { "first" => 0, "prev" => Math.Max(0, page - 1), "next" => Math.Min(list.Pages - 1, page + 1), _ => list.Pages - 1 };
        return true;
    }

    /// <summary>What a player with the page open is sent: every word, every class, and that player's page of each list - its rows' fields (empty
    /// where a row or a field is missing, so nothing of a longer page stays behind), the classes of the rows in use and of their tones, the page's
    /// number and count (osdqpn, osdqps) and whether it is the first or the last (osdqfirst, osdqlast) or the list has nothing (osdqnone).
    /// Pages are clamped to what the lists have now. Without a block yet: the one word that says so.</summary>
    public static (List<(string Name, string Text)> Words, List<(string Name, bool On)> Classes) View(HudDash? d, Dictionary<string, int> pages)
    {
        var w = new List<(string, string)>();
        var c = new List<(string, bool)>();
        if (d == null)
        {
            w.Add(("osdstate", "Waiting for the server"));
            c.Add(("osdnone", true));
            return (w, c);
        }
        foreach (var (name, text) in d.Words) w.Add((name, text));
        foreach (var (name, on) in d.Classes) c.Add((name, on));
        foreach (var l in d.Lists)
        {
            var page = Math.Clamp(pages.GetValueOrDefault(l.Name), 0, l.Pages - 1);
            pages[l.Name] = page;
            var fields = Math.Min(MaxFields, l.Rows.Count == 0 ? 1 : l.Rows.Max(r => r.Fields.Length));
            for (var i = 1; i <= l.PageSize; i++)
            {
                var at = page * l.PageSize + i - 1;
                var row = at < l.Rows.Count ? l.Rows[at] : null;
                for (var k = 0; k < fields; k++) w.Add(($"{l.Name}{i}{Field(k)}", row != null && k < row.Fields.Length ? row.Fields[k] : ""));
                c.Add(($"{l.Name}{i}", row != null));
                foreach (var tone in Tones) c.Add(($"{l.Name}{i}{tone}", row?.Tone == tone));
            }
            w.Add(($"{l.Name}pn", (page + 1).ToString(CultureInfo.InvariantCulture)));
            w.Add(($"{l.Name}ps", l.Pages.ToString(CultureInfo.InvariantCulture)));
            c.Add(($"{l.Name}first", page == 0));
            c.Add(($"{l.Name}last", page == l.Pages - 1));
            c.Add(($"{l.Name}none", l.Rows.Count == 0));
        }
        return (w, c);
    }
}
