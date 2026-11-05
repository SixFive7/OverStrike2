// The committed search.json, kept in step with the committed maps.json: `dotnet run -- --search-index` in bridge/ (Usage below).
//
// search.json is the map browser's full-text index: for every map on maps.json, the words a search finds it by - its name, its Workshop id
// and its Workshop tags, lower case. Other authors' descriptions never enter it (AGENTS.md). The bridge reads it like maps.json
// (SearchIndex.cs) and serves it to the plugin, and HudSearch.cs matches the typed words against the name first, then this text. On a server
// the Workshop sync adds a text for every map it publishes, of every mode; this command keeps the repository's file in step with the
// repository's maps.json (the Arms Race list: the other modes' maps get theirs on the server). deploy/data.py copies it to a server that has
// none yet.
//
// The texts are Python's, because every committed text was made by Python, and a regenerated file must not change a byte: Python's
// str.lower() (İ becomes i and a combining dot, where .NET keeps it; a capital sigma that ends a word becomes the final sigma), its
// str.split() (U+001C-U+001F are whitespace too), lengths in code points, and json.dumps(indent=0, ensure_ascii=False) with a final
// newline. A value Python would only have str()-ed into a text - an id, a name or a tag that is neither text nor a whole number - is refused.
// Plain BCL, no bridge state: Main hands the arguments over before it reads anything.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

static class SearchIndexFile
{
    public const int MaxText = 300; // code points: a title, an id and tags fit well inside this (the longest is about 120); anything longer is not what belongs here

    const string Usage = """
        dotnet run -- --search-index [--check] [--export F.json] [--maps F] [--out F]       (in bridge/)
          (nothing more)    write search.json: maps.json's order, a line for every map, no other
          --export F.json   the same, a new map's tags from what `os2-sync export` printed
          --check           write nothing; exit 1 when search.json is not in step with maps.json
          --maps F          the bridge's maps.json (default: maps.json here)
          --out F           the search.json to check or write (default: beside --maps)
        A map new on maps.json gets its name and id, and its tags when --export names them; a known map keeps its text; a map that left
        maps.json loses its line.
        """;

    /// <summary>Input this command cannot use: it writes nothing and exits 1.</summary>
    sealed class Refused(string message) : Exception(message);

