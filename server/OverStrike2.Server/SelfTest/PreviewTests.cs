using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OverStrike2.Core;
using OverStrike2.TestKit;
using static OverStrike2.TestKit.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>THE PREVIEW (maps.preview): the list of what a start would delete and its keep set, written to a folder of its own - and the maps
/// folder, content/730, the hand-overs and the acf byte for byte and date for date as they were. It needs no links, so it runs everywhere.
/// The tree is set up as a rehearsal sets it, maps.layout off beside maps.preview.</summary>
static class PreviewTests
{
    const string Marker150 = """
        {"version": 1, "id": "150", "title": "Top Rated", "files": 1, "bytes": 6,
         "installed": {"size": "6", "timeupdated": "3000", "manifest": "9150"},
         "details": {"manifest": "9150", "timeupdated": "3000", "timetouched": "3001", "latest_timeupdated": "3000", "latest_manifest": "9150"}}
        """;

    public static void Run()
    {
        // the tree a start with organize's own dry run lists 101 and 103 of (OrganizeTests), with an organized map no list names (104) and a
        // complete hand-over of 150, which a start would take in
        var tree = Dir("preview-tree");
        var content = Path.Combine(tree, "game", "bin", "linuxsteamrt64", "steamapps", "workshop", "content", "730");
        var ws = Path.GetDirectoryName(Path.GetDirectoryName(content))!;
        var maps = Path.Combine(tree, "maps");
        var hand = Path.Combine(tree, "handover");
        foreach (var id in new[] { "100", "101", "102", "103", "400", "500" })
            Put(Path.Combine(content, id), id);                  // new downloads: a start would move them into their type's folders
        Put(Path.Combine(maps, "other", "104"), "104");
        Put(Path.Combine(hand, "150"), "150", "handed");
        File.WriteAllBytes(Path.Combine(hand, "150.manifest"), "MANIFEST"u8.ToArray());
        File.WriteAllText(Path.Combine(hand, "150.json"), Marker150);
        string[] stored = ["100", "101", "102", "103", "104", "400", "500"];
        File.WriteAllText(Path.Combine(ws, "appworkshop_730.acf"), "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"SizeOnDisk\"\t\t\"0\"\n\t\"WorkshopItemsInstalled\"\n\t{\n"
            + string.Concat(stored.Select(w => $"\t\t\"{w}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"{1L << 30}\"\n\t\t\t\"timeupdated\"\t\t\"1000\"\n\t\t\t\"manifest\"\t\t\"7{w}\"\n\t\t}}\n"))
            + "\t}\n\t\"WorkshopItemDetails\"\n\t{\n" + string.Concat(stored.Select(w => $"\t\t\"{w}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"7{w}\"\n\t\t}}\n")) + "\t}\n}\n");
        var data = Dir("preview-data");
        var ratings = new JsonObject
        {
            ["version"] = 1,
            ["fetched"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600,
            ["scores"] = JsonNode.Parse("{\"100\": [0.1, 1, 0, 9], \"101\": [0.05, 1, 0, 1], \"102\": [0.2, 1, 0, 50], \"150\": [0.9, 1, 0, 3]}"),
            ["trend"] = JsonNode.Parse("{\"armsrace\": [\"102\"], \"deathmatch\": [\"150\"], \"wingman\": []}"),
        };
        foreach (var (name, text) in new Dictionary<string, string>
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
        })
            File.WriteAllText(Path.Combine(data, name), text);
        var outDir = Dir("preview-out");
        var said = new List<string>();
        var log = new Log("test", SystemClock.Instance, Scenario.Paris, said.Add);
        var config = Config(tree, data, outDir);
        string Organize(ServerConfig c) => new MapsFolder(c, log, SystemClock.Instance, Scenario.Paris).Organize();

        var before = Snapshot(tree);
        var line = Organize(config);
        Check($"preview: the maps folder, content/730, the hand-over and the acf are byte for byte and date for date as they were - nothing moved, linked, taken in, written or backed up ({line})",
              Snapshot(tree) == before && before.Contains("F handover") && before.Contains("appworkshop_730.acf"));
        Check("preview: no INDEX.tsv, no acf backup, no type folder made, nothing in update-watch",
              !File.Exists(Path.Combine(maps, "INDEX.tsv")) && !Directory.EnumerateFiles(ws).Any(f => f.Contains(".bak-", StringComparison.Ordinal))
              && !Directory.Exists(Path.Combine(maps, "armsrace")) && !Directory.Exists(Path.Combine(tree, "update-watch")));
        var lists = Directory.Exists(outDir) ? Directory.GetFiles(outDir, "deleted-*-preview.tsv") : [];
        var rows = lists.Length == 1 ? File.ReadAllLines(lists[0]) : [];
        Check($"preview: the list of what a start would delete - the rows of a start's own list on this tree (101, 103), the organized map no list names (104); never the own map, the HUD, a queued map or the hand-over ({Show(rows.Skip(1))})",
              rows.SequenceEqual(["type\tid\ttitle\tMB", "armsrace\t101\tNot Kept\t1073", "other\t103\t103\t1073", "other\t104\t104\t1073"]));
        var keep = Read(Path.Combine(outDir, "keep-preview.json"));
        Check("preview: keep-preview.json - the keep set's parts, the delete mode, no problems, what a start would do, the hand-over it would take in",
              keep is JsonObject k && Node.Str(k["delete"]) == "on" && Node.Arr(k["problems"]).Count == 0 && Node.Str(k["would"]) == "DELETE them"
              && Node.Int(k["outside"]) == 3 && Node.Eq(k["takeIn"], JsonNode.Parse("[\"150\"]")) && Node.Str(k["preview"]).StartsWith("what a start", StringComparison.Ordinal)
              && Node.Arr(k["parts"]?["top deathmatch"]).Select(x => Node.Str(x)).SequenceEqual(["150"]));
        Check("preview: only its two files in its folder", Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal)
              .SequenceEqual(new[] { "keep-preview.json", Path.GetFileName(lists.FirstOrDefault() ?? "?") }.OrderBy(x => x, StringComparer.Ordinal)));
        Check($"preview: its lines say PREVIEW, what a start would do, and where the list is ({said.FirstOrDefault(l => l.StartsWith("keep set", StringComparison.Ordinal))})",
              line.StartsWith("maps folder: " + MapsFolder.PreviewWords, StringComparison.Ordinal) && line.Contains("take in 1 hand-over(s) and find 3 map(s) outside the keep set")
              && line.Contains("it would DELETE them") && line.Contains(outDir) && said.Any(l => l.StartsWith("keep set (preview): ", StringComparison.Ordinal)));
        var dry = Organize(config with { MapsDelete = "dryrun" });
        Check($"preview: with maps.delete dryrun a start would list them only, and the tree is still as it was ({dry})",
              dry.Contains("it would list them only (maps.delete dryrun)") && Snapshot(tree) == before);
        // paths.preview empty: preview/ in the game's update-watch, and nothing else
        var home = Organize(config with { Paths = config.Paths with { Preview = "" } });
        var homeDir = Path.Combine(tree, "update-watch", "preview");
        Check($"preview: with paths.preview empty its two files go to update-watch/preview, and the rest of the tree is as it was ({home})",
              Snapshot(tree, "update-watch") == before && File.Exists(Path.Combine(homeDir, "keep-preview.json"))
              && Directory.GetFiles(homeDir, "deleted-*-preview.tsv").Length == 1 && Directory.GetFileSystemEntries(Path.Combine(tree, "update-watch")).Length == 1);
        // a build that predates maps.preview sees only maps.layout off: it leaves everything alone, so a rehearsal of it previews nothing and
        // changes nothing
        var old = Organize(config with { MapsPreview = false });
        Check($"preview: maps.layout off without maps.preview - what a build that predates the key does - changes nothing either ({old})",
              old.StartsWith("maps folder: off", StringComparison.Ordinal) && Snapshot(tree, "update-watch") == before);
        // the switch: maps.preview and paths.preview, from the settings file
        var s = Settings.Parse("{ \"schema\": 1, \"hud\": { \"addon\": \"500\" }, \"maps\": { \"layout\": \"off\", \"preview\": true }, \"paths\": { \"preview\": \"/rehearsal/maps-preview\" } }", Part.Server, _ => null);
        var fromFile = ServerConfig.From(s, _ => null);
        var plain = ServerConfig.From(Settings.Parse("{ \"schema\": 1, \"hud\": { \"addon\": \"500\" } }", Part.Server, _ => null), _ => null);
        Check("preview: the settings - maps.preview and paths.preview; by default no preview, the layout folders, the preview's folder update-watch/preview",
              s.Problems.Count == 0 && fromFile.MapsPreview && fromFile.MapsLayout == "off" && fromFile.Paths.PreviewDir == "/rehearsal/maps-preview"
              && !plain.MapsPreview && plain.MapsLayout == "folders" && plain.Paths.PreviewDir == Path.Combine(plain.Paths.StateDir, "preview"));
    }

    static ServerConfig Config(string root, string data, string preview) => Scenario.Template with
    {
        Paths = new ServerPaths
        {
            GameDir = root,
            BridgeData = data,
            Handover = Path.Combine(root, "handover"),
            Depotcache = Path.Combine(root, "depotcache"),
            Plugins = Path.Combine(root, "plugins"),
            Preview = preview,
        },
        HudAddon = "500",
        MapsLayout = "off",
        MapsPreview = true,
        MapsDelete = "on",
        KeepRules = new KeepRules { Top = 1, MinTop = 1, MinQueue = 1 },
    };

    static void Put(string folder, string id, string text = "x")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + "_dir.vpk"), text);
    }

    static JsonNode? Read(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;

    /// <summary>Every folder, file and link under `root`, but `skip` (a top folder): a folder with its write time, a file with its size, write
    /// time and bytes (a hash), a link with its target - the tree as one string.</summary>
    static string Snapshot(string root, string? skip = null)
    {
        var sb = new StringBuilder();
        var all = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", all).OrderBy(x => x, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, path);
            if (skip is not null && (rel == skip || rel.StartsWith(skip + Path.DirectorySeparatorChar)))
                continue;
            var info = new FileInfo(path);
            if (info.LinkTarget is { } target)
                sb.Append($"L {rel} -> {target}\n");
            else if (Directory.Exists(path))
                sb.Append($"D {rel} {Directory.GetLastWriteTimeUtc(path).Ticks}\n");
            else
                sb.Append($"F {rel} {info.Length} {info.LastWriteTimeUtc.Ticks} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}\n");
        }
        return sb.ToString();
    }
}
