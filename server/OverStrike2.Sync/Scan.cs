using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>THE SCAN: what a Workshop download holds - a map, a model pack, an empty upload or a CS:GO-era one - and the commands its
/// entity lumps, cfg files and map scripts carry (connect &lt;ip&gt;, `say connect ...`, gambling promo codes). An entity lump's text is read in
/// every format found on the server's maps (release 197): KV3 v5 with LZ4, as CS2's compiler writes it now; v4 with LZ4 in chained frames, about
/// a fifth of the lumps, which the scan read as raw bytes before; and Zstandard in either, a few in a hundred. A map script (cs_script, .vjs_c and
/// .vts_c) keeps its source as plain text: aim_redline_fps and awp_lego_fps said a gambling advert in chat at every round start from one, unseen
/// until then.</summary>
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

    /// <summary>LZ4 as KV3 v4 keeps it: blocks of `frame` bytes of output each, chained - a later block may copy from an earlier one - and a block ends
    /// where the output reaches a multiple of the frame after its literals. Decoded from `start` as far as it goes; a bad offset or a block cut short
    /// ends it, and what came out so far is the answer.</summary>
    public static byte[] Lz4Frames(byte[] src, int start, int frame, long limit)
    {
        var dst = new List<byte>();
        int i = start, n = src.Length;
        try
        {
            while (i < n)
            {
                var token = src[i++];
                var lit = token >> 4;
                if (lit == 15)
                    while (true)
                    {
                        var b = src[i++];
                        lit += b;
                        if (b != 255)
                            break;
                    }
                var take = Math.Max(0, Math.Min(lit, n - i));
                dst.AddRange(src.AsSpan(i, take).ToArray());
                i += lit;
                if (i >= n || (frame > 0 && dst.Count > 0 && dst.Count % frame == 0))
                    continue;
                var off = src[i] | (src[i + 1] << 8);
                i += 2;
                if (off == 0 || off > dst.Count)
                    break;
                var ml = token & 15;
                if (ml == 15)
                    while (true)
                    {
                        var b = src[i++];
                        ml += b;
                        if (b != 255)
                            break;
                    }
                ml += 4;
                var at = dst.Count - off;
                for (var k = 0; k < ml; k++)
                    dst.Add(dst[at + k]);
                if (dst.Count > limit)
                    break;
            }
        }
        catch (IndexOutOfRangeException)
        {
        }
        return dst.ToArray();
    }

    /// <summary>Every Zstandard frame in a block, decompressed and joined, each to at most `limit` bytes; a frame that does not decode ends the list.</summary>
    public static byte[] ZstdFrames(byte[] data, long limit)
    {
        var output = new List<byte>();
        var pos = 0;
        while (pos < data.Length)
        {
            var at = data.AsSpan(pos).IndexOf((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]);
            if (at < 0)
                break;
            pos += at;
            try
            {
                using var unpack = new ZstdSharp.DecompressionStream(new MemoryStream(data, pos, data.Length - pos, false), leaveOpen: false);
                var buffer = new byte[64 * 1024];
                long got = 0;
                int read;
                while ((read = unpack.Read(buffer, 0, buffer.Length)) > 0 && got <= limit)
                {
                    output.AddRange(buffer.AsSpan(0, read).ToArray());
                    got += read;
                }
                break; // the stream reads on through every frame after this one
            }
            catch (Exception e) when (e is ZstdSharp.ZstdException or IOException or InvalidDataException or ArgumentException)
            {
                pos += 4;
            }
        }
        return output.ToArray();
    }

    /// <summary>How much an entity lump's decoded bytes look like one: its "classname" keys first, then the printable bytes of its start.</summary>
    static int LumpScore(byte[] bytes)
    {
        var score = 0;
        var key = "classname"u8;
        for (var at = bytes.AsSpan().IndexOf(key); at >= 0; )
        {
            score += 1000;
            var next = bytes.AsSpan(at + key.Length).IndexOf(key);
            at = next < 0 ? -1 : at + key.Length + next;
        }
        return score + bytes.Take(4096).Count(TextByte);
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

    /// <summary>The text a compiled Source 2 resource (an entity lump, .vents_c) carries: its KV3 DATA block decompressed - v5 with LZ4 (the string
    /// table, as CS2's compiler writes it now), v4 with LZ4 in chained frames (its data's start found by trying, the start that reads most like an
    /// entity lump), or Zstandard in either - else its raw bytes.</summary>
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
            var method = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
            if (data.AsSpan(0, 4).SequenceEqual("\u00053VK"u8) && method == 1)
            {
                var (u1, c1) = Two(data, 72);
                return Lz4Block(Slice(data, 120, 120 + (long)c1), u1 + 16L);
            }
            if (method == 2)
                return ZstdFrames(data, 64L << 20) is { Length: > 0 } unpacked ? unpacked : data;
            if (method == 1)
            {
                var frame = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26)) is var f and > 0 ? f : 16384;
                byte[] best = [];
                for (var start = 40; start <= Math.Min(200, data.Length - 1); start++)
                {
                    var tried = Lz4Frames(data, start, frame, 64L << 20);
                    if (LumpScore(tried) > LumpScore(best))
                        best = tried;
                }
                return best.Length > 0 ? best : data;
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

    /// <summary>A map script (cs_script): compiled JavaScript or TypeScript, its source kept as plain text in the resource.</summary>
    static bool IsScript(string lowerPath) => lowerPath.EndsWith(".vjs_c", StringComparison.Ordinal) || lowerPath.EndsWith(".vts_c", StringComparison.Ordinal);

    /// <summary>The largest map package the scan reads, into memory: a map's own .vpk inside the item's. One over it is not read - its commands, its
    /// entity lumps and its spawn points stay unknown (spawns_error "its map is over 1 GiB").</summary>
    public const long InnerMapLimit = 1L << 30;

    /// <summary>What one Workshop download holds. verdict: map | pack | empty | legacy | unknown, maps (the level names in it), models,
    /// files, bytes, names, advert (category) and advert_sample (the command that made it), or none; spawns (its team spawn points, t, ct and
    /// set) and spawns_error (why they are not known, or not all read).</summary>
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
        Spawns.Count? spawns = null;   // the map with the most team spawn points: the one played (a skybox's own map has none)
        string? spawnError = null;
        string? unread = null;         // why a map's own package was not read: then the item's count is not known, so none is given
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
                    if (entry.Length > InnerMapLimit)
                    {
                        unread ??= "its map is over 1 GiB";
                        continue;
                    }
                    var innerBytes = vpk.Read(entry, InnerMapLimit);
                    if (innerBytes is not { Length: > 0 })
                    {
                        unread ??= "its map could not be read";
                        continue;
                    }
                    var inner = new Vpk(innerBytes);
                    var entities = new List<Dictionary<string, object?>>();
                    foreach (var ie in inner.Entries)
                    {
                        var il = ie.Path.ToLowerInvariant();
                        if (IsScript(il))
                        {
                            if (inner.Read(ie, 8L << 20) is { Length: > 0 } script)
                                commands.AddRange(CommandsIn(script));
                        }
                        else if (il.EndsWith(".vents_c", StringComparison.Ordinal) || il.EndsWith(".cfg", StringComparison.Ordinal))
                        {
                            var raw = inner.Read(ie, 32L << 20);
                            if (raw is { Length: > 0 })
                            {
                                if (il.EndsWith(".vents_c", StringComparison.Ordinal))
                                {
                                    // the count is the scan's extra: a lump it cannot read, whatever the error, leaves the counts at none and the verdict as ever
                                    try { entities.AddRange(Spawns.Entities(raw)); }
                                    catch (Exception e) when (e is not StopRequested) { spawnError ??= Text.Cut($"{e.GetType().Name}: {e.Message}", 120); }
                                }
                                var text = il.EndsWith(".vents_c", StringComparison.Ordinal) ? Kv3Strings(raw) : raw;
                                clientCommand = clientCommand || Contains(text, "point_clientcommand"u8) || Contains(text, "point_broadcastclientcommand"u8);
                                commands.AddRange(CommandsIn(text));
                            }
                        }
                    }
                    var counted = Spawns.Of(entities);
                    if (spawns is null || counted.T + counted.Ct > spawns.T + spawns.Ct)
                        spawns = counted;
                }
                else if (low.EndsWith(".vmdl_c", StringComparison.Ordinal))
                    models++;
                else if (IsScript(low))
                {
                    if (vpk.Read(entry, 8L << 20) is { Length: > 0 } script)
                        commands.AddRange(CommandsIn(script));
                }
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
        res["spawns"] = spawns is null || unread is not null ? null : new JsonObject { ["t"] = spawns.T, ["ct"] = spawns.Ct, ["set"] = spawns.TwoVersusTwo ? "2v2" : "all" };
        if ((unread ?? spawnError) is { } why)
            res["spawns_error"] = why;
        return res;
    }
}
