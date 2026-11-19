using System.Text;
using static OverStrike2.TestKit.T;

namespace OverStrike2.Core.SelfTest;

/// <summary>The game's own map pools (GamePools.cs) on a made-up game folder: Valve's text with its comments, a template's groups in the order of their
/// numbers, the install's maps, Valve's names, the whole read out of a VPK, and the pools message both ends of the wire read (BridgeWire.cs).
/// Every map and group name here is made up; nothing of the game's own files is in it.</summary>
static class GamePoolsTests
{
    /// <summary>A gamemodes.txt in the game's own shape: a template whose numbers are not in the file's order, a commented-out group in it, a mode
    /// with its groups named directly, a map in two groups, a condition, a bare token and a mode the file gives no number.</summary>
    const string Modes = """
        // DO NOT MODIFY THIS FILE
        "GameModes.txt"
        {
            "templates#include"
            {
                "pair"
                {
                    "mg_second"     "2"
                    "mg_first"      "1"
                }
                "mixed"
                {
                    "mg_new"        "0"
                    "mg_first"      "1"
                    // "mg_dropped"   "2"
                    "mg_second"     "3"
                }
            }
            "gameTypes"
            {
                "classic"
                {
                    "value"     "0"
                    "gameModes"
                    {
                        "casual"        { "value" "0"   "mapgroupsMP" { "template#include" "pair" } }
                        "scrimcomp2v2"
                        {
                            "value"     "2"
                            "exec" { "exec" "a.cfg"   "exec" "b.cfg" }   // a key twice
                            "mapgroupsMP" { "template#include" "pair" }
                        }
                    }
                }
                "gungame"
                {
                    "value"     "1"
                    "gameModes"
                    {
                        "gungameprogressive" { "value" "0" "mapgroupsMP" { "mg_race" "" } }
                        "deathmatch"
                        {
                            "value"     "2"     [$WIN32]
                            "mapgroupsSP" { "template#include" "pair" }
                            "mapgroupsMP" { "template#include" "mixed" }
                        }
                        "nameless" { "mapgroupsMP" { "mg_race" "" } }
                    }
                }
            }
            "mapgroups"
            {
                "mg_first"   { "name" "mg_first"   "maps" { "de_alpha" ""  "de_beta" "" } }
                "mg_second"  { "name" "mg_second"  "maps" { "cs_gamma" ""  "de_alpha" "" } }
                "mg_new"     { "name" "mg_new"     "maps" { de_newest "" } }
                "mg_dropped" { "name" "mg_dropped" "maps" { "de_gone" "" } }
                "mg_race"    { "name" "mg_race \"night\""    "maps" { "ar_one" ""  "ar_two" "" } }
            }
        }
        """;

    /// <summary>Valve's English names in csgo_english.txt's shape: a byte-order mark, a token twice (the first counts), an [english] marker and an
    /// empty name (neither counts).</summary>
    const string English = "﻿\"lang\"\n{\n\"Language\" \"English\"\n\"Tokens\"\n{\n// maps\n\"SFUI_Map_de_alpha\"\t\t\"Alpha\"\n\"[english]SFUI_Map_de_beta\" \"Not this\"\n"
        + "\"SFUI_Map_de_beta\"  \"Beta Two\"\n\"SFUI_Map_DE_ALPHA\" \"Second\"\n\"SFUI_Map_ar_one\" \"One (Night)\"\n\"SFUI_Map_de_empty\" \"\"\n\"SFUI_Map_de_nowhere\" \"Nowhere\"\n}\n}\n";

