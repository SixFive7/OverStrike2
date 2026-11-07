using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverStrike2.Core;

/// <summary>Careful file writes: a reader sees the old file or the new one, and after a crash the new one is on disk.</summary>
public static class Files
{
    /// <summary>Write beside the target, flush it to disk, rename it over the target, then flush the folder (on Linux), so the rename
    /// itself survives a crash. unixMode: the new file's permissions (Linux), else the umask's. On Windows - builds and self-tests only - something
    /// else can hold the target or the new file for a moment (a scanner, by all signs) and the move fails with "access denied", so it is tried again
    /// there, for 2 s at the most. On Linux a rename does not care who has the file open: one try, and a real failure throws as ever.</summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> data, UnixFileMode? unixMode = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(folder, $".{Path.GetFileName(path)}.tmp-{Environment.ProcessId}");
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(data);
            f.Flush(true);
        }
        if (unixMode is { } mode && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, mode);
        for (var tries = OperatingSystem.IsWindows() ? 100 : 1; ; tries--)
        {
            try
            {
                File.Move(tmp, path, true);
                break;
            }
            catch (Exception e) when (tries > 1 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
        Posix.FsyncFolder(folder);
    }

    public static void WriteAtomic(string path, string text, UnixFileMode? unixMode = null) => WriteAtomic(path, Encoding.UTF8.GetBytes(text), unixMode);

    /// <summary>(files, bytes) of everything under a folder, links not followed: what the sync's hand-over marker names, counted the same
    /// way again by the restart watcher before it takes the hand-over in.</summary>
    public static (long Files, long Bytes) FolderStats(string path)
    {
        long files = 0, total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try
                {
                    total += new FileInfo(f).Length;
                    files++;
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return (files, total);
    }

    /// <summary>Where a link points (as written in it, relative or not), or null: no link, or nothing that can be read.</summary>
    public static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsLink(string path) => LinkTarget(path) != null;

    /// <summary>Whether anything is at the path, a link to nothing included.</summary>
    public static bool Lexists(string path) => IsLink(path) || File.Exists(path) || Directory.Exists(path);

    /// <summary>A folder gone with everything in it, or a link gone - never what it points to. quiet: what cannot go stays, and nothing is
    /// thrown; else the IOException or UnauthorizedAccessException is the caller's.</summary>
    public static void RemoveTree(string path, bool quiet = false)
    {
        try
        {
            if (IsLink(path))
                File.Delete(path);
            else
                Directory.Delete(path, true);
        }
        catch (Exception e) when (quiet && e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A JSON file as a node, or null when it is missing or does not parse.</summary>
    public static JsonNode? ReadJson(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
