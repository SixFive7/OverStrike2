// The sync's self-test: the scan: what a download holds, and its advert rules.
using System.Text;
using System.Text.Json.Nodes;
using static OverStrike2.TestKit.T;
using OverStrike2.Core;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The scan: a map, a pack, an empty or a CS:GO-era upload; the advert rules; the VPK, LZ4 and KV3 readers - with the builders of
/// their fixtures.</summary>
static class ScanTests
{
    /// <summary>A valid LZ4 block of literals only: compressor stand-in for the test.</summary>
    public static byte[] Lz4Literals(byte[] data)
    {
        var result = new List<byte>();
        var lit = data.Length;
        result.Add((byte)((lit >= 15 ? 15 : lit) << 4));
        if (lit >= 15)
        {
            var n = lit - 15;
            while (n >= 255)
            {
                result.Add(255);
                n -= 255;
            }
            result.Add((byte)n);
        }
        result.AddRange(data);
        return result.ToArray();
    }

    static byte[] U32(long v) => BitConverter.GetBytes((uint)v);
    static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);

    /// <summary>A VPK v1 holding `files` {path: bytes}, data inline (archive 0x7fff).</summary>
    public static byte[] VpkOf(params (string Path, byte[] Body)[] files)
    {
        var byExt = new List<(string Ext, List<(string Folder, List<(string Stem, byte[] Body)> Items)> Folders)>();
        foreach (var (path, body) in files)
        {
            var folder = path[..path.LastIndexOf('/')];
            var name = path[(path.LastIndexOf('/') + 1)..];
            var stem = name[..name.LastIndexOf('.')];
            var ext = name[(name.LastIndexOf('.') + 1)..];
            var e = byExt.FindIndex(x => x.Ext == ext);
            if (e < 0)
            {
                byExt.Add((ext, []));
                e = byExt.Count - 1;
            }
            var folders = byExt[e].Folders;
            var f = folders.FindIndex(x => x.Folder == folder);
            if (f < 0)
            {
                folders.Add((folder, []));
                f = folders.Count - 1;
            }
            folders[f].Items.Add((stem, body));
        }
        var tree = new List<byte>();
        var data = new List<byte>();
        foreach (var (ext, folders) in byExt)
        {
            tree.AddRange(Encoding.UTF8.GetBytes(ext));
            tree.Add(0);
            foreach (var (folder, items) in folders)
            {
                tree.AddRange(Encoding.UTF8.GetBytes(folder));
                tree.Add(0);
                foreach (var (stem, body) in items)
                {
                    tree.AddRange(Encoding.UTF8.GetBytes(stem));
                    tree.Add(0);
                    tree.AddRange(U32(0));
                    tree.AddRange(U16(0));
                    tree.AddRange(U16(0x7FFF));
                    tree.AddRange(U32(data.Count));
                    tree.AddRange(U32(body.Length));
                    tree.AddRange(U16(0xFFFF));
                    data.AddRange(body);
                }
                tree.Add(0);
            }
            tree.Add(0);
        }
        tree.Add(0);
        return [.. U32(0x55AA1234), .. U32(1), .. U32(tree.Count), .. tree, .. data];
    }

    public static byte[] Kv3Resource(byte[] text)
    {
        var body = new byte[120];
        "\u00053VK"u8.CopyTo(body);
        U32(1).CopyTo(body, 20);
        var packed = Lz4Literals(text);
        U32(text.Length).CopyTo(body, 72);
        U32(packed.Length).CopyTo(body, 76);
        var kv3 = body.Concat(packed).ToArray();
        var head = new byte[16];
        U32(8).CopyTo(head, 8);
        U32(1).CopyTo(head, 12);
        return [.. head, .. "DATA"u8, .. U32(8), .. U32(kv3.Length), .. kv3];
    }

    /// <summary>A KV3 v4 entity lump the older way (release 197's formats): a header of `header` bytes, then LZ4 in chained frames of 16 KB - each
    /// frame here a block of literals of its own, so a lump past 16 KB crosses a frame's end - inside a resource as Kv3Resource's.</summary>
    public static byte[] Kv3ResourceV4(byte[] text, int header)
    {
        var body = new byte[header];
        "\u00043VK"u8.CopyTo(body);
        U32(1).CopyTo(body, 20);
        U16(16384).CopyTo(body, 26);
        var packed = new List<byte>();
        for (var at = 0; at < text.Length; at += 16384)
            packed.AddRange(Lz4Literals(text[at..Math.Min(text.Length, at + 16384)]));
        return InResource([.. body, .. packed]);
    }

    /// <summary>A KV3 v5 entity lump in Zstandard: the 120-byte header, then one frame.</summary>
    public static byte[] Kv3ResourceZstd(byte[] text)
    {
        var body = new byte[120];
        "\u00053VK"u8.CopyTo(body);
        U32(2).CopyTo(body, 20);
        using var packer = new ZstdSharp.Compressor(3);
        return InResource([.. body, .. packer.Wrap(text).ToArray()]);
    }

    static byte[] InResource(byte[] kv3)
    {
        var head = new byte[16];
        U32(8).CopyTo(head, 8);
        U32(1).CopyTo(head, 12);
        return [.. head, .. "DATA"u8, .. U32(8), .. U32(kv3.Length), .. kv3];
    }

    /// <summary>A map script (cs_script) as the two advert maps of 2026-11-09 had theirs: compiled, its source kept as text in the resource, a promo
    /// said in chat at every round start (the code made up).</summary>
    static readonly byte[] AdvertScript = Encoding.UTF8.GetBytes("RED2\0DATA\0import { Instance } from \"cs_script/point_script\"\n"
        + "const MESAJ = \"(18+) Claim 3 FREE CASES & 200% DEPOSIT BONUS at Clash.gg | Use code TESTCODE to claim!\"\n"
        + "Instance.OnRoundStart(() => Instance.ServerCommand(`say ${MESAJ}`))\n");

    // ---- the scan: the advert rules
    internal static void ScanChecks(string root)
    {
        var cmds = Scan.CommandsIn("say \"Hey guys! Want to open some free cases? Use code PICHET on Clash.gg for 3 FREE cases\"\0m_x"u8.ToArray());
        Check("scan: a Clash.gg promo is a gambling ad", Scan.AdvertOf(cmds, false).Category == "gambling ad");
        Check("scan: 'say connect <ip>' is a chat advert", Scan.AdvertOf(Scan.CommandsIn("say connect 203.0.113.6:27015"u8.ToArray()), false).Category == "chat advert");
        Check("scan: a bind to connect with a client-command entity is a client connect",
              Scan.AdvertOf(Scan.CommandsIn("bind w \"connect 203.0.113.52:27215; bind w +forward\""u8.ToArray()), true).Category == "client connect");
        Check("scan: a bind to connect run on the server only is not listed",
              Scan.AdvertOf(Scan.CommandsIn("bind w \"connect 203.0.113.52:27215\""u8.ToArray()), false).Category == "server connect");
        Check("scan: a bare connect with a client-command entity is a client connect", Scan.AdvertOf(Scan.CommandsIn("connect 203.0.113.4:27015"u8.ToArray()), true).Category == "client connect");
        Check("scan: a bare server-side connect is not listed (CS2 refuses it)", Scan.AdvertOf(Scan.CommandsIn("connect 203.0.113.4:27015"u8.ToArray()), false).Category == "server connect");
        Check("scan: an ordinary map has none", Scan.AdvertOf(Scan.CommandsIn("say \"Leave a like if you enjoyed the map !\""u8.ToArray()), false) == (null, null));
        var blob = Enumerable.Repeat("say connect 203.0.113.4:27015\0point_clientcommand\0"u8.ToArray(), 3).SelectMany(x => x).ToArray();
        Check("scan: the LZ4 reader decodes a literal block", Scan.Lz4Block(Lz4Literals(blob), blob.Length).SequenceEqual(blob));
        byte[] match = [0x41, .. "abcd"u8, 4, 0];               // 4 literals, then a match of 5 at offset 4
        Check("scan: the LZ4 reader copies an overlapping match", Scan.Lz4Block(match, 64).SequenceEqual("abcdabcda"u8.ToArray()));
        // a fake KV3 v5 entity lump inside a resource, inside a map VPK, inside the item's VPK
        var resource = Kv3Resource(blob);
        Check("scan: Kv3Strings finds the commands in a v5 LZ4 entity lump", Encoding.UTF8.GetString(Scan.Kv3Strings(resource)).Contains("say connect 203.0.113.4"));
        var inner = VpkOf(("maps/adtest/entities/default_ents.vents_c", Kv3Resource("connect 203.0.113.4:27015\0point_clientcommand\0"u8.ToArray())));
        var itemDir = Path.Combine(root, "item1");
        Directory.CreateDirectory(itemDir);
        File.WriteAllBytes(Path.Combine(itemDir, "1_dir.vpk"), VpkOf(("maps/adtest.vpk", inner), ("materials/x.vmat_c", "m"u8.ToArray())));
        var s = Scan.Item(itemDir);
        Check("scan: a map VPK is found and named", Node.Str(s["verdict"]) == "map" && Node.StrList(s["maps"]) is ["adtest"]);
        Check("scan: its entity lump's connect with a client-command entity makes it a client connect", Node.Str(s["advert"]) == "client connect");
        var itemDir2 = Path.Combine(root, "item1b");
        Directory.CreateDirectory(itemDir2);
        File.WriteAllBytes(Path.Combine(itemDir2, "1b_dir.vpk"), VpkOf(("maps/adtest.vpk", VpkOf(("maps/adtest/entities/default_ents.vents_c", resource)))));
        Check("scan: a 'say connect' in the entity lump is a chat advert", Node.Str(Scan.Item(itemDir2)["advert"]) == "chat advert");
        var packDir = Path.Combine(root, "item2");
        Directory.CreateDirectory(packDir);
        File.WriteAllBytes(Path.Combine(packDir, "2_dir.vpk"), VpkOf(("models/a/b.vmdl_c", "x"u8.ToArray()), ("models/a/c.vmdl_c", "y"u8.ToArray())));
        Check("scan: models and no map is a pack", Node.Str(Scan.Item(packDir)["verdict"]) == "pack");
        var emptyDir = Path.Combine(root, "item3");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllBytes(Path.Combine(emptyDir, "3_dir.vpk"), VpkOf(("materials/a/b.vmat_c", "x"u8.ToArray())));
        Check("scan: no map and no models is empty", Node.Str(Scan.Item(emptyDir)["verdict"]) == "empty");
        // ---- release 197: the older entity-lump formats, and map scripts ----
        var filler = Enumerable.Range(0, 900).SelectMany(i => Encoding.UTF8.GetBytes($"classname\0info_target\0targetname\0t{i}\0origin\0{i}.0 0.0 0.0\0")).ToArray();
        var lump = filler.Concat("Command\0say connect 203.0.113.9:27015\0point_servercommand\0"u8.ToArray()).ToArray();
        Check($"scan: a KV3 v4 entity lump in chained LZ4 frames ({lump.Length} bytes, past one 16 KB frame), behind a header of 76 or of 98 bytes, reads whole",
              new[] { 76, 98 }.All(h => Scan.Kv3Strings(Kv3ResourceV4(lump, h)).AsSpan().IndexOf("say connect 203.0.113.9:27015"u8) >= 0) && lump.Length > 16384);
        Check("scan: a KV3 entity lump in Zstandard reads too", Encoding.UTF8.GetString(Scan.Kv3Strings(Kv3ResourceZstd(lump))).Contains("say connect 203.0.113.9:27015"));
        Check("scan: the chained LZ4 reader stops at what it cannot decode, and keeps what it had", Scan.Lz4Frames([0x41, .. "abcd"u8, 9, 0], 0, 16384, 64).SequenceEqual("abcd"u8.ToArray()));
        var v4Dir = Path.Combine(root, "item5");
        Directory.CreateDirectory(v4Dir);
        File.WriteAllBytes(Path.Combine(v4Dir, "5_dir.vpk"), VpkOf(("maps/older.vpk", VpkOf(("maps/older/entities/default_ents.vents_c", Kv3ResourceV4(lump, 76))))));
        Check("scan: an older map's 'say connect' in a v4 lump is a chat advert now - before release 197 its lump was read as raw bytes", Node.Str(Scan.Item(v4Dir)["advert"]) == "chat advert");
        var scriptDir = Path.Combine(root, "item6");
        Directory.CreateDirectory(scriptDir);
        File.WriteAllBytes(Path.Combine(scriptDir, "6_dir.vpk"), VpkOf(("maps/fpsmap.vpk", inner), ("scripts/mesaj.vjs_c", AdvertScript)));
        var scripted = Scan.Item(scriptDir);
        Check("scan: a map script's gambling promo is a gambling ad, over what the map's entity lump says (aim_redline_fps and awp_lego_fps, 2026-11-09)",
              Node.Str(scripted["advert"]) == "gambling ad" && Node.Str(scripted["advert_sample"]).Contains("Clash.gg", StringComparison.Ordinal) && Node.StrList(scripted["maps"]) is ["fpsmap"]);
        var nestedDir = Path.Combine(root, "item7");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllBytes(Path.Combine(nestedDir, "7_dir.vpk"), VpkOf(("maps/fpsmap.vpk", VpkOf(("maps/fpsmap/entities/default_ents.vents_c", Kv3Resource("classname\0worldspawn\0"u8.ToArray())),
            ("scripts/vscripts/mesaj.vts_c", AdvertScript)))));
        Check("scan: so is one inside the map's own package, and a plain map with neither has none",
              Node.Str(Scan.Item(nestedDir)["advert"]) == "gambling ad" && Scan.Item(packDir)["advert"] is null);

        var legacyDir = Path.Combine(root, "item4");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllBytes(Path.Combine(legacyDir, "4_legacy.bin"), "x"u8.ToArray());
        Check("scan: only a _legacy.bin is a CS:GO-era upload", Node.Str(Scan.Item(legacyDir)["verdict"]) == "legacy");
        SpawnChecks(root);
    }

    // ---- the spawn points (Kv3.cs, Spawns.cs): a KV3 entity lump read, the counts as Wingman takes them ----

    /// <summary>A KV3 block of version 1, uncompressed, of a value - the encoder of what Kv3.Read reads, for the tests: objects (a list of keys and values),
    /// arrays, strings, booleans, numbers and blobs, each byte in the stream the reader takes it from, in its order: a value's type in the types, then what it
    /// holds - an object's count, then each member's type, its key's string number and its own bytes. A real lump (version 4 or 5, LZ4 or Zstandard) is never
    /// committed: it is a game file; the port was held to ValveResourceFormat's reading of 450 Workshop maps instead (the release's note).</summary>
    /// <summary>For the encoder: a typed array of nulls - its count among the 4-byte values, its element type once, no bytes per value.</summary>
    sealed record TypedNulls(int Count);

    static byte[] Kv3V1(object? root)
    {
        var strings = new List<string>();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        List<byte> b1 = [], b4 = [], b8 = [], types = [];
        int Id(string str) { if (!ids.TryGetValue(str, out var i)) { i = strings.Count; strings.Add(str); ids[str] = i; } return i; }
        static byte TypeOf(object? v) => v switch
        {
            null => 1, true => 13, false => 14, string => 6, long => 3, int => 11, double => 5, byte[] => 7, List<object?> => 8, List<(string, object?)> => 9, TypedNulls => 10,
            _ => throw new ArgumentException($"no KV3 encoding for {v.GetType().Name}"),
        };
        void Emit(object? v) { types.Add(TypeOf(v)); Payload(v); }
        void Payload(object? v)
        {
            switch (v)
            {
                case string str: b4.AddRange(BitConverter.GetBytes(Id(str))); break;
                case long l: b8.AddRange(BitConverter.GetBytes(l)); break;
                case int i: b4.AddRange(BitConverter.GetBytes(i)); break;
                case double d: b8.AddRange(BitConverter.GetBytes(d)); break;
                case byte[] blob: b4.AddRange(BitConverter.GetBytes(blob.Length)); b1.AddRange(blob); break;
                case TypedNulls nulls: b4.AddRange(BitConverter.GetBytes(nulls.Count)); types.Add(1); break;
                case List<object?> list:
                    b4.AddRange(BitConverter.GetBytes(list.Count));
                    foreach (var x in list) Emit(x);
                    break;
                case List<(string Key, object? Value)> obj:
                    b4.AddRange(BitConverter.GetBytes(obj.Count));
                    foreach (var (k, x) in obj)
                    {
                        types.Add(TypeOf(x));
                        b4.AddRange(BitConverter.GetBytes(Id(k)));
                        Payload(x);
                    }
                    break;
            }
        }
        Emit(root);
        var buffer = new List<byte>(b1);
        while (buffer.Count % 4 != 0) buffer.Add(0);
        buffer.AddRange(BitConverter.GetBytes(strings.Count)); // the strings count leads the 4-byte values
        buffer.AddRange(b4);
        while (buffer.Count % 8 != 0) buffer.Add(0);
        buffer.AddRange(b8);
        foreach (var str in strings) { buffer.AddRange(Encoding.UTF8.GetBytes(str)); buffer.Add(0); }
        buffer.AddRange(types);
        buffer.AddRange(U32(0xFFEEDD00));
        return [.. U32(0x4B563301), .. new byte[16], .. U32(0), .. U32((uint)b1.Count), .. U32((uint)(1 + b4.Count / 4)), .. U32((uint)(b8.Count / 8)), .. U32((uint)buffer.Count), .. buffer];
    }

    static List<(string, object?)> Obj(params (string, object?)[] members) => [.. members];

    /// <summary>An entity of the newer form: keyValues3Data, version 1, its values.</summary>
    static List<(string, object?)> Ent(params (string, object?)[] values) => Obj(("keyValues3Data", Obj(("version", 1), ("values", Obj(values)))), ("m_connections", new List<object?>()));

    /// <summary>An entity of the older form: m_keyValuesData, its keys MurmurHash2 hashes, here every value a CString.</summary>
    static List<(string, object?)> OldEnt(params (string Key, string Value)[] values)
    {
        var blob = new List<byte>();
        blob.AddRange(U32(1)); blob.AddRange(U32((uint)values.Length)); blob.AddRange(U32(0));
        foreach (var (k, v) in values) { blob.AddRange(U32(Spawns.Murmur2(k))); blob.AddRange(U32(0x1e)); blob.AddRange(Encoding.UTF8.GetBytes(v)); blob.Add(0); }
        return Obj(("m_keyValuesData", blob.ToArray()), ("m_connections", new List<object?>()));
    }

    static byte[] Lump(params List<(string, object?)>[] entities) => InResource(Kv3V1(Obj(("m_entityKeyValues", entities.Select(e => (object?)e).ToList()))));

    static void SpawnChecks(string root)
    {
        var plain = Lump(Ent(("classname", "worldspawn")), Ent(("classname", "info_player_terrorist")), Ent(("classname", "info_player_terrorist")),
            Ent(("classname", "info_player_counterterrorist"), ("enabled", true)), Ent(("classname", "info_player_counterterrorist"), ("enabled", false)),
            Ent(("classname", "info_player_counterterrorist"), ("startdisabled", true)), Ent(("classname", "info_player_counterterrorist")));
        var ents = Spawns.Entities(plain);
        Check($"spawns: a KV3 entity lump read back - {ents.Count} entities, their keys and values", ents.Count == 7 && ents.All(e => e.ContainsKey("classname")));
        Check("spawns: every enabled team spawn point counts - one with enabled false or startdisabled does not: 2 T, 2 CT", Spawns.Of(ents) is { T: 2, Ct: 2, TwoVersusTwo: false });
        var twoVersusTwo = Lump(Ent(("classname", "info_player_terrorist")), Ent(("classname", "info_player_terrorist")), Ent(("classname", "info_player_terrorist")),
            Ent(("classname", "info_player_counterterrorist")), Ent(("classname", "info_player_terrorist"), ("targetname", "[PR#]spawnpoints.2v2"), ("startdisabled", true)),
            Ent(("classname", "info_player_counterterrorist"), ("targetname", "spawnpoints.2v2"), ("startdisabled", true)));
        Check("spawns: a map with Wingman's own set (spawnpoints.2v2, a prefab's name in front or not) counts only that set, disabled at the start as Valve's are: 1 a side",
              Spawns.Of(Spawns.Entities(twoVersusTwo)) is { T: 1, Ct: 1, TwoVersusTwo: true });
        var old = Lump(OldEnt(("classname", "info_player_terrorist")), OldEnt(("classname", "info_player_counterterrorist"), ("targetname", "ct_a")), OldEnt(("classname", "light")));
        Check("spawns: the older entity form (m_keyValuesData, its keys MurmurHash2 hashes) reads too: 1 T, 1 CT", Spawns.Of(Spawns.Entities(old)) is { T: 1, Ct: 1 });
        Check("spawns: MurmurHash2 with Valve's seed names the older form's keys as ValveResourceFormat's StringToken does (its values, taken 2026-11-09), whatever the case",
              Spawns.Murmur2("classname") == 3323665506 && Spawns.Murmur2("targetname") == 1094168427 && Spawns.Murmur2("startdisabled") == 2777094460
              && Spawns.Murmur2("enabled") == 4052476635 && Spawns.Murmur2("ClassName") == 3323665506);
        var threw = false;
        try { Kv3.Read("not a KV3 block at all"u8.ToArray()); } catch (FormatException) { threw = true; }
        Check("spawns: what is no KV3 block is refused as such, never read as one", threw);
        Check("kv3: a typed array of constants reads, with no bytes per value", Kv3.Read(Kv3V1(new TypedNulls(3))) is List<object?> { Count: 3 } three && three.All(x => x is null));
        static string? Refusal(byte[] block, long maxValues)
        {
            try { Kv3.Read(block, maxValues: maxValues); return null; }
            catch (FormatException e) { return e.Message; }
        }
        var billions = Kv3V1(new TypedNulls(int.MaxValue));   // 2147483647 values in a block of a few dozen bytes
        var refused = Refusal(billions, Kv3.MaxValues);
        Check($"kv3: a block asking for more values than any lump holds (2147483647 nulls in {billions.Length} bytes) is refused at {Kv3.MaxValues}",
              refused is not null && refused.Contains("values") && Refusal(billions, 1000) is { } small && small.Contains("1000"));

        var dir = Path.Combine(root, "item8");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "8_dir.vpk"), VpkOf(("maps/wm_duel.vpk", VpkOf(("maps/wm_duel/entities/default_ents.vents_c", plain))),
            ("maps/wm_duel_skybox.vpk", VpkOf(("maps/wm_duel_skybox/entities/default_ents.vents_c", Lump(Ent(("classname", "worldspawn"))))))));
        var item = Scan.Item(dir);
        Check("spawns: an item's scan says its spawn points, of the map with the most - not its skybox's",
              Node.Get(item, "spawns") is JsonObject s && Node.Int(s["t"]) == 2 && Node.Int(s["ct"]) == 2 && Node.Str(s["set"]) == "all" && item["spawns_error"] is null);
        var broken = Path.Combine(root, "item9");
        Directory.CreateDirectory(broken);
        File.WriteAllBytes(Path.Combine(broken, "9_dir.vpk"), VpkOf(("maps/bad.vpk", VpkOf(("maps/bad/entities/default_ents.vents_c", Kv3Resource("classname\0worldspawn\0"u8.ToArray()))))));
        var bad = Scan.Item(broken);
        Check("spawns: a lump that does not read leaves the counts at none, and says why (spawns_error) - the rest of the scan as ever",
              Node.Get(bad, "spawns") is JsonObject b && Node.Int(b["t"]) == 0 && Node.Int(b["ct"]) == 0 && bad["spawns_error"] is not null && Node.Str(bad["verdict"]) == "map");
    }
}
