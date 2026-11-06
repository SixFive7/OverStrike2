using System.Text.Json;
using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.Server.SelfTest.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>THE MAPS FOLDER (N3), the acf edit (K2), THE HAND-OVER and THE DELETION (N4) and a keep set that cannot be computed (KM). Links
/// and renames on one file system: the checks need Linux, and say SKIP elsewhere.</summary>
static class OrganizeTests
{
    public static void Run()
    {
        // K2. acf edit: hand-overs added, maps dropped, SizeOnDisk the sum again, every section in id order; a file it cannot reproduce: none
        List<(string, string)> inst = [("size", "5"), ("timeupdated", "3000"), ("manifest", "9")];
        List<(string, string)> det = [("manifest", "9"), ("timeupdated", "3000"), ("timetouched", "3001"), ("latest_timeupdated", "3000"), ("latest_manifest", "9")];
        var edited = KeyValues.Edit(WorkshopTests.Acf, new Dictionary<string, (List<(string, string)>, List<(string, string)>)> { ["150"] = (inst, det) }, ["222"]);
        Check("K2: acf_edit adds a hand-over's two entries in id order, drops the other map's, SizeOnDisk 10 + 5",
              edited is not null && KeyValues.Installed(edited) is { Count: 2 } i && i["111"] == 1000 && i["150"] == 3000 && edited.Contains("\"SizeOnDisk\"\t\t\"15\"")
              && edited.IndexOf("\"111\"", StringComparison.Ordinal) < edited.IndexOf("\"150\"", StringComparison.Ordinal) && edited.Contains("\"latest_manifest\"\t\t\"9\"")
              && !edited.Contains("\"222\"") && KeyValues.Dump(KeyValues.Parse(edited)) == edited);
        Check("K2: acf_edit refuses a file it cannot reproduce byte for byte",
              KeyValues.Edit(WorkshopTests.Acf.Replace("\t\t\"730\"", " \"730\""), null, ["111"]) is null);
        LinuxOnly(Folders);
        LinuxOnly(HandOverAndDeletion);
    }

    /// <summary>A block of checks that needs Linux: elsewhere its checks say SKIP (what it builds on may fail there).</summary>
    static void LinuxOnly(Action block)
    {
        if (OperatingSystem.IsLinux())
        {
            block();
            return;
        }
        Skipping = true;
        try
        {
            block();
        }
        catch (Exception)
        {
        }
        finally
        {
            Skipping = false;
        }
    }

    static bool Skipping;

    static void C(string name, Func<bool> ok)
    {
        if (Skipping)
        {
            Skips++;
            Console.WriteLine("SKIP " + name);
        }
        else
            Check(name, ok);
    }

    static string Contents(string root) => Path.Combine(root, "game", "bin", "linuxsteamrt64", "steamapps", "workshop", "content", "730");

    static void Item(string content, string id, string text = "x")
    {
        Directory.CreateDirectory(Path.Combine(content, id));
        File.WriteAllText(Path.Combine(content, id, id + "_dir.vpk"), text);
    }

    static ServerConfig Config(string root, string data, string hud) => Scenario.Copy(Scenario.Template, new ServerPaths
    {
        GameDir = root,
        BridgeData = data,
        Handover = Path.Combine(root, "handover"),
        Depotcache = Path.Combine(root, "depotcache"),
        Plugins = Path.Combine(root, "plugins"),
    }) is var c ? Tune(c, hud) : null!;

    static ServerConfig Tune(ServerConfig c, string hud)
    {
        c.HudAddon = hud;
        return c;
    }

