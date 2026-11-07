using static OverStrike2.TestKit.T;

namespace OverStrike2.Core.SelfTest;

static class KeyValuesTests
{
    // an appworkshop_730.acf as the Steam client writes it: two maps, their sizes summed in SizeOnDisk
    public const string Acf = "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"SizeOnDisk\"\t\t\"30\"\n\t\"WorkshopItemsInstalled\"\n\t{\n"
                              + "\t\t\"111\"\n\t\t{\n\t\t\t\"size\"\t\t\"10\"\n\t\t\t\"timeupdated\"\t\t\"1000\"\n\t\t\t\"manifest\"\t\t\"7\"\n\t\t}\n"
                              + "\t\t\"222\"\n\t\t{\n\t\t\t\"size\"\t\t\"20\"\n\t\t\t\"timeupdated\"\t\t\"2000\"\n\t\t\t\"manifest\"\t\t\"8\"\n\t\t}\n\t}\n"
                              + "\t\"WorkshopItemDetails\"\n\t{\n\t\t\"111\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"7\"\n\t\t}\n"
                              + "\t\t\"222\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"8\"\n\t\t}\n\t}\n}\n";

    public static void Run()
    {
        Check("kv: an acf comes back byte-identical through Parse and Dump", KeyValues.Dump(KeyValues.Parse(Acf)) == Acf);
        Check("kv: Installed reads the ids and their timeupdated", KeyValues.Installed(Acf) is var inst && inst.Count == 2 && inst["111"] == 1000 && inst["222"] == 2000);
        Check("kv: Sizes reads the installed sizes", KeyValues.Sizes(Acf) is var sz && sz["111"] == 10 && sz["222"] == 20);
        Check("kv: Sizes of a text that does not parse is empty", KeyValues.Sizes("\"a\" {").Count == 0);
        Check("kv: Manifests reads the installed manifests, and nothing of a text that does not parse",
              KeyValues.Manifests(Acf) is var mf && mf.Count == 2 && mf["111"] == "7" && mf["222"] == "8" && KeyValues.Manifests("\"a\" {").Count == 0);
        Check("kv: a block without a key is refused", () => Throws(() => KeyValues.Parse("{ \"a\" \"b\" }")));
        Check("kv: a key without a value is refused", () => Throws(() => KeyValues.Parse("\"a\"")));
        Check("kv: an unbalanced block is refused", () => Throws(() => KeyValues.Parse("\"a\" { \"b\" \"c\" } }")));
        Check("kv: a truncated block is refused", () => Throws(() => KeyValues.Parse("\"a\" { \"b\" \"c\"")));
        Check("kv: escaped quotes stay in a value", KeyValues.Parse("\"a\" \"say \\\"hi\\\"\"")[0].Value == "say \\\"hi\\\"");

        var entries = KeyValues.Entries(Acf, "111");
        Check("kv: Entries - one map's installed and details values", entries is { } e && e.Installed["size"] == "10" && e.Installed["timeupdated"] == "1000"
              && e.Details["manifest"] == "7");
        Check("kv: Entries of a map the acf does not name is null", KeyValues.Entries(Acf, "333") is null && KeyValues.Entries("nope {", "111") is null);

        // a hand-over taken in, a map dropped: id order, SizeOnDisk summed again, nothing else touched
        var inst150 = new List<(string, string)> { ("size", "5"), ("timeupdated", "3000"), ("manifest", "9") };
        var det150 = new List<(string, string)> { ("manifest", "9"), ("timeupdated", "3000"), ("timetouched", "3001"), ("latest_timeupdated", "3000"), ("latest_manifest", "9") };
        var edited = KeyValues.Edit(Acf, new Dictionary<string, (List<(string, string)>, List<(string, string)>)> { ["150"] = (inst150, det150) }, ["222"]);
        Check("kv: Edit adds a hand-over's two entries in id order, drops the other map's, SizeOnDisk 10 + 5",
              edited is { } n && KeyValues.Installed(n) is var i2 && i2.Count == 2 && i2["111"] == 1000 && i2["150"] == 3000
              && n.Contains("\"SizeOnDisk\"\t\t\"15\"", StringComparison.Ordinal) && n.IndexOf("\"111\"", StringComparison.Ordinal) < n.IndexOf("\"150\"", StringComparison.Ordinal)
              && n.Contains("\"latest_manifest\"\t\t\"9\"", StringComparison.Ordinal) && !n.Contains("\"222\"", StringComparison.Ordinal)
              && KeyValues.Dump(KeyValues.Parse(n)) == n);
        Check("kv: Edit refuses a text it cannot reproduce byte for byte", KeyValues.Edit(Acf.Replace("\t\t\"730\"", " \"730\""), null, ["111"]) is null);
        Check("kv: Edit refuses a text without an AppWorkshop block", KeyValues.Edit("\"Other\"\n{\n}\n", null, ["111"]) is null);
        var big = KeyValues.Edit(Acf, new Dictionary<string, (List<(string, string)>, List<(string, string)>)> { ["1000000000"] = (inst150, []) }, null);
        Check("kv: ids sort as numbers, not as text (222 before 1000000000)", big is { } b && b.IndexOf("\"222\"", StringComparison.Ordinal) < b.IndexOf("\"1000000000\"", StringComparison.Ordinal));
        var forgot = KeyValues.Forget(Acf, ["111"]);
        Check("kv: Forget drops a map from both sections", KeyValues.Installed(forgot) is var i3 && i3.Count == 1 && i3.ContainsKey("222")
              && KeyValues.Entries(forgot, "111") is null && !forgot.Contains("\"111\"", StringComparison.Ordinal));
        Check("kv: Forget refuses a text without an AppWorkshop block", () => Throws(() => KeyValues.Forget("\"Other\"\n{\n}\n", ["1"])));
    }

    static bool Throws(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }
}
