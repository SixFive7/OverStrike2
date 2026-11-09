// --selftest, the log's day files (LogFile.cs): what the bridge logs also goes to logs/bridge-YYYY-MM-DD.log under its data folder, dated, a file a day,
// the last LogFile.KeepDays days kept - so a vote of yesterday can be looked up after a swap of the bridge has removed its container and its console log.
static partial class Program
{
    static void LogFileSelfTest(Action<string, bool> Check)
    {
        var dir = Path.GetFullPath($"selftest-logfile-{Environment.ProcessId}");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var day = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Local);
        string FileOf(DateTime d) => Path.Combine(dir, LogFile.NameOf(d));
        string Read(string path) // beside the open writer, as a share's reader does
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        string[] Lines(DateTime d) => Read(FileOf(d)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        static string Stamp(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture); // a line's date and time, as the file writes them
        foreach (var old in new[] { -20, -LogFile.KeepDays, -(LogFile.KeepDays - 1) }) File.WriteAllText(FileOf(day.AddDays(old)), "an old day\n");
        File.WriteAllText(Path.Combine(dir, "bridge-notes.log"), "not ours by its name\n");
        File.WriteAllText(Path.Combine(dir, "other.txt"), "not ours\n");
        var files = new LogFile(dir);
        LogFile? broken = null;
        try
        {
            files.Write(day, "vote: round 1 open");
            files.Write(day.AddHours(13).AddMinutes(59).AddSeconds(59), "vote: Alice [U:1:1001] votes 3");
            Check("Log files: a line goes to its day's file, dated and timed, one line each, the file readable while the bridge writes it",
                LogFile.NameOf(day) == "bridge-2026-11-20.log" && Lines(day) is [var first, var second] && first == $"{Stamp(day)} vote: round 1 open" && second == $"{Stamp(day.AddHours(14).AddSeconds(-1))} vote: Alice [U:1:1001] votes 3");
            Check($"...the first line of a day deletes the day files older than {LogFile.KeepDays} days, by the date in their names, and nothing else",
                !File.Exists(FileOf(day.AddDays(-20))) && !File.Exists(FileOf(day.AddDays(-LogFile.KeepDays))) && File.Exists(FileOf(day.AddDays(-(LogFile.KeepDays - 1))))
                && File.Exists(Path.Combine(dir, "bridge-notes.log")) && File.Exists(Path.Combine(dir, "other.txt")));
            files.Write(day.AddDays(1).AddSeconds(1 - 36000), "vote: round 2 open"); // 00:00:01 the next day
            Check("...a new day opens a new file, and yesterday's stays whole",
                Lines(day.AddDays(1)) is [var third] && third == $"{Stamp(day.Date.AddDays(1).AddSeconds(1))} vote: round 2 open" && Lines(day).Length == 2 && files.Problem == null
                && files.Health(day.AddDays(1)) == $"log files: {FileOf(day.AddDays(1))}, {LogFile.KeepDays} days kept");
            files.Close();
            var blocked = Path.Combine(dir, "other.txt"); // a file where the folder should be: the folder cannot be made
            broken = new LogFile(blocked);
            broken.Write(day, "a line");
            broken.Write(day.AddMinutes(1), "another line");
            Check("...a folder that cannot be written costs nothing but its lines: no exception, the health page says so, and it is tried again later",
                broken.Problem != null && broken.Health(day).StartsWith("log files: NOT written", StringComparison.Ordinal) && File.ReadAllText(blocked) == "not ours\n");
            File.Delete(blocked);
            broken.Write(day.AddMinutes(2), "within the wait: not tried");
            broken.Write(day + LogFile.RetryAfter + TimeSpan.FromMinutes(1), "after it: written");
            Check("...and once it can be, it is written again",
                broken.Problem == null && Directory.Exists(blocked) && Read(Path.Combine(blocked, LogFile.NameOf(day))).EndsWith("after it: written\n", StringComparison.Ordinal)
                && !Read(Path.Combine(blocked, LogFile.NameOf(day))).Contains("within the wait", StringComparison.Ordinal));
            Check("The bridge's own log: the day files from its start (Main), never in --selftest, which tests them here", Files == null);
        }
        finally
        {
            files.Close();
            broken?.Close();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