    public static void Run()
    {
        Check("game pools: Valve's text - comments to the line's end, conditions skipped, bare tokens, a key twice, an escaped quote", () =>
        {
            var kv = GamePools.ParseText("// head\n\"a\" { \"b\" \"1\" [$X360] // \"c\" \"2\"\n bare \"x \\\"y\\\"\"  \"b\" \"3\" }\n");
            var a = kv.Single().Block!;
            return kv[0].Key == "a" && a.Count == 3 && a[0] is { Key: "b", Value: "1" } && a[1] is { Key: "bare", Value: "x \"y\"" } && a[2] is { Key: "b", Value: "3" };
        });
        Check("game pools: a text that is not whole is refused - a block without a key, a key without a value, a block never closed, a } too many",
            new[] { "{ \"a\" \"b\" }", "\"a\" { \"b\" }", "\"a\" { \"b\" \"c\"", "\"a\" \"b\" }" }.All(t =>
            {
                try { GamePools.ParseText(t); return false; }
                catch (FormatException) { return true; }
            }));
        var pools = GamePools.PoolsIn(Modes);
        GamePools.Pool? Of(string mode) => pools.FirstOrDefault(p => p.Mode == mode);
        Check($"game pools: every mode's online pool - a template's groups by their numbers, a commented-out group none, each map once ({Show(Of("deathmatch")?.Maps ?? [])})",
            Of("deathmatch") is { Type: "gungame", TypeValue: 1, ModeValue: 2 } dm && dm.Maps.SequenceEqual(["de_newest", "de_alpha", "de_beta", "cs_gamma"])
            && Of("scrimcomp2v2") is { TypeValue: 0, ModeValue: 2 } wm && wm.Maps.SequenceEqual(["de_alpha", "de_beta", "cs_gamma"])
            && Of("gungameprogressive") is { TypeValue: 1, ModeValue: 0 } ar && ar.Maps.SequenceEqual(["ar_one", "ar_two"])
            && Of("nameless") is { ModeValue: -1 } && pools.Count == 5 && pools.All(p => p.Missing.Count == 0));

        var names = GamePools.NamesIn(English);
        Check($"game pools: Valve's names - the first value of a token, by the map's name in lower case; an [english] marker and an empty name are none ({names.Count})",
            names.Count == 4 && names["de_alpha"] == "Alpha" && names["DE_BETA"] == "Beta Two" && names["ar_one"] == "One (Night)" && !names.ContainsKey("de_empty"));

        var game = Dir("game-pools");
        void File(string rel, string text) => Put(Path.Combine(game, rel.Replace('/', Path.DirectorySeparatorChar)), text);
        foreach (var installed in new[] { "csgo/maps/de_alpha.vpk", "csgo/maps/de_beta.vpk", "csgo/maps/ar_one.vpk", "csgo/maps/ar_two.vpk", "csgo/maps/de_newest_vanity.vpk",
                     "csgo/maps/workshop_preview_alpha.vpk", "csgo_community_addons/cs_gamma/cs_gamma_dir.vpk", "csgo_community_addons/de_newest/de_newest_000.vpk" })
            File(installed, "VPK");
        File("csgo/maps/de_empty.vpk", "");
        Directory.CreateDirectory(Path.Combine(game, "csgo_community_addons", "de_gone"));
        var found = GamePools.InstalledIn(game);
        Check($"game pools: the installed maps - a non-empty csgo/maps package or a community folder with a package in it; no vanity or preview package, "
              + $"no empty file, no empty folder ({Show(found)})", found.SequenceEqual(["ar_one", "ar_two", "cs_gamma", "de_alpha", "de_beta", "de_newest"]));
        Check("game pools: without a pak01_dir.vpk the read says so, never throws, and keeps what it found",
            GamePools.Read(game) is { Problem: { } p, Pools.Count: 0, Build: "?" } r && p.Contains("pak01_dir.vpk", StringComparison.Ordinal) && r.Installed.Count == 6);

        System.IO.File.WriteAllBytes(Path.Combine(game, "csgo", "pak01_dir.vpk"), VpkOf((GamePools.ModesEntry, Modes), (GamePools.NamesEntry, English), ("scripts/other.txt", "x")));
        File("csgo/steam.inf", "ClientVersion=1\nPatchVersion=1.41.9.4\n");
        var read = GamePools.Read(game);
        var deathmatch = read.Pools.FirstOrDefault(p => p.Mode == "deathmatch");
        Check($"game pools: the whole read out of pak01_dir.vpk - the build, every pool with its maps not installed set apart, Valve's names of the maps it names alone "
              + $"({read.Problem ?? "no problem"}; deathmatch {Show(deathmatch?.Playable ?? [])}, names {Show(read.Names.Keys.Order())})",
            read is { Problem: null, Build: "1.41.9.4", Pools.Count: 5 } && deathmatch is { } d && d.Missing.Count == 0 && d.Playable.Count == 4
            && read.Names.Keys.Order().SequenceEqual(["ar_one", "de_alpha", "de_beta"]));
        System.IO.File.Delete(Path.Combine(game, "csgo_community_addons", "cs_gamma", "cs_gamma_dir.vpk"));
        var gone = GamePools.Read(game).Pools.First(p => p.Mode == "deathmatch");
        Check($"game pools: a map whose package is gone (its folder emptied, as the game did with three maps on 2026-11-18) is missing, out of the playable maps ({Show(gone.Missing)})",
            gone.Missing.SequenceEqual(["cs_gamma"]) && gone.Playable.SequenceEqual(["de_newest", "de_alpha", "de_beta"]));
        Check($"game pools: the maps the game ships as community addons - a community folder with a package in it, no csgo/maps package; one emptied is none any more "
              + $"({Show(read.Addons)}, then {Show(GamePools.AddonsIn(game))})",
            read.Addons.SequenceEqual(["cs_gamma", "de_newest"]) && GamePools.AddonsIn(game).SequenceEqual(["de_newest"]) && GamePools.Read(game).Addons.SequenceEqual(["de_newest"]));

        var body = BridgeWire.PoolsBody(read with { Pools = [.. read.Pools.Select(p => p.Mode == "deathmatch" ? p with { Missing = ["cs_gamma"] } : p)] });
        var back = BridgeWire.ReadPools(body);
        Check($"bridge wire: the pools message - its head with the build, a line a mode, the installed maps, a line a name - read back as it was sent",
            body.StartsWith("os2-pools\t1\t1.41.9.4\npool\tclassic\tcasual\t0\t0\tde_alpha,de_beta,cs_gamma\t\n", StringComparison.Ordinal)
            && body.Contains("\npool\tgungame\tdeathmatch\t1\t2\tde_newest,de_alpha,de_beta,cs_gamma\tcs_gamma\n", StringComparison.Ordinal)
            && body.Contains("\ninstalled\tar_one,ar_two,cs_gamma,de_alpha,de_beta,de_newest\n", StringComparison.Ordinal) && body.EndsWith("\nname\tde_beta\tBeta Two\n", StringComparison.Ordinal)
            && back is { Build: "1.41.9.4", Problem: null } && back.Pools.Count == 5 && back.Pools[3] is { Mode: "deathmatch", TypeValue: 1, ModeValue: 2 } bd
            && bd.Playable.SequenceEqual(["de_newest", "de_alpha", "de_beta"]) && back.Installed.Count == 6 && back.Names["DE_ALPHA"] == "Alpha" && back.Names.Count == 3
            && body.Contains("\ninstalled\tar_one,ar_two,cs_gamma,de_alpha,de_beta,de_newest\naddons\tcs_gamma,de_newest\n", StringComparison.Ordinal)
            && back.Addons.SequenceEqual(["cs_gamma", "de_newest"]));
        Check("bridge wire: a pools message from an os2-server before release 221a has no addons line - none, then",
            BridgeWire.ReadPools("os2-pools\t1\t9\npool\ta\tb\t0\t0\tx\t\ninstalled\tx\n") is { Addons.Count: 0, Installed: ["x"] });
        Check("bridge wire: a pools message is no other message, nor one without a pool; a line it does not know is passed over",
            BridgeWire.ReadPools("os2-ready\t1\npool\ta\tb\t0\t0\tx\t\n") is null && BridgeWire.ReadPools("os2-pools\t1\t9\ninstalled\tx\n") is null
            && BridgeWire.ReadPools("os2-pools\t2\t9\npool\ta\tb\t0\t0\tx\t\n") is null
            && BridgeWire.ReadPools("os2-pools\t1\t9\nlater\tsomething\npool\ta\tb\t0\t0\tx,y\t\n") is { Pools: [{ Maps.Count: 2 }] });
    }

