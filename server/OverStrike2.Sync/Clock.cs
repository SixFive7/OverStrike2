using System.Globalization;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>A SIGTERM on its way: thrown out of the next wait, so every step's clean-up runs (SteamCMD told to quit, the test engine
/// stopped, the status file saying idle). Never caught as an error.</summary>
public sealed class StopRequested() : Exception("the service is stopping (SIGTERM)");

/// <summary>The service's own clock: the system's, whose waits end at once when the service is told to stop.</summary>
public sealed class StoppableClock : IClock
{
    public static readonly StoppableClock Instance = new();
    readonly ManualResetEventSlim stopping = new(false);

    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public bool Stopping => stopping.IsSet;

    public void Stop() => stopping.Set();

    public void Sleep(TimeSpan span)
    {
        if (stopping.IsSet || (span > TimeSpan.Zero && stopping.Wait(span)))
            throw new StopRequested();
    }

    /// <summary>Throws when the service is stopping: a check point inside a long loop.</summary>
    public void Check()
    {
        if (stopping.IsSet)
            throw new StopRequested();
    }
}

/// <summary>Local time for the restarts' times and the day's name, on a clock a test can move, in unix seconds.</summary>
public sealed class SyncClock(IClock clock, TimeZoneInfo zone)
{
    public IClock Clock { get; } = clock;
    public TimeZoneInfo Zone { get; } = zone;

    public static double Seconds(DateTimeOffset t) => (t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
    public static DateTimeOffset At(double t) => DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(t * TimeSpan.TicksPerSecond));

    public double Now() => Seconds(Clock.Now);

    public DateTimeOffset Local(double? ts = null) => TimeZoneInfo.ConvertTime(At(ts ?? Now()), Zone);

    public string Day(double? ts = null) => Local(ts).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string Hm(double? ts = null) => Local(ts).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A local time as the reports write it, yyyy-MM-ddTHH:mm:ss.</summary>
    public string Stamp(double? ts = null) => Local(ts).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>That day's hh:mm (local) as a unix time.</summary>
    public double AtTime(TimeOfDay hhmm, double? ts = null)
    {
        var local = Local(ts);
        return Seconds(LocalTime.At(DateOnly.FromDateTime(local.DateTime), hhmm, Zone));
    }

    public void Sleep(double seconds)
    {
        if (seconds > 0)
            Clock.Sleep(TimeSpan.FromSeconds(seconds));
        else if (Clock is StoppableClock s)
            s.Check();
    }
}

/// <summary>One line per event to the container log (stdout) and to &lt;state&gt;/sync.log (10 MB, then .1), each with the local time - and
/// the same line to a test's list.</summary>
public sealed class SyncLog(SyncClock clock)
{
    readonly object gate = new();
    public List<string> Lines { get; } = [];
    public bool Echo { get; set; } = true;
    public string? File { get; set; }

    public void Line(string message)
    {
        lock (gate)
        {
            Lines.Add(message);
            if (Lines.Count > 20000)
                Lines.RemoveRange(0, 10000);
            if (!Echo)
                return;
            var line = $"os2-sync: {clock.Local().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}";
            Console.Out.WriteLine(line);
            Console.Out.Flush();
            if (File is { } path)
            {
                try
                {
                    if (System.IO.File.Exists(path) && new FileInfo(path).Length > (10 << 20))
                        System.IO.File.Move(path, path + ".1", true);
                    System.IO.File.AppendAllText(path, line + "\n");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
