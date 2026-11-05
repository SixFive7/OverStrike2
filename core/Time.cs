using System.Globalization;

namespace OverStrike2.Core;

/// <summary>The time, and waiting for it - what a test replaces with a clock of its own.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    void Sleep(TimeSpan span);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public void Sleep(TimeSpan span)
    {
        if (span > TimeSpan.Zero)
            Thread.Sleep(span);
    }
}

/// <summary>Local wall-clock time: the daily restart's slot, the local day, across the clock changes of summer time.</summary>
public static class LocalTime
{
    /// <summary>The zone the TZ variable names (the containers' own setting), else the system's; UTC when neither is known.</summary>
    public static TimeZoneInfo Zone(string? tz = null)
    {
        tz ??= Environment.GetEnvironmentVariable("TZ");
        if (!string.IsNullOrWhiteSpace(tz))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(tz.Trim().TrimStart(':'));
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>That day at hh:mm in the zone: a time the clock skips (summer time starts) is the one after the gap, a time it shows twice
    /// (summer time ends) its first showing.</summary>
    public static DateTimeOffset At(DateOnly day, TimeOfDay hhmm, TimeZoneInfo tz)
    {
        var local = new DateTime(day.Year, day.Month, day.Day, hhmm.Hour, hhmm.Minute, 0, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
            local = local.AddHours(1);
        var offset = tz.IsAmbiguousTime(local) ? tz.GetAmbiguousTimeOffsets(local).Max() : tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>The first local hh:mm at or after `after`, or null when switched off.</summary>
    public static DateTimeOffset? NextSlot(DateTimeOffset after, TimeOfDay? hhmm, TimeZoneInfo tz)
    {
        if (hhmm is not { } t)
            return null;
        var baseDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, tz).DateTime);
        for (var add = -1; add <= 2; add++)
        {
            var candidate = At(baseDay.AddDays(add), t, tz);
            if (candidate >= after)
                return candidate;
        }
        return null;
    }

    /// <summary>The local day of a moment, yyyy-MM-dd.</summary>
    public static string Day(DateTimeOffset t, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(t, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The local wall-clock time of a moment, HH:mm:ss. A gate names when the restart it waits on happened, never how long ago:
    /// an age changes every second, a moment does not.</summary>
    public static string Clock(DateTimeOffset t, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Is a daily slot due: from today's slot until `window` after it, unless done today (doneDay, the slot's local day).</summary>
    public static bool DailyDue(DateTimeOffset now, TimeOfDay? hhmm, TimeZoneInfo tz, string doneDay, TimeSpan window)
    {
        var slot = NextSlot(now - window, hhmm, tz);
        return slot is { } s && s <= now && now < s + window && doneDay != Day(s, tz);
    }
}

/// <summary>THE STEAM WEB API'S ONE DOOR, per program: its calls go one at a time, each at least the gap after the last one ended
/// (2 s at least, the rule for every Web API caller). Programs do not share it: each keeps its own.</summary>
public sealed class WebApiDoor(double gapSeconds, IClock? clock = null)
{
    public const double MinimumGap = 2.0;
    readonly IClock clock = clock ?? SystemClock.Instance;
    readonly SemaphoreSlim one = new(1, 1);
    readonly TimeSpan gap = TimeSpan.FromSeconds(Math.Max(MinimumGap, gapSeconds));
    DateTimeOffset lastEnded = DateTimeOffset.MinValue;

    public TimeSpan Gap => gap;

    /// <summary>Run a call through the door: wait for any call in flight, sleep out what is left of the gap, call, note when it ended.</summary>
    public T Call<T>(Func<T> call)
    {
        one.Wait();
        try
        {
            if (lastEnded != DateTimeOffset.MinValue)
                clock.Sleep(lastEnded + gap - clock.Now);
            try
            {
                return call();
            }
            finally
            {
                lastEnded = clock.Now;
            }
        }
        finally
        {
            one.Release();
        }
    }
}

/// <summary>Words for logs and messages.</summary>
public static class Words
{
    /// <summary>"0h30m": hours and minutes.</summary>
    public static string Hm(double seconds)
    {
        var s = (long)Math.Max(0, seconds);
        return $"{s / 3600}h{s % 3600 / 60:00}m";
    }

    /// <summary>"1h05m", "5m03s", "42s": the two largest units.</summary>
    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, seconds);
        return s >= 3600 ? $"{s / 3600}h{s % 3600 / 60:00}m" : s >= 60 ? $"{s / 60}m{s % 60:00}s" : $"{s}s";
    }

    /// <summary>"1.4 GB" from a gigabyte on, else "730 MB".</summary>
    public static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>What may be handed to RCON say: one line, no double quotes or semicolons, at most `limit` characters.</summary>
    public static string ChatSafe(string text, int limit = 120)
    {
        var t = new string(text.Select(c => c == '"' ? '\'' : c == ';' ? ',' : c < ' ' ? ' ' : c).ToArray()).Trim();
        return t.Length <= limit ? t : t[..(limit - 3)] + "...";
    }
}
