using System.Text;

namespace OverStrike2.Core;

/// <summary>The new lines of a log file since the last look.</summary>
public interface ILines
{
    List<string> Lines();
}

/// <summary>A growing file's new lines (console.log, the Steam client's workshop log, a test engine's log): positioned at its end when made,
/// so only what is written after counts; a line not yet ended is kept for the next look, and a file that got shorter (rotated, truncated) is
/// read from its start again. One look reads at most `limit` bytes; the rest comes at the next.</summary>
public sealed class Tail : ILines
{
    readonly int limit;
    long position;
    byte[] buffer = [];

    public string Path { get; }

    public Tail(string path, int limit = 1 << 20)
    {
        Path = path;
        this.limit = limit;
        try
        {
            position = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            position = 0;
        }
    }

    public List<string> Lines()
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
            data = data.AsSpan(0, f.ReadAtLeast(data, data.Length, false)).ToArray();
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
        buffer = all.AsSpan(start).ToArray();
        return lines;
    }
}