    /// <summary>N3. the maps folder: real folders per type, links in content/730, new downloads moved in, nothing lost.</summary>
    static void Folders()
    {
        var tree = Dir("orgtree");
        var content = Contents(tree);
        Directory.CreateDirectory(content);
        var maps = Path.Combine(tree, "maps");
        foreach (var id in new[] { "100", "200", "300", "400", "500", "600" })
            Item(content, id);
        var data = Dir("orgdata");
        File.WriteAllText(Path.Combine(data, "maps.json"), "[{\"rank\": 1, \"id\": 100, \"name\": \"De Dust 2 (remake)!\", \"sizeMb\": 5}, {\"rank\": 2, \"id\": 400, \"name\": \"My Map\", \"sizeMb\": 1}]");
        File.WriteAllText(Path.Combine(data, "maps-deathmatch.json"), "[{\"rank\": 1, \"id\": 200, \"name\": \"Bastion\", \"sizeMb\": 5}, {\"rank\": 2, \"id\": 100, \"name\": \"De Dust 2 (remake)!\", \"sizeMb\": 5}]");
        File.WriteAllText(Path.Combine(data, "maps-wingman.json"), "[{\"rank\": 1, \"id\": 300, \"name\": \"\\u0420\\u0443\\u0441\\u0441\\u043a\\u0438\\u0439\", \"sizeMb\": 5}]");
        File.WriteAllText(Path.Combine(data, "ours.json"), "{\"maps\": [{\"id\": 400, \"name\": \"My Map\"}]}");
        var log = new Log("test", SystemClock.Instance, Scenario.Paris, _ => { });
        var mf = new MapsFolder(Config(tree, data, "500"), log, SystemClock.Instance, Scenario.Paris);
        var line = mf.Organize();
        string Look(string id) => Path.GetFullPath(Path.Combine(content, MapsFolder.LinkTarget(Path.Combine(content, id)) ?? "?"));
        C($"organize: an Arms Race map, also on Deathmatch, is Arms Race's ({line})", () => Look("100") == Path.Combine(maps, "armsrace", "100-de_dust_2_remake"));
        C("organize: Deathmatch, Wingman with no ASCII in its title, own before the list, the HUD, not listed",
          () => Look("200") == Path.Combine(maps, "deathmatch", "200-bastion") && Look("300") == Path.Combine(maps, "wingman", "300")
                && Look("400") == Path.Combine(maps, "own", "400-my_map") && Look("500") == Path.Combine(maps, "hud", "500-hud"));
        C("organize: the link is relative, the file readable through it",
          () => !Path.IsPathRooted(MapsFolder.LinkTarget(Path.Combine(content, "100"))!) && File.ReadAllText(Path.Combine(content, "100", "100_dir.vpk")) == "x");
        C("organize: an item no list names goes to other", () => Look("600") == Path.Combine(maps, "other", "600"));
        C("organize: the index lists them", () => File.ReadAllText(Path.Combine(maps, "INDEX.tsv")).Contains("armsrace\t100\tDe Dust 2 (remake)!"));
        C("organize: a second run changes nothing", () => mf.Organize() == "maps folder: in order");
        File.Delete(Path.Combine(content, "100"));
        Item(content, "100", "new");
        line = mf.Organize();
        C($"organize: Steam's real folder (an update replaced the link) replaces the old copy ({line})",
          () => File.ReadAllText(Path.Combine(maps, "armsrace", "100-de_dust_2_remake", "100_dir.vpk")) == "new" && MapsFolder.IsLink(Path.Combine(content, "100")));
        Item(content, "700");
        mf.Organize();
        C("organize: a new download goes to other until the lists name it", () => Look("700") == Path.Combine(maps, "other", "700"));
        File.WriteAllText(Path.Combine(data, "maps-wingman.json"), "[{\"rank\": 1, \"id\": 700, \"name\": \"Now Listed\", \"sizeMb\": 1}]");
        line = mf.Organize();
        C($"organize: moved when the lists name it ({line})", () => Look("700") == Path.Combine(maps, "wingman", "700-now_listed") && !Directory.Exists(Path.Combine(maps, "other", "700")));
        File.Delete(Path.Combine(content, "200"));
        line = mf.Organize();
        C($"organize: a folder that lost its link gets it back ({line})", () => Look("200") == Path.Combine(maps, "deathmatch", "200-bastion"));
        Directory.Delete(Path.Combine(maps, "deathmatch", "200-bastion"), true);
        line = mf.Organize();
        C($"organize: a link to nothing goes - Steam fetches the map again when it is wanted ({line})", () => !MapsFolder.Lexists(Path.Combine(content, "200")));
        var fresh = Dir("orgfresh");
        var freshLine = new MapsFolder(Config(fresh, data, "500"), log, SystemClock.Instance, Scenario.Paris).Organize();
        C($"organize: a new volume, no Steam folder yet: its line and an empty index, not a stop ({freshLine})",
          () => !freshLine.Contains("stopped") && File.ReadAllText(Path.Combine(fresh, "maps", "INDEX.tsv")) == "type\tid\ttitle\tMB\n" && !Directory.Exists(Contents(fresh)));
        var none = new MapsFolder(Config(tree, Path.Combine(tree, "nothing-here"), "500"), log, SystemClock.Instance, Scenario.Paris);
        C("organize: no lists, nothing touched", () => none.Organize().Contains("not readable"));
        var off = Config(tree, data, "500");
        off.MapsLayout = "off";
        C("organize: maps.layout off leaves Steam's layout alone", () => new MapsFolder(off, log, SystemClock.Instance, Scenario.Paris).Organize().StartsWith("maps folder: off"));
    }

