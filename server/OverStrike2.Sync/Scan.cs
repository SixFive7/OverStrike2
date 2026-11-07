using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>One entry of a VPK's directory: its path, the archive it is in (0x7FFF: the directory file itself), where, how long, the bytes
/// preloaded in the directory, and where the directory's data starts.</summary>
public sealed record VpkEntry(string Path, int Archive, long Offset, long Length, byte[] Preload, long DataStart);

/// <summary>A VPK on disk (a _dir.vpk with its _NNN.vpk archives, or a single .vpk), or one held in memory.</summary>
public sealed partial class Vpk
{
    public const uint Signature = 0x55AA1234;
    readonly string? path;
    readonly byte[]? data;
    public List<VpkEntry> Entries { get; }

    [GeneratedRegex("_dir\\.vpk$")] private static partial Regex DirSuffix();

    public Vpk(string path)
    {
        this.path = path;
        using var f = File.OpenRead(path);
        var head = new byte[12];
        if (f.ReadAtLeast(head, 12, false) < 12)
        {
            Entries = [];
            return;
        }
        var sig = BinaryPrimitives.ReadUInt32LittleEndian(head);
        var ver = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        var tree = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        if (sig != Signature || ver is not (1 or 2) || tree > (256 << 20))
        {
            Entries = [];
            return;
        }
        f.Seek(0, SeekOrigin.Begin);
        var want = (ver == 2 ? 28 : 12) + (int)tree;
        var buf = new byte[want];
        var got = f.ReadAtLeast(buf, want, false);
        Entries = Tree(buf.AsSpan(0, got).ToArray());
    }

    public Vpk(byte[] data)
    {
        this.data = data;
        Entries = Tree(data);
    }

    /// <summary>The C string at pos, and the position after its terminator; throws FormatException when it has none.</summary>
    static (string, int) CStr(byte[] buf, int pos)
    {
        var end = Array.IndexOf(buf, (byte)0, pos);
        if (end < 0)
            throw new FormatException("no terminator");
        return (Encoding.UTF8.GetString(buf, pos, end - pos), end + 1);
    }

