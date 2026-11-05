using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OverStrike2.Server;

/// <summary>The evidence of a broken download path: the Steam client's logs live in the container's own layer and a recreate loses them,
/// so the moment a process is marked broken they are copied to update-watch/steamlogs/&lt;local time&gt;-&lt;why&gt;/ with a summary.txt (the
/// newest SnapshotKeep are kept).</summary>
public static partial class Snapshots
{
    public const int SnapshotKeep = 10;
    public const long MaxFile = 32L << 20;            // a log bigger than this is copied from its tail

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NotSlug();

    /// <summary>(manifests in the depotcache, is it a mounted volume, writable) - null for the count if unreadable.</summary>
    public static (int? Count, bool Mounted, bool Writable) Depotcache(string dir)
    {
        int? count;
        try
        {
            count = Directory.EnumerateFiles(dir, "*.manifest").Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            count = null;
        }
        return (count, IsMount(dir), Writable(dir));
    }

    /// <summary>Is the folder a mount point (in /proc/self/mountinfo)?</summary>
    public static bool IsMount(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd('/');
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var f = line.Split(' ');
                if (f.Length > 4 && f[4].Replace("\\040", " ") == (full.Length == 0 ? "/" : full))
                    return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    static bool Writable(string dir) => Directory.Exists(dir) && Native.MayWrite(dir);

    /// <summary>What content_log.txt says about the download sources: the lines that tell the stuck state apart.</summary>
    public static List<string> ContentLogSummary(string text)
    {
        (string Label, string Needle)[] keys =
        [
            ("directory lists", "download sources and"), ("manifests OK", "manifest request received 200"), ("HTTP 503", "received 503"),
            ("send failed", "failed to send"), ("dropped (No Connection)", ": No Connection"), ("servers created", "Created download interface"),
            ("No target sources connected", "No target sources connected"),
        ];
        var lines = text.Split('\n');
        var output = keys.Select(k => $"{k.Label}: {lines.Count(l => l.Contains(k.Needle, StringComparison.Ordinal))}").ToList();
        var last = lines.Where(l => l.Contains("download sources and", StringComparison.Ordinal) || l.Contains("Moving to source priority class", StringComparison.Ordinal)).ToList();
        output.AddRange(last.Skip(Math.Max(0, last.Count - 3)).Select(l => "last: " + l.TrimEnd('\r')));
        return output;
    }

    /// <summary>Copy the Steam client's logs for the record; the folder, or null. Never throws: the restart it precedes must not depend on it.</summary>
    public static string? SteamLogs(Watch w, string why, int? pid)
    {
        try
        {
            var slug = NotSlug().Replace(why.ToLowerInvariant(), "-").Trim('-');
            slug = slug.Length > 48 ? slug[..48] : slug;
            if (slug.Length == 0)
                slug = "broken";
            var stamp = TimeZoneInfo.ConvertTime(w.Clock.Now, w.Zone).ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            var target = Path.Combine(w.Config.Paths.SnapshotDir, $"{stamp}-{slug}");
            Directory.CreateDirectory(target);
            var copied = new List<string>();
            var content = "";
            var logs = w.Config.Paths.SteamLogs;
            foreach (var source in Directory.Exists(logs) ? Directory.EnumerateFiles(logs).OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>())
            {
                var name = Path.GetFileName(source);
                if (!name.EndsWith(".txt", StringComparison.Ordinal))
                    continue;
                byte[] data;
                long size;
                using (var f = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    size = f.Length;
                    if (size > MaxFile)
                        f.Seek(size - MaxFile, SeekOrigin.Begin);
                    using var ms = new MemoryStream();
                    f.CopyTo(ms);
                    data = ms.ToArray();
                }
                File.WriteAllBytes(Path.Combine(target, name), data);
                copied.Add($"{name} ({data.Length} bytes{(size > MaxFile ? ", tail" : "")})");
                if (name == "content_log.txt")
                    content = Encoding.UTF8.GetString(data);
            }
            var (count, mounted, writable) = Depotcache(w.Config.Paths.Depotcache);
            var age = pid is { } p ? w.Engine.Age(p) : null;
            var summary = new List<string>
            {
                $"why: {why}",
                $"engine pid: {(pid?.ToString(CultureInfo.InvariantCulture) ?? "None")}, running for {(age is { } a ? Rules.Hms(a) : "?")}",
                $"depotcache {w.Config.Paths.Depotcache}: {(count?.ToString(CultureInfo.InvariantCulture) ?? "None")} manifests, {(mounted ? "a mounted volume" : "NOT a mounted volume")}, {(writable ? "writable" : "NOT writable")}",
                $"copied from {logs}: {(copied.Count > 0 ? string.Join(", ", copied) : "nothing")}",
                "",
                "content_log.txt:",
            };
            summary.AddRange(ContentLogSummary(content).Select(l => "  " + l));
            File.WriteAllText(Path.Combine(target, "summary.txt"), string.Join("\n", summary) + "\n");
            foreach (var old in Directory.EnumerateDirectories(w.Config.Paths.SnapshotDir).OrderBy(x => x, StringComparer.Ordinal).SkipLast(SnapshotKeep).ToList())
                Directory.Delete(old, true);
            w.Log.Line($"Steam logs saved for the record: {target} ({copied.Count} files)");
            return target;
        }
        catch (Exception e)   // evidence is a nice-to-have, the restart is not
        {
            w.Log.Line($"WARNING: could not save the Steam logs ({e.GetType().Name}: {e.Message})");
            return null;
        }
    }
}
