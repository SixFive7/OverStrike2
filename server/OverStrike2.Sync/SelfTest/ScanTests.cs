// The sync's self-test: the scan: what a download holds, and its advert rules.
using System.Text;
using static OverStrike2.Sync.SelfTest.T;

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
        var legacyDir = Path.Combine(root, "item4");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllBytes(Path.Combine(legacyDir, "4_legacy.bin"), "x"u8.ToArray());
        Check("scan: only a _legacy.bin is a CS:GO-era upload", Node.Str(Scan.Item(legacyDir)["verdict"]) == "legacy");
    }
}