    /// <summary>A VPK v1 with these files stored in the directory file itself (archive 0x7FFF), a file without a folder under the space the format
    /// gives such a one.</summary>
    static byte[] VpkOf(params (string Path, string Text)[] files)
    {
        static byte[] U32(long v) => BitConverter.GetBytes((uint)v);
        static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
        static byte[] Z(string s) => [.. Encoding.UTF8.GetBytes(s), 0];
        var tree = new List<byte>();
        var data = new List<byte>();
        foreach (var byExt in files.GroupBy(f => Path.GetExtension(f.Path).TrimStart('.')))
        {
            tree.AddRange(Z(byExt.Key));
            foreach (var byFolder in byExt.GroupBy(f => Path.GetDirectoryName(f.Path)?.Replace('\\', '/') is { Length: > 0 } folder ? folder : " "))
            {
                tree.AddRange(Z(byFolder.Key));
                foreach (var (path, text) in byFolder)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    tree.AddRange([.. Z(Path.GetFileNameWithoutExtension(path)), .. U32(0), .. U16(0), .. U16(0x7FFF), .. U32(data.Count), .. U32(bytes.Length), .. U16(0xFFFF)]);
                    data.AddRange(bytes);
                }
                tree.Add(0);
            }
            tree.Add(0);
        }
        tree.Add(0);
        return [.. U32(Vpk.Signature), .. U32(1), .. U32(tree.Count), .. tree, .. data];
    }
}