    public static int Command(string[] args)
    {
        string? maps = null, output = null, export = null;
        var check = false;
        for (var i = 0; i < args.Length; i++)
        {
            var (name, value) = (args[i], (string?)null);
            if (name.StartsWith("--", StringComparison.Ordinal) && name.IndexOf('=') is > 2 and var eq) (name, value) = (name[..eq], name[(eq + 1)..]);
            if (name is "-h" or "--help") { Console.WriteLine(Usage); return 0; }
            if (name == "--check" && value == null) { check = true; continue; }
            if (name is not ("--maps" or "--out" or "--export")) return Wrong($"unknown argument {args[i]}");
            value ??= i + 1 < args.Length ? args[++i] : null;
            if (string.IsNullOrEmpty(value)) return Wrong($"{name} needs a file");
            if (name == "--maps") maps = value; else if (name == "--out") output = value; else export = value;
        }
        maps ??= "maps.json";
        output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(maps)) ?? ".", "search.json");
        try
        {
            var list = ReadMaps(maps);
            var listed = list.Select(m => m.Id).ToList();
            var known = File.Exists(output) ? ReadIndex(output) : [];
            if (check)
            {
                var found = Problems(listed, known);
                Console.WriteLine($"{output}: {(found.Count == 0 ? "in step" : "NOT in step - " + string.Join("; ", found))} ({known.Count} texts, {list.Count} maps)");
                return found.Count == 0 ? 0 : 1;
            }
            var index = Build(list, known, export == null ? new Dictionary<string, List<string>>() : ReadTags(export));
            var refused = Problems(listed, index.Select(e => (e.Key, (string?)e.Text)).ToList());
            if (refused.Count > 0) { Console.Error.WriteLine("refused: " + string.Join("; ", refused)); return 1; }
            var bytes = new UTF8Encoding(false, true).GetBytes(Encode(index)); // a lone surrogate throws here, before the file is touched
            File.WriteAllBytes(output, bytes);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"wrote {output}: {index.Count} maps, {index.Sum(e => Len(e.Text)):N0} characters of text ({bytes.Length:N0} bytes)"));
            return 0;
        }
        catch (Exception e) when (e is Refused or IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException
                                      or EncoderFallbackException or InvalidOperationException)
        {
            Console.Error.WriteLine("refused: " + e.Message);
            return 1;
        }
    }

    static int Wrong(string why)
    {
        Console.Error.WriteLine($"--search-index: {why}\n{Usage}");
        return 2;
    }

    // ---- the files ----

    /// <summary>maps.json's maps in its order: each one's id as Python's str() writes it, and its name (null when it has none that is
    /// text: refused only for a map that needs a new text).</summary>
    static List<(string Id, string? Name)> ReadMaps(string path)
    {
        using var doc = Parse(path);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new Refused($"{path}: not a list of maps");
        var maps = new List<(string Id, string? Name)>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("id", out var id) || Str(id) is not { } wid)
                throw new Refused($"{path}: map {maps.Count + 1} has no id");
            maps.Add((wid, m.TryGetProperty("name", out var name) ? Str(name) : null));
        }
        return maps;
    }

    /// <summary>search.json's texts in the file's order, as a Python dict holds them: a key given twice keeps its first place and its last
    /// text. A value that is not text reads as "" when Python's bool() of it is false (made again, like an empty text) and as null when true
    /// (refused where it is used).</summary>
    static List<(string Key, string? Text)> ReadIndex(string path)
    {
        using var doc = Parse(path);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new Refused($"{path}: not an object of map key -> text");
        var index = new List<(string Key, string? Text)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : Truthy(p.Value) ? null : "";
            if (at.TryGetValue(p.Name, out var i)) index[i] = (p.Name, text);
            else { at[p.Name] = index.Count; index.Add((p.Name, text)); }
        }
        return index;
    }

    /// <summary>What `os2-sync export` printed: the tags of the maps on its "maps.json" list, by id.</summary>
    static Dictionary<string, List<string>> ReadTags(string path)
    {
        using var doc = Parse(path);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new Refused($"{path}: not what `os2-sync export` prints");
        var tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("maps.json", out var maps)) return tags;
        if (maps.ValueKind != JsonValueKind.Array) throw new Refused($"{path}: its maps.json is not a list");
        foreach (var e in maps.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id) || Str(id) is not { } wid)
                throw new Refused($"{path}: a map on its maps.json has no id");
            var list = new List<string>();
            if (e.TryGetProperty("tags", out var t) && Truthy(t))
            {
                if (t.ValueKind != JsonValueKind.Array) throw new Refused($"{path}: the tags of {wid} are not a list");
                foreach (var tag in t.EnumerateArray()) list.Add(Str(tag) ?? throw new Refused($"{path}: a tag of {wid} is not text"));
            }
            tags[wid] = list;
        }
        return tags;
    }

    /// <summary>A JSON file read as Python reads it: UTF-8, strictly.</summary>
    static JsonDocument Parse(string path) => JsonDocument.Parse(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));

    /// <summary>Python's str() of an id, a name or a tag: text as it is, a whole number in its digits; null for anything else.</summary>
    static string? Str(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number when v.GetRawText() is var raw && raw.IndexOfAny(['.', 'e', 'E']) < 0
            => BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Python's bool() of a JSON value: null, false, 0, "", [] and {} are false.</summary>
    static bool Truthy(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() != "",
        JsonValueKind.Number => double.Parse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture) != 0,
        JsonValueKind.True => true,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Object => v.EnumerateObject().Any(),
        _ => false,
    };

    // ---- the index ----

    /// <summary>The words a search finds a map by: its name's, its Workshop id, its tags' - lower case, one space between.</summary>
    public static string TextOf(string name, string id, IEnumerable<string> tags)
    {
        var words = Words(Lower(name));
        words.Add(id);
        foreach (var tag in tags) words.AddRange(Words(Lower(tag)));
        return string.Join(' ', words);
    }

    /// <summary>search.json's content for these maps, in their order: a known map keeps its text, a new one - or one whose text is empty -
    /// gets its own.</summary>
    public static List<(string Key, string Text)> Build(IReadOnlyList<(string Id, string? Name)> maps, IReadOnlyList<(string Key, string? Text)> known,
        IReadOnlyDictionary<string, List<string>> tags)
    {
        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, text) in known) texts[key] = text;
        var index = new List<(string Key, string Text)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, name) in maps)
        {
            var text = texts.TryGetValue(id, out var kept) && kept != ""
                ? kept ?? throw new Refused($"the search text of {id} is not text")
                : TextOf(name ?? throw new Refused($"map {id} on maps.json has no name"), id, tags.TryGetValue(id, out var t) ? t : []);
            if (at.TryGetValue(id, out var i)) index[i] = (id, text);
            else { at[id] = index.Count; index.Add((id, text)); }
        }
        return index;
    }

    /// <summary>What is wrong with an index for maps.json's ids: empty when they are in step.</summary>
    public static List<string> Problems(IReadOnlyList<string> listed, IReadOnlyList<(string Key, string? Text)> index)
    {
        var keys = index.Select(e => e.Key).ToList();
        var have = keys.ToHashSet(StringComparer.Ordinal);
        var onList = listed.ToHashSet(StringComparer.Ordinal);
        var found = new List<string>();
        var missing = listed.Where(w => !have.Contains(w)).ToList();
        if (missing.Count > 0) found.Add($"{missing.Count} map(s) of maps.json have no text, e.g. {Some(missing)}");
        var stray = keys.Where(w => !onList.Contains(w)).ToList();
        if (stray.Count > 0) found.Add($"{stray.Count} text(s) for maps not on maps.json, e.g. {Some(stray)}");
        if (missing.Count == 0 && stray.Count == 0 && !keys.SequenceEqual(listed)) found.Add("the texts are not in maps.json's order");
        var notText = index.Where(e => e.Text == null).Select(e => e.Key).ToList();
        if (notText.Count > 0) found.Add($"{notText.Count} value(s) that are not text, e.g. {Some(notText)}");
        var noId = index.Where(e => e.Text != null && !Words(e.Text).Contains(e.Key)).Select(e => e.Key).ToList();
        if (noId.Count > 0) found.Add($"{noId.Count} text(s) without their own Workshop id, e.g. {Some(noId)}");
        var tooLong = index.Where(e => e.Text != null && Len(e.Text) > MaxText).Select(e => e.Key).ToList();
        if (tooLong.Count > 0) found.Add($"{tooLong.Count} text(s) over {MaxText} characters - more than a title, an id and tags, e.g. {Some(tooLong)}");
        return found;
    }

    /// <summary>The first five, as Python prints a list of them: ['1', '2'].</summary>
    static string Some(List<string> keys) => "[" + string.Join(", ", keys.Take(5).Select(k => $"'{k}'")) + "]";

    /// <summary>The file: json.dumps(index, ensure_ascii=False, indent=0) and a newline - a map a line, and nothing escaped but quotes,
    /// backslashes and control characters.</summary>
    public static string Encode(IReadOnlyList<(string Key, string Text)> index)
    {
        if (index.Count == 0) return "{}\n";
        var s = new StringBuilder("{\n");
        for (var i = 0; i < index.Count; i++)
        {
            if (i > 0) s.Append(",\n");
            Quote(s, index[i].Key);
            s.Append(": ");
            Quote(s, index[i].Text);
        }
        return s.Append("\n}\n").ToString();
    }

    static void Quote(StringBuilder s, string text)
    {
        s.Append('"');
        foreach (var c in text)
            switch (c)
            {
                case '"': s.Append("\\\""); break;
                case '\\': s.Append("\\\\"); break;
                case '\n': s.Append("\\n"); break;
                case '\r': s.Append("\\r"); break;
                case '\t': s.Append("\\t"); break;
                case '\b': s.Append("\\b"); break;
                case '\f': s.Append("\\f"); break;
                default:
                    if (c < ' ') s.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else s.Append(c);
                    break;
            }
        s.Append('"');
    }

    // ---- Python's text rules ----

    /// <summary>Python's str.lower(): every code point's full lower case - İ (U+0130) becomes i and a combining dot above, where .NET keeps
    /// it - and a capital sigma that ends a word becomes the final sigma (Unicode's Final_Sigma, which .NET's casing does not apply).</summary>
    public static string Lower(string text)
    {
        var points = Points(text);
        var s = new StringBuilder(text.Length);
        for (var i = 0; i < points.Count; i++)
        {
            var c = points[i];
            if (c == 0x3A3) s.Append(FinalSigma(points, i) ? (char)0x3C2 : (char)0x3C3); // the final sigma, else the small sigma
            else if (c == 0x130) s.Append('i').Append((char)0x307);                    // i and the combining dot above
            else if (c is >= 0xD800 and <= 0xDFFF) s.Append((char)c); // a lone surrogate stays as it is, as in Python
            else s.Append(Rune.ToLowerInvariant(new Rune(c)).ToString());
        }
        return s.ToString();
    }

    /// <summary>Python's str.split(): the runs between whitespace, where Python's whitespace also has U+001C-U+001F (.NET's does not).</summary>
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || IsSpace(text[i]);
            if (!space && start < 0) start = i;
            else if (space && start >= 0) { words.Add(text[start..i]); start = -1; }
        }
        return words;
    }

    static bool IsSpace(char c) =>
        (int)c is (>= 0x09 and <= 0x0D) or (>= 0x1C and <= 0x20) or 0x85 or 0xA0 or 0x1680 or (>= 0x2000 and <= 0x200A) or 0x2028 or 0x2029 or 0x202F
            or 0x205F or 0x3000;

    /// <summary>Python's len(): code points, a surrogate pair one.</summary>
    static int Len(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++, n++)
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        return n;
    }

    static List<int> Points(string text)
    {
        var points = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                points.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else points.Add(text[i]);
        }
        return points;
    }

    /// <summary>Unicode's Final_Sigma, as CPython tests it: a cased letter before the sigma (case-ignorable ones between are skipped), and
    /// none after it.</summary>
    static bool FinalSigma(List<int> points, int at)
    {
        var j = at - 1;
        while (j >= 0 && CaseIgnorable(points[j])) j--;
        if (j < 0 || !Cased(points[j])) return false;
        j = at + 1;
        while (j < points.Count && CaseIgnorable(points[j])) j++;
        return j == points.Count || !Cased(points[j]);
    }

    /// <summary>Unicode's Case_Ignorable: marks, format characters, modifier letters and symbols, and what sits inside a word without
    /// ending it - apostrophes, full stops, colons (Word_Break MidLetter, MidNumLet and Single_Quote).</summary>
    static bool CaseIgnorable(int c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol
        || c is 0x27 or 0x2E or 0x3A or 0xB7 or 0x387 or 0x55F or 0x5F4 or 0x2018 or 0x2019 or 0x2024 or 0x2027 or 0xFE13 or 0xFE52 or 0xFE55
            or 0xFF07 or 0xFF0E or 0xFF1A;

    /// <summary>Unicode's Cased, where Final_Sigma asks it (it skips the case-ignorable ones first): upper, lower and title case letters, and
    /// the few others with a case - the ordinal indicators, the Roman numerals and the circled and squared Latin letters.</summary>
    static bool Cased(int c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        || c is 0xAA or 0xBA or (>= 0x2160 and <= 0x217F) or (>= 0x24B6 and <= 0x24E9) or (>= 0x1F130 and <= 0x1F149) or (>= 0x1F150 and <= 0x1F169)
            or (>= 0x1F170 and <= 0x1F189);
}