    static void Put(string folder, string id, string text = "x")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + "_dir.vpk"), text);
    }

    /// <summary>N4. organize with the keep set: a hand-over taken in; the deletion as a dry run, then for real, never the protected maps;
    /// refused when the keep set is not to be trusted. KM: a keep set that cannot be computed deletes nothing.</summary>
    static void HandOverAndDeletion()
    {
        var t4 = Dir("tree4");
        var content = Contents(t4);
        var ws = Path.GetDirectoryName(Path.GetDirectoryName(content))!;
        var maps = Path.Combine(t4, "maps");
        var hand = Path.Combine(t4, "handover");
        var depot = Path.Combine(t4, "depotcache");
        var data = Path.Combine(t4, "data");
        foreach (var d in new[] { content, maps, hand, depot, data })
            Directory.CreateDirectory(d);
        string[] items = ["100", "101", "102", "103", "150", "400", "500"];
        foreach (var id in items.Where(x => x != "150"))
            Put(Path.Combine(content, id), id);
        var acf = "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"SizeOnDisk\"\t\t\"0\"\n\t\"WorkshopItemsInstalled\"\n\t{\n"
                  + string.Concat(items.Where(x => x != "150").Select(w => $"\t\t\"{w}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"{1L << 30}\"\n\t\t\t\"timeupdated\"\t\t\"1000\"\n\t\t\t\"manifest\"\t\t\"7{w}\"\n\t\t}}\n"))
                  + "\t}\n\t\"WorkshopItemDetails\"\n\t{\n"
                  + string.Concat(items.Where(x => x != "150").Select(w => $"\t\t\"{w}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"7{w}\"\n\t\t}}\n")) + "\t}\n}\n";
        var ratings = new JsonObject
        {
            ["version"] = 1,
            ["fetched"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600,
            ["scores"] = JsonNode.Parse("{\"100\": [0.1, 1, 0, 9], \"101\": [0.05, 1, 0, 1], \"102\": [0.2, 1, 0, 50], \"150\": [0.9, 1, 0, 3]}"),
            ["trend"] = JsonNode.Parse("{\"armsrace\": [\"102\"], \"deathmatch\": [\"150\"], \"wingman\": []}"),
        };
        var files = new Dictionary<string, string>
        {
            ["maps.json"] = "[{\"rank\": 1, \"id\": 100, \"name\": \"Up Voted\"}, {\"rank\": 2, \"id\": 101, \"name\": \"Not Kept\"}, {\"rank\": 3, \"id\": 102, \"name\": \"Queued\"}]",
            ["maps-deathmatch.json"] = "[{\"rank\": 1, \"id\": 150, \"name\": \"Top Rated\"}]",
            ["maps-wingman.json"] = "[]",
            ["broken.json"] = "{}",
            ["removed.json"] = "{\"verdicts\": {}}",
            ["votes.json"] = "{\"100\": {\"[U:1:1]\": 1}, \"101\": {\"[U:1:1]\": -1}}",
            ["ours.json"] = "{\"maps\": [{\"id\": 400, \"name\": \"Own\"}]}",
            ["state.json"] = "{\"crashes\": {}}",
            ["ratings.json"] = ratings.ToJsonString(),
            ["queues.json"] = "{\"version\": 1, \"random\": {\"armsrace-new\": [\"102\"], \"armsrace-original\": [\"102\"], \"deathmatch\": [\"150\"], \"wingman\": [\"de_debris\"]}, \"requested\": []}",
        };
        void WriteData(Dictionary<string, string>? over = null)
        {
            foreach (var (name, text) in files)
                File.WriteAllText(Path.Combine(data, name), over is not null && over.TryGetValue(name, out var o) ? o : text);
        }
        WriteData();
        File.WriteAllText(Path.Combine(ws, "appworkshop_730.acf"), acf);
        var config = Config(t4, data, "500");
        config.MapsDelete = "dryrun";
        config.KeepRules = new KeepRules { Top = 1, MinTop = 1, MinQueue = 1 };
        var log = new Log("test", SystemClock.Instance, Scenario.Paris, _ => { });
        MapsFolder Mf() => new(config, log, SystemClock.Instance, Scenario.Paris);
        var stateDir = config.Paths.StateDir;
        List<string> Stored() => [.. MapsFolder.MapTypes.Where(k => Directory.Exists(Path.Combine(maps, k)))
            .SelectMany(k => Directory.EnumerateFileSystemEntries(Path.Combine(maps, k))).Select(p => Path.GetFileName(p).Split('-', 2)[0]).OrderBy(x => x, StringComparer.Ordinal)];
        List<string> Lists() => Directory.Exists(stateDir) ? [.. Directory.EnumerateFiles(stateDir, "deleted-*").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).Select(x => x!)] : [];

        var line = Mf().Organize();
        var lists = Lists();
        C($"N4: a dry run lists the maps outside the keep set and deletes nothing ({line})",
          () => Stored().SequenceEqual(items.Where(x => x != "150")) && lists.Count > 0 && lists[^1].EndsWith("-dryrun.tsv")
                && File.ReadAllLines(Path.Combine(stateDir, lists[^1])).Skip(1).SequenceEqual(["armsrace\t101\tNot Kept\t1073", "other\t103\t103\t1073"]));
        var keep = JsonNode.Parse(File.ReadAllText(config.Paths.KeepFile))!;
        C("N4: keep.json: the parts, their sizes, the delete mode, no problems",
          () => keep["delete"]!.GetValue<string>() == "dryrun" && keep["problems"]!.AsArray().Count == 0
                && keep["parts"]!["up"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["100"])
                && keep["parts"]!["top deathmatch"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["150"])
                && keep["summary"]!["up"]!["maps"]!.GetValue<int>() == 1 && keep["summary"]!["up"]!["on_disk"]!.GetValue<int>() == 1
                && keep["summary"]!["up"]!["gb_on_disk"]!.GetValue<double>() == 1.0 && keep["summary"]!["up"]!["gb"]!.GetValue<double>() == 1.0
                && keep["summary"]!["top deathmatch"]!["on_disk"]!.GetValue<int>() == 0);
        // a hand-over of 150, complete, with its manifest; one incomplete (no marker); one whose folder does not match its marker
        Put(Path.Combine(hand, "150"), "150", "handed");
        File.WriteAllBytes(Path.Combine(hand, "150.manifest"), "MANIFEST"u8.ToArray());
        var marker = new JsonObject
        {
            ["version"] = 1, ["id"] = "150", ["title"] = "Top Rated", ["files"] = 1, ["bytes"] = 6,
            ["installed"] = new JsonObject { ["size"] = "6", ["timeupdated"] = "3000", ["manifest"] = "9150" },
            ["details"] = new JsonObject { ["manifest"] = "9150", ["timeupdated"] = "3000", ["timetouched"] = "3001", ["latest_timeupdated"] = "3000", ["latest_manifest"] = "9150" },
        };
        File.WriteAllText(Path.Combine(hand, "150.json"), marker.ToJsonString());
        Put(Path.Combine(hand, "160"), "160");
        Put(Path.Combine(hand, "170"), "170", "short");
        var m170 = (JsonObject)marker.DeepClone();
        (m170["id"], m170["bytes"]) = ("170", 999);
        File.WriteAllText(Path.Combine(hand, "170.json"), m170.ToJsonString());
        line = Mf().Organize();
        var acfNow = File.ReadAllText(config.Paths.WorkshopAcf);
        C($"N4: a complete hand-over is taken in: its type's folder, its link, its acf entries, its manifest, its marker gone ({line})",
          () => Path.GetFullPath(Path.Combine(content, MapsFolder.LinkTarget(Path.Combine(content, "150"))!)) == Path.Combine(maps, "deathmatch", "150-top_rated")
                && File.ReadAllText(Path.Combine(content, "150", "150_dir.vpk")) == "handed" && KeyValues.Installed(acfNow).GetValueOrDefault("150") == 3000
                && acfNow.Contains("\"latest_manifest\"\t\t\"9150\"") && File.ReadAllText(Path.Combine(depot, "730_9150.manifest")) == "MANIFEST"
                && !File.Exists(Path.Combine(hand, "150.json")) && !Directory.Exists(Path.Combine(hand, "150")));
        C("N4: a hand-over without its marker, and one whose folder does not match it, are left alone",
          () => Directory.Exists(Path.Combine(hand, "160")) && Directory.Exists(Path.Combine(hand, "170")) && File.Exists(Path.Combine(hand, "170.json")));
        C("N4: the acf was backed up before the edit", () => Directory.EnumerateFiles(ws).Any(n => n.EndsWith("-organize", StringComparison.Ordinal)));
        Put(Path.Combine(hand, "150"), "150", "handed");
        File.WriteAllText(Path.Combine(hand, "150.json"), marker.ToJsonString());
        line = Mf().Organize();
        C($"N4: a hand-over of a map the server has as new already is dropped ({line})",
          () => !Directory.Exists(Path.Combine(hand, "150")) && !File.Exists(Path.Combine(hand, "150.json")) && line.Contains("1 hand-over(s) not needed"));
        config.MapsDelete = "on";
        var oldRatings = (JsonObject)ratings.DeepClone();
        oldRatings["fetched"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5 * 86400;
        WriteData(new() { ["ratings.json"] = oldRatings.ToJsonString() });
        line = Mf().Organize();
        C($"N4: deletion on, but the ratings are 5 days old: refused, nothing deleted ({line})", () => Stored().Contains("101") && Stored().Contains("103") && line.Contains("refused"));
        WriteData();
        line = Mf().Organize();
        acfNow = File.ReadAllText(config.Paths.WorkshopAcf);
        C($"N4: deletion on: the maps outside the keep set go - folder, link, acf entries ({line})",
          () => Stored().SequenceEqual(["100", "102", "150", "400", "500"]) && !MapsFolder.Lexists(Path.Combine(content, "101")) && !MapsFolder.Lexists(Path.Combine(content, "103"))
                && !KeyValues.Installed(acfNow).ContainsKey("101") && !acfNow.Contains("\"103\"") && Lists().Any(n => n.EndsWith("-done.tsv")));
        C("N4: never the own map, the HUD, the queued map or the upvoted one", () => new[] { "100", "102", "400", "500" }.All(Stored().Contains));
        WriteData(new()
        {
            ["votes.json"] = "{}",
            ["queues.json"] = "{\"version\": 1, \"random\": {\"armsrace-new\": [], \"armsrace-original\": [\"102\"], \"deathmatch\": [\"150\"], \"wingman\": [\"de_debris\"]}, \"requested\": []}",
        });
        line = Mf().Organize();
        C($"N4: an empty queue is a reason to refuse: 100 and 102 stay ({line})", () => Stored().Contains("100") && Stored().Contains("102") && line.Contains("refused"));
        Put(Path.Combine(content, "180"), "180");
        Mf().Organize();
        C("N4: CS2_MAPS_DELETE=off-style refusals keep a new download too", () => Stored().Contains("180"));
        // KM. a keep set that cannot be computed (an input of a shape it does not read): it says why, and nothing is deleted
        WriteData(new() { ["ratings.json"] = "[1, 2, 3]" });
        var safe = KeepSet.Safe(KeepSet.Inputs(data), DateTimeOffset.UtcNow, "500", null, config.KeepRules);
        var before = Stored();
        Put(Path.Combine(content, "190"), "190");
        line = Mf().Organize();
        C($"KM: no keep set to trust (its inputs of a shape it does not read): --organize deletes nothing, a new download stays ({line})",
          () => safe.Problems.Count > 0 && before.All(Stored().Contains) && Stored().Contains("190") && line.Contains("refused"));
    }
}
