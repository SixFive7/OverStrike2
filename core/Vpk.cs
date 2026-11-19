using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace OverStrike2.Core;

/// <summary>One entry of a VPK's directory: its path, the archive it is in (0x7FFF: the directory file itself), where, how long, the bytes
/// preloaded in the directory, and where the directory's data starts.</summary>
public sealed record VpkEntry(string Path, int Archive, long Offset, long Length, byte[] Preload, long DataStart);

/// <summary>A VPK on disk (a _dir.vpk with its _NNN.vpk archives, or a single .vpk), or one held in memory: the sync's scan reads a Workshop
/// download with it, os2-server the game's own pak01_dir.vpk (GamePools.cs).</summary>
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
