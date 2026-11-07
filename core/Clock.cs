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
