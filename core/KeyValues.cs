using System.Text;
using System.Text.RegularExpressions;

namespace OverStrike2.Core;

/// <summary>One entry of Valve's KeyValues text (an .acf): a key with a string value, or a key with a block of entries.</summary>
public sealed class Kv(string key, string? value, List<Kv>? block)
{
    public string Key { get; } = key;
    public string? Value { get; set; } = value;
    public List<Kv>? Block { get; } = block;

    public static Kv Text(string key, string value) => new(key, value, null);
    public static Kv Of(string key, List<Kv> block) => new(key, null, block);
}

/// <summary>Valve's KeyValues text, the way the Steam client writes appworkshop_730.acf: parse, dump, and the edits the server makes to
/// it. A file is only ever rewritten when it comes back byte-identical through <see cref="Parse"/> and <see cref="Dump"/> - never a file
/// this code does not fully understand.</summary>
public static partial class KeyValues
{
    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"|([{}])")]
    private static partial Regex Token();

    /// <summary>The entries of a KeyValues text, order kept. Throws FormatException for a block without a key, a key without a value,
    /// an unbalanced or truncated text.</summary>
    public static List<Kv> Parse(string text)
    {
        var stack = new Stack<List<Kv>>();
        var cur = new List<Kv>();
        string? key = null;
        foreach (Match m in Token().Matches(text))
        {
            if (m.Groups[2].Value == "{")
            {
                if (key is null)
                    throw new FormatException("a block without a key");
                var child = new List<Kv>();
                cur.Add(Kv.Of(key, child));
                stack.Push(cur);
                cur = child;
                key = null;
            }
            else if (m.Groups[2].Value == "}")
            {
                if (stack.Count == 0 || key is not null)
                    throw new FormatException("an unbalanced block");
                cur = stack.Pop();
            }
            else if (key is null)
                key = m.Groups[1].Value;
            else
            {
                cur.Add(Kv.Text(key, m.Groups[1].Value));
                key = null;
            }
        }
        if (stack.Count > 0 || key is not null)
            throw new FormatException("truncated");
        return cur;
    }

    /// <summary>The text of entries, as the Steam client writes it: tabs, a line each, every line ended.</summary>
    public static string Dump(List<Kv> kv)
    {
        var sb = new StringBuilder();
        Dump(kv, 0, sb);
        return sb.ToString();
    }

    static void Dump(List<Kv> kv, int depth, StringBuilder sb)
    {
        var tab = new string('\t', depth);
        foreach (var e in kv)
        {
            if (e.Block != null)
            {
                sb.Append(tab).Append('"').Append(e.Key).Append("\"\n").Append(tab).Append("{\n");
                Dump(e.Block, depth + 1, sb);
                sb.Append(tab).Append("}\n");
            }
            else
                sb.Append(tab).Append('"').Append(e.Key).Append("\"\t\t\"").Append(e.Value).Append("\"\n");
        }
    }

    /// <summary>The first entry with this key, or null.</summary>
    public static Kv? Get(List<Kv>? kv, string key) => kv?.FirstOrDefault(e => e.Key == key);

    static bool Digits(string? s) => !string.IsNullOrEmpty(s) && s.All(char.IsAsciiDigit);

    /// <summary>{workshop id: installed timeupdated} of an appworkshop_730.acf (0 when it names none). Throws FormatException.</summary>
    public static Dictionary<string, long> Installed(string acfText)
    {
        var out_ = new Dictionary<string, long>();
        foreach (var e in Get(Get(Parse(acfText), "AppWorkshop")?.Block, "WorkshopItemsInstalled")?.Block ?? [])
            if (e.Block != null)
                out_[e.Key] = Get(e.Block, "timeupdated")?.Value is { } t && Digits(t) && long.TryParse(t, out var v) ? v : 0;
        return out_;
    }

    /// <summary>{workshop id: installed size in bytes}; empty when the text does not parse.</summary>
    public static Dictionary<string, long> Sizes(string acfText)
    {
        var out_ = new Dictionary<string, long>();
        try
        {
            foreach (var e in Get(Get(Parse(acfText), "AppWorkshop")?.Block, "WorkshopItemsInstalled")?.Block ?? [])
                if (e.Block != null && Get(e.Block, "size")?.Value is { } s && Digits(s) && long.TryParse(s, out var v))
                    out_[e.Key] = v;
        }
        catch (FormatException)
        {
        }
        return out_;
    }

    /// <summary>{workshop id: installed manifest} - the content each map holds, which the Workshop calls its hcontent_file; empty when the
    /// text does not parse.</summary>
    public static Dictionary<string, string> Manifests(string acfText)
    {
        var out_ = new Dictionary<string, string>();
        try
        {
            foreach (var e in Get(Get(Parse(acfText), "AppWorkshop")?.Block, "WorkshopItemsInstalled")?.Block ?? [])
                if (e.Block != null && Get(e.Block, "manifest")?.Value is { } m && Digits(m))
                    out_[e.Key] = m;
        }
        catch (FormatException)
        {
        }
        return out_;
    }

    /// <summary>(WorkshopItemsInstalled, WorkshopItemDetails) values of one item, or null when the acf has no installed entry for it.</summary>
    public static (Dictionary<string, string> Installed, Dictionary<string, string> Details)? Entries(string acfText, string id)
    {
        List<Kv> kv;
        try
        {
            kv = Parse(acfText);
        }
        catch (FormatException)
        {
            return null;
        }
        var workshop = Get(kv, "AppWorkshop")?.Block;
        var installed = Get(Get(workshop, "WorkshopItemsInstalled")?.Block, id);
        if (installed?.Block is null)
            return null;
        var details = Get(Get(workshop, "WorkshopItemDetails")?.Block, id);
        return (installed.Block.Where(e => e.Block is null).ToDictionary(e => e.Key, e => e.Value ?? ""),
                (details?.Block ?? []).Where(e => e.Block is null).ToDictionary(e => e.Key, e => e.Value ?? ""));
    }

    /// <summary>The acf text with items added or replaced (add: id -> its installed and its details entries, empty for none) and items
    /// dropped from both sections, SizeOnDisk the sum of the installed sizes again, every section's entries in id order. Null when the text
    /// does not come back byte-identical or has no AppWorkshop block.</summary>
    public static string? Edit(string acfText, IReadOnlyDictionary<string, (List<(string Key, string Value)> Installed, List<(string Key, string Value)> Details)>? add,
                               IEnumerable<string>? drop)
    {
        List<Kv> kv;
        try
        {
            kv = Parse(acfText);
        }
        catch (FormatException)
        {
            return null;
        }
        if (Dump(kv) != acfText)
            return null;
        var workshop = Get(kv, "AppWorkshop")?.Block;
        if (workshop is null)
            return null;
        add ??= new Dictionary<string, (List<(string, string)>, List<(string, string)>)>();
        var gone = new HashSet<string>(drop ?? []);
        gone.UnionWith(add.Keys);
        foreach (var (section, part) in new[] { ("WorkshopItemsInstalled", 0), ("WorkshopItemDetails", 1) })
        {
            var entries = Get(workshop, section)?.Block;
            if (entries is null)
            {
                entries = [];
                workshop.Add(Kv.Of(section, entries));
            }
            var kept = entries.Where(e => !gone.Contains(e.Key)).ToList();
            foreach (var (id, pair) in add)
            {
                var values = part == 0 ? pair.Installed : pair.Details;
                if (values.Count > 0)
                    kept.Add(Kv.Of(id, values.Select(v => Kv.Text(v.Key, v.Value)).ToList()));
            }
            var ordered = kept.OrderBy(e => Digits(e.Key) ? 0 : 1).ThenBy(e => Digits(e.Key) && ulong.TryParse(e.Key, out var n) ? n : 0UL).ToList();
            entries.Clear();
            entries.AddRange(ordered);
        }
        long total = 0;
        foreach (var e in Get(workshop, "WorkshopItemsInstalled")!.Block!)
            if (e.Block != null && Get(e.Block, "size")?.Value is { } s && Digits(s) && long.TryParse(s, out var v))
                total += v;
        foreach (var e in workshop)
            if (e.Key == "SizeOnDisk" && e.Block is null)
                e.Value = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Dump(kv);
    }

    /// <summary>The acf text without `ids` (both sections), checked to parse back. Throws FormatException when it has no AppWorkshop block.</summary>
    public static string Forget(string acfText, IEnumerable<string> ids)
    {
        var kv = Parse(acfText);
        var workshop = Get(kv, "AppWorkshop")?.Block ?? throw new FormatException("no AppWorkshop block");
        var drop = new HashSet<string>(ids);
        foreach (var section in new[] { "WorkshopItemsInstalled", "WorkshopItemDetails" })
            Get(workshop, section)?.Block?.RemoveAll(e => drop.Contains(e.Key));
        var text = Dump(kv);
        if (Dump(Parse(text)) != text)
            throw new FormatException("the edited acf does not parse back");
        return text;
    }
}