    /// <summary>The entries of a VPK v1/v2 directory held in `buf` (its head).</summary>
    public static List<VpkEntry> Tree(byte[] buf)
    {
        if (buf.Length < 12)
            return [];
        var sig = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        var ver = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        var tree = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8));
        if (sig != Signature || ver is not (1 or 2))
            return [];
        var pos = ver == 2 ? 28 : 12;
        long dataStart = pos + (long)tree;
        var result = new List<VpkEntry>();
        try
        {
            while (true)
            {
                string ext;
                (ext, pos) = CStr(buf, pos);
                if (ext.Length == 0)
                    break;
                while (true)
                {
                    string folder;
                    (folder, pos) = CStr(buf, pos);
                    if (folder.Length == 0)
                        break;
                    while (true)
                    {
                        string name;
                        (name, pos) = CStr(buf, pos);
                        if (name.Length == 0)
                            break;
                        if (pos + 18 > buf.Length)
                            throw new FormatException("a short entry");
                        var span = buf.AsSpan(pos, 18);
                        var pre = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
                        var arch = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
                        var off = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                        var length = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                        pos += 18;
                        var preload = buf.AsSpan(pos, Math.Max(0, Math.Min(pre, buf.Length - pos))).ToArray();
                        pos += pre;
                        var full = folder.Trim().Length > 0 ? $"{folder}/{name}.{ext}" : $"{name}.{ext}";
                        result.Add(new VpkEntry(full, arch, off, length, preload, dataStart));
                    }
                }
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
        }
        return result;
    }

    /// <summary>An entry's bytes, or null: longer than the limit, or not readable.</summary>
    public byte[]? Read(VpkEntry e, long limit = 64L << 20)
    {
        if (e.Length > limit)
            return null;
        if (data is not null)
        {
            if (e.Archive != 0x7FFF)
                return null;
            var start = Math.Min(e.DataStart + e.Offset, data.Length);
            var end = Math.Min(start + e.Length, data.Length);
            return [.. e.Preload, .. data.AsSpan((int)start, (int)(end - start))];
        }
        string target;
        long at;
        if (e.Archive == 0x7FFF)
            (target, at) = (path!, e.DataStart + e.Offset);
        else
            (target, at) = (DirSuffix().Replace(path!, $"_{e.Archive:000}.vpk"), e.Offset);
        try
        {
            using var f = File.OpenRead(target);
            f.Seek(at, SeekOrigin.Begin);
            var buf = new byte[e.Length];
            var got = f.ReadAtLeast(buf, buf.Length, false);
            return [.. e.Preload, .. buf.AsSpan(0, got)];
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>THE SCAN: what a Workshop download holds - a map, a model pack, an empty upload or a CS:GO-era one - and the commands its
/// entity lump and cfg files carry (connect &lt;ip&gt;, `say connect ...`, gambling promo codes).</summary>
public static partial class Scan
{
    const string Ip = "\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}(?::\\d+)?";
    [GeneratedRegex("^\\s*\"?connect\\s+" + Ip, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex ConnectRx();
    [GeneratedRegex("^\\s*\"?(say|say_team)\\b.*\\bconnect\\b\\s*\"?\\s*" + Ip, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex SayConnectRx();
    [GeneratedRegex("clash\\.gg|\\bpromo ?code\\b|use code\\b|deposit bonus|free cases", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex AdRx();
    [GeneratedRegex("\\b(bind|alias)\\b[^;]*\\bconnect\\s+" + Ip, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex ClientRx();
    [GeneratedRegex("_\\d{3}\\.vpk$")] private static partial Regex ArchiveName();
    [GeneratedRegex("[;\\n]")] private static partial Regex CommandSplit();

    /// <summary>Decompress one raw LZ4 block (no frame header) into at most `limit` bytes. Throws FormatException for a bad offset or more
    /// output than stated; IndexOutOfRangeException for a block cut short.</summary>
    public static byte[] Lz4Block(byte[] src, long limit)
    {
        var dst = new List<byte>();
        int i = 0, n = src.Length;
        while (i < n)
        {
            var token = src[i++];
            var lit = token >> 4;
            if (lit == 15)
            {
                while (true)
                {
                    var b = src[i++];
                    lit += b;
                    if (b != 255)
                        break;
                }
            }
            var take = Math.Max(0, Math.Min(lit, n - i));
            dst.AddRange(src.AsSpan(i, take).ToArray());
            i += lit;
            if (i >= n)
                break;
            var off = src[i] | (src[i + 1] << 8);
            i += 2;
            if (off == 0 || off > dst.Count)
                throw new FormatException("bad LZ4 offset");
            var ml = token & 15;
            if (ml == 15)
            {
                while (true)
                {
                    var b = src[i++];
                    ml += b;
                    if (b != 255)
                        break;
                }
            }
            ml += 4;
            var start = dst.Count - off;
            if (ml <= off)
                dst.AddRange(dst.GetRange(start, ml));
            else
                for (var k = 0; k < ml; k++)
                    dst.Add(dst[start + k]);
            if (dst.Count > limit)
                throw new FormatException("LZ4 output over its stated size");
        }
        return dst.ToArray();
    }

    static byte[] Slice(byte[] b, long start, long end)
    {
        start = Math.Clamp(start, 0, b.Length);
        end = Math.Clamp(end, start, b.Length);
        return b.AsSpan((int)start, (int)(end - start)).ToArray();
    }

    static (uint, uint) Two(byte[] b, long start)
    {
        var s = Slice(b, start, start + 8);
        if (s.Length != 8)
            throw new FormatException("short");
        return (BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(4)));
    }

    /// <summary>The text a compiled Source 2 resource (an entity lump, .vents_c) carries: the string table of its KV3 DATA block, decompressed
    /// when it is KV3 v5 with LZ4 (the format CS2's compiler writes), else its raw bytes.</summary>
    public static byte[] Kv3Strings(byte[] resource)
    {
        try
        {
            var (blockOffset, blockCount) = Two(resource, 8);
            long pos = 8 + (long)blockOffset;
            byte[]? data = null;
            for (var k = 0; k < Math.Min(blockCount, 32u); k++)
            {
                var kind = Slice(resource, pos, pos + 4);
                var (offset, size) = Two(resource, pos + 4);
                if (kind.AsSpan().SequenceEqual("DATA"u8))
                    data = Slice(resource, pos + 4 + offset, pos + 4 + offset + size);
                pos += 12;
            }
            if (data is null || data.Length < 120)
                return resource;
            if (data.AsSpan(0, 4).SequenceEqual("\u00053VK"u8) && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)) == 1)
            {
                var (u1, c1) = Two(data, 72);
                return Lz4Block(Slice(data, 120, 120 + (long)c1), u1 + 16L);
            }
            return data;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            return resource;
        }
    }

    static bool TextByte(byte b) => b is >= 0x20 and <= 0x7e or >= 0x80 || b == (byte)'\t';

    /// <summary>The commands in a blob: its runs of 4 or more printable bytes, split at ; and line ends, trimmed.</summary>
    public static List<string> CommandsIn(byte[] bytes)
    {
        var result = new List<string>();
        var i = 0;
        while (i < bytes.Length)
        {
            if (!TextByte(bytes[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < bytes.Length && TextByte(bytes[i]))
                i++;
            if (i - start < 4)
                continue;
            var s = Encoding.UTF8.GetString(bytes, start, i - start);
            foreach (var part in CommandSplit().Split(s))
            {
                var p = part.Trim();
                if (p.Length > 0)
                    result.Add(p);
            }
        }
        return result;
    }

    /// <summary>(category, sample), or (null, null):
    ///   'gambling ad'      a gambling or case-site promotion
    ///   'chat advert'      `say ... connect &lt;ip&gt;`: everybody reads it in chat
    ///   'client connect'   a `connect &lt;ip&gt;` the players' own game is made to run (point_clientcommand, a bind or an alias)
    ///   'server connect'   only a server-side `connect &lt;ip&gt;`, which CS2 refuses for Workshop maps: nobody sees it; not listed.</summary>
    public static (string? Category, string? Sample) AdvertOf(IEnumerable<string> commands, bool hasClientCommand)
    {
        var found = new Dictionary<string, string>();
        foreach (var c in commands)
        {
            if (AdRx().IsMatch(c))
                found.TryAdd("gambling ad", c);
            else if (SayConnectRx().IsMatch(c))
                found.TryAdd("chat advert", c);
            else if (ConnectRx().IsMatch(c) || ClientRx().IsMatch(c))
                // a bind or an alias to connect, or a bare connect, reaches a player only through a client-command entity; run on the
                // server it does nothing (validated against a scan of the whole Workshop: 82 of 82 adverts alike)
                found.TryAdd(hasClientCommand ? "client connect" : "server connect", c);
        }
        foreach (var category in new[] { "gambling ad", "chat advert", "client connect", "server connect" })
            if (found.TryGetValue(category, out var sample))
                return (category, Text.Cut(sample, 160));
        return (null, null);
    }

    /// <summary>The item's file names (sorted) and its VPK trees: its _dir.vpk files, else its .vpk files that are no numbered archive.</summary>
    public static (List<string> Names, List<string> Trees) ItemVpks(string folder)
    {
        List<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ([], []);
        }
        var trees = names.Where(n => n.EndsWith("_dir.vpk", StringComparison.Ordinal)).ToList();
        if (trees.Count == 0)
            trees = names.Where(n => n.EndsWith(".vpk", StringComparison.Ordinal) && !ArchiveName().IsMatch(n)).ToList();
        return (names, trees);
    }

    static bool Contains(byte[] hay, ReadOnlySpan<byte> needle) => hay.AsSpan().IndexOf(needle) >= 0;

    /// <summary>What one Workshop download holds. verdict: map | pack | empty | legacy | unknown, maps (the level names in it), models,
    /// files, bytes, names, advert (category) and advert_sample (the command that made it), or none.</summary>
    public static JsonObject Item(string folder)
    {
        var maps = new List<string>();
        long models = 0, files = 0;
        var verdict = "unknown";
        var (names, trees) = ItemVpks(folder);
        var res = new JsonObject
        {
            ["verdict"] = "unknown", ["maps"] = new JsonArray(), ["models"] = 0, ["files"] = 0, ["bytes"] = Files.FolderStats(folder).Bytes,
            ["names"] = Node.Strings(names.Take(30)), ["advert"] = null, ["advert_sample"] = null,
        };
        if (trees.Count == 0)
        {
            if (names.Count > 0 && names.All(n => n.EndsWith("_legacy.bin", StringComparison.Ordinal) || n == "publish_data.txt")
                && names.Any(n => n.EndsWith("_legacy.bin", StringComparison.Ordinal)))
                res["verdict"] = "legacy";
            return res;
        }
        var commands = new List<string>();
        var clientCommand = false;
        var seen = false;
        foreach (var tree in trees)
        {
            Vpk vpk;
            try
            {
                vpk = new Vpk(Path.Combine(folder, tree));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in vpk.Entries)
            {
                seen = true;
                files++;
                var low = entry.Path.ToLowerInvariant();
                if (low.StartsWith("maps/", StringComparison.Ordinal) && low.EndsWith(".vpk", StringComparison.Ordinal) && low.Count(c => c == '/') == 1)
                {
                    maps.Add(entry.Path[5..^4]);                    // its ends checked above, both ASCII
                    var innerBytes = vpk.Read(entry, 1L << 30);
                    if (innerBytes is not { Length: > 0 })
                        continue;
                    var inner = new Vpk(innerBytes);
                    foreach (var ie in inner.Entries)
                    {
                        var il = ie.Path.ToLowerInvariant();
                        if (il.EndsWith(".vents_c", StringComparison.Ordinal) || il.EndsWith(".cfg", StringComparison.Ordinal))
                        {
                            var raw = inner.Read(ie, 32L << 20);
                            if (raw is { Length: > 0 })
                            {
                                var text = il.EndsWith(".vents_c", StringComparison.Ordinal) ? Kv3Strings(raw) : raw;
                                clientCommand = clientCommand || Contains(text, "point_clientcommand"u8) || Contains(text, "point_broadcastclientcommand"u8);
                                commands.AddRange(CommandsIn(text));
                            }
                        }
                    }
                }
                else if (low.EndsWith(".vmdl_c", StringComparison.Ordinal))
                    models++;
                else if (low.EndsWith(".cfg", StringComparison.Ordinal))
                {
                    var raw = vpk.Read(entry, 4L << 20);
                    if (raw is { Length: > 0 })
                        commands.AddRange(CommandsIn(raw));
                }
            }
        }
        if (seen)
            verdict = maps.Count > 0 ? "map" : models > 0 ? "pack" : "empty";
        var (advert, sample) = AdvertOf(commands, clientCommand);
        res["verdict"] = verdict;
        res["maps"] = Node.Strings(maps);
        res["models"] = models;
        res["files"] = files;
        res["advert"] = advert;
        res["advert_sample"] = sample;
        return res;
    }
}
