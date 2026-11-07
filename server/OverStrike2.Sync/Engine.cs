using System.Text;

namespace OverStrike2.Sync;

/// <summary>What A2S says of the test engine, as a test load reads it.</summary>
public sealed record EngineInfo(string Map, int Players, int Bots, int Humans);

/// <summary>The new lines of a log file since the last look.</summary>
public interface ILines
{
    List<string> Lines();
}

/// <summary>The test copy of the server, as the steps use it: a test stands in its own.</summary>
public interface ITestEngine
{
    int Starts { get; set; }
    ILines? Tail { get; }
    bool Alive();
    (bool Ok, string Why) Start();
    void Stop();
    string? Rcon(string command);
    EngineInfo? A2s();
}

/// <summary>A log file's new lines: where it ended at the start, from there on; read again from its start when it shrank.</summary>
public sealed class Tail : ILines
{
    readonly string path;
    long position;
    byte[] buffer = [];

    public Tail(string path)
    {
        this.path = path;
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
            var info = new FileInfo(path);
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
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Seek(position, SeekOrigin.Begin);
            data = new byte[Math.Min(size - position, 8 << 20)];
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
