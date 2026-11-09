// The bridge's log on its data folder as well as on the console (docs/reference.md, "The data files"): one file a day, logs/bridge-YYYY-MM-DD.log,
// every line with its date and time, the last KeepDays days kept. The console's log lives in the bridge's container, and a swap of the bridge
// (os2-deploy bridge swap) removes that container with it; these files stay, so a vote of yesterday can still be looked up after a release.
// Writing one never stops the bridge: a line that cannot be written is lost alone, said once on the console, and the file is tried again later.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

sealed partial class LogFile(string dir, int keepDays = LogFile.KeepDays)
{
    /// <summary>How many days of files stay: today's and the 13 before it.</summary>
    public const int KeepDays = 14;

    /// <summary>After a failure, how long before the file is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    readonly object gate = new();
    StreamWriter? writer;
    DateTime day = DateTime.MinValue, retryAt = DateTime.MinValue;

    /// <summary>The folder the files are in.</summary>
    public string Dir => dir;

    /// <summary>Why the last line could not be written, or null while it works.</summary>
    public string? Problem { get; private set; }

    /// <summary>A day's file name.</summary>
    public static string NameOf(DateTime day) => $"bridge-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log";

    [GeneratedRegex(@"^bridge-(\d{4}-\d{2}-\d{2})\.log$")] private static partial Regex NameRx();

    /// <summary>One line, at this local time: into that day's file - a new day opens a new file and deletes those older than KeepDays.</summary>
    public void Write(DateTime now, string message)
    {
        lock (gate)
        {
            if (Problem != null && now < retryAt) return;
            try
            {
                if (writer == null || now.Date != day)
                {
                    writer?.Dispose();
                    writer = null;
                    Directory.CreateDirectory(dir);
                    var stream = new FileStream(Path.Combine(dir, NameOf(now)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    day = now.Date;
                    Prune(now);
                }
                writer.WriteLine($"{now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}");
                if (Problem != null) { Console.WriteLine($"{now:HH:mm:ss} log file: written again ({Path.Combine(dir, NameOf(now))})"); Problem = null; }
            }
            catch (Exception e)
            {
                writer?.Dispose();
                writer = null;
                if (Problem == null) Console.WriteLine($"{now:HH:mm:ss} log file: {Path.Combine(dir, NameOf(now))} not written ({e.Message}) - the console's log goes on; tried again in {RetryAfter.TotalMinutes:0} minutes");
                Problem = e.Message;
                retryAt = now + RetryAfter;
            }
        }
    }

    /// <summary>Deletes the day files older than KeepDays: only files named like ours, by the date in their names.</summary>
    void Prune(DateTime now)
    {
        var oldest = now.Date.AddDays(-(keepDays - 1));
        foreach (var path in Directory.EnumerateFiles(dir, "bridge-*.log"))
            if (NameRx().Match(Path.GetFileName(path)) is { Success: true } m
                && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < oldest)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The file of today, for the health page: its name, or why it is not written.</summary>
    public string Health(DateTime now)
    {
        lock (gate) return Problem != null ? $"log files: NOT written ({Problem})" : $"log files: {Path.Combine(dir, NameOf(now))}, {keepDays} days kept";
    }

    /// <summary>The file closed (the bridge stops, the self-test cleans up).</summary>
    public void Close()
    {
        lock (gate) { writer?.Dispose(); writer = null; }
    }
}
