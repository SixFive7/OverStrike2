using System.Text;

namespace OverStrike2.Server;

/// <summary>The new lines of a growing file (console.log, the Steam client's workshop log): positioned at its end when made, so only what
/// is written after counts; a file that got shorter (rotated, truncated) is read from its start again.</summary>
public sealed class Tail
{
    public string Path { get; }
    long position;
    byte[] buffer = [];

    public Tail(string path)
    {
        Path = path;
        try
        {
            position = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            position = 0;
        }
    }

    public List<string> Lines(int limit = 1 << 20)
    {
        long size;
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
                return [];
            size = info.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        if (size < position)
            (position, buffer) = (0, []);
        if (size == position)
            return [];
        byte[] data;
        try
        {
            using var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Seek(position, SeekOrigin.Begin);
            data = new byte[Math.Min(size - position, limit)];
            var n = 0;
            while (n < data.Length)
            {
                var got = f.Read(data, n, data.Length - n);
                if (got == 0)
                    break;
                n += got;
            }
            if (n < data.Length)
                Array.Resize(ref data, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        position += data.Length;
        var all = new byte[buffer.Length + data.Length];
        buffer.CopyTo(all, 0);
        data.CopyTo(all, buffer.Length);
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i] != (byte)'\n')
                continue;
            lines.Add(Encoding.UTF8.GetString(all, start, i - start).TrimEnd('\r'));
            start = i + 1;
        }
        buffer = all[start..];
        return lines;
    }
}
