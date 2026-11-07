// The sync's self-test: the JSON the sync writes, its words, and search.json's texts.
using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The JSON the sync writes read back as written, its words for people, and search.json's one rule shared with the
/// bridge.</summary>
static class FormatTests
{
    // ---- the JSON the sync writes: every reader takes back the values it wrote, its text stays readable, its whole numbers whole, and the
    // bridge's lists keep the layout they have always had
    internal static void Formats()
    {
        var tricky = O("""
            {"f": [0.1, 1e16, 1.5e-07, 1793500000.0, 0.547, 2.675, 1e-05, 123456789.125, -0.0, 1e15], "s": "a\"b\\c\nd\te\u0001\u007f\u00e9\u202e\u2028\ud83d\ude00",
             "n": null, "t": true, "e": [], "o": {}, "i": [1, -2, 12345678901234]}
            """);
        var back = J(Node.Compact(tricky));
        Check("json: what the sync writes reads back as the same values - floats, escapes, text beyond ASCII and the BMP, a line separator, nesting",
              Node.Eq(back, tricky) && Node.Compact(back) == Node.Compact(tricky) && !Node.Compact(tricky).Contains('\n'));
        var cafe = "Caf" + (char)0xE9 + " " + (char)0x4E2D;
        Check("json: text stays as it is but for what JSON must escape, so names in every script stay readable; whole numbers stay whole",
              Node.Compact(new JsonObject { ["s"] = cafe + " q\"b\\", ["n"] = 5L, ["t"] = 1793500000.0, ["m"] = 0.5 }) == "{\"s\":\"" + cafe + " q\\\"b\\\\\",\"n\":5,\"t\":1793500000,\"m\":0.5}"
              && KeepSet.Integer(J(Node.Compact(new JsonObject { ["t"] = 1793500000.0 }))["t"]) == 1793500000);
        Check("json: the bridge's lists keep their layout - a member or an item a line, indent 0 (search.json), 1 (maps.json) or 2 (broken.json), a final newline",
              Node.File(O("{\"a\": [1, {\"b\": \"c\"}], \"d\": {}}"), 0) == "{\n\"a\": [\n1,\n{\n\"b\": \"c\"\n}\n],\n\"d\": {}\n}\n"
              && Node.File(A("[{\"rank\": 1, \"id\": 5}]"), 1) == "[\n {\n  \"rank\": 1,\n  \"id\": 5\n }\n]\n" && Node.File(O("{\"maps\": []}"), 2) == "{\n  \"maps\": []\n}\n");
        Check("words: sizes in MB below a gigabyte and in GB with one decimal from one on, numbers for people with their decimals",
              string.Join("|", new long[] { 0, 1048575, 1572864, 1073741824, 1342177280, 1610612736, 5L << 30 }.Select(Text.Mb)) == "0 MB|1 MB|2 MB|1.0 GB|1.3 GB|1.5 GB|5.0 GB"
              && Text.Fixed(12, 1) == "12.0" && Text.Fixed(2.675, 2) == "2.67" && Text.Fixed(7.5, 0) == "8");
        Check("words: Text.Clean and the search text - control characters, bidi overrides, a cut at a word, by code points",
              Text.Clean("  a\u0000b\u202Ec\t\td  " + string.Concat(Enumerable.Repeat("word ", 30)), 50) == "a bc d word word word word word word word word…"
              && Text.Clean(string.Concat(Enumerable.Repeat("\U0001F600", 30)) + " tail", 20) == string.Concat(Enumerable.Repeat("\U0001f600", 20)) + "…"
              && PublishPlan.SearchBlob("Big  Map!", "77", ["Arms Race", "Wingman"], "A <b>bold</b> map\nwith lines") == "big map! 77 arms race wingman a <b>bold</b> map with lines");
    }

    // ---- search.json has two writers and one rule (core's SearchText): this sync, on a server, and the bridge's --search-index
    // (bridge/SearchIndexFile.cs), for the repository's file. Every code point - alone, after a capital (the final sigma) and inside a word -
    // makes the same text through both, as a title and as a tag; and the bridge, which reads a map's name off maps.json after Text.Clean, gets
    // from it the sync's text of the title as it came
    internal static void SearchTexts()
    {
        var bridgeText = System.Reflection.Assembly.Load("GunGameBridge").GetType("SearchIndexFile", true)!
            .GetMethod("TextOf", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .CreateDelegate<Func<string, string, IEnumerable<string>, string>>();
        var differ = new List<string>();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            var c = cp is >= 0xD800 and <= 0xDFFF ? ((char)cp).ToString() : char.ConvertFromUtf32(cp); // a lone surrogate too
            var word = "a" + c + "b";
            var same = new[] { c, "A" + c, word }.All(t => PublishPlan.SearchBlob(t, "1", [], "") == bridgeText(t, "1", []))
                       && PublishPlan.SearchBlob("x", "1", [word], "") == bridgeText("x", "1", [word])
                       && bridgeText(Text.Clean(word), "1", []) == PublishPlan.SearchBlob(word, "1", [], "");
            if (!same)
                differ.Add($"U+{cp:X4}");
        }
        Check($"search.json: the sync and the bridge's --search-index make the same text of every code point, in a title and a tag ({0x110000 - differ.Count} of {0x110000}"
              + (differ.Count > 0 ? $"; not {string.Join(" ", differ.Take(12))}" : "") + ")", differ.Count == 0);
    }
}
