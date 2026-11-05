using System.Globalization;
using static OverStrike2.Core.SelfTest.T;

namespace OverStrike2.Core.SelfTest;

static class TimeTests
{
    static readonly TimeZoneInfo Cest = TimeZoneInfo.CreateCustomTimeZone("CEST test", TimeSpan.FromHours(2), "CEST test", "CEST test");
    static readonly TimeSpan Window = TimeSpan.FromHours(2);

    static DateTimeOffset At(int h, int m, int s = 0) => new(2026, 10, 4, h, m, s, TimeSpan.FromHours(2));   // a day of summer time

    public static void Run()
    {
        var seven = new TimeOfDay(7, 0);
        Check("time: the daily slot - due from its minute on", LocalTime.DailyDue(At(7, 0), seven, Cest, "2026-10-03", Window));
        Check("time: still due a minute before its window closes", LocalTime.DailyDue(At(8, 59), seven, Cest, "2026-10-03", Window));
        Check("time: its window closed two hours after the slot", !LocalTime.DailyDue(At(9, 0), seven, Cest, "2026-10-03", Window));
        Check("time: not yet a second before", !LocalTime.DailyDue(At(6, 59, 59), seven, Cest, "2026-10-03", Window));
        Check("time: done today", !LocalTime.DailyDue(At(7, 30), seven, Cest, "2026-10-04", Window));
        Check("time: off is never due", !LocalTime.DailyDue(At(7, 30), null, Cest, "", Window));
        var ams = LocalTime.Zone("Europe/Paris");     // any zone with European summer time
        Check("time: the TZ zone is found (" + ams.Id + ")", ams.BaseUtcOffset == TimeSpan.FromHours(1));
        var dstEnd = new DateTimeOffset(2026, 10, 25, 6, 30, 0, TimeSpan.Zero);      // 07:30 CET, the morning summer time ends
        Check("time: the morning summer time ends - 07:30 CET is due", LocalTime.DailyDue(dstEnd, seven, ams, "2026-10-24", Window));
        var spring = LocalTime.At(new DateOnly(2026, 3, 29), new TimeOfDay(2, 30), ams);
        Check("time: a time the clock skips (02:30 the night summer time starts) is the one after the gap: 03:30 summer time = 01:30 UTC",
              spring.UtcDateTime == new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc));
        var twice = LocalTime.At(new DateOnly(2026, 10, 25), new TimeOfDay(2, 30), ams);
        Check("time: a time the clock shows twice (02:30 the night summer time ends) is its first showing: 00:30 UTC",
              twice.UtcDateTime == new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc));
        var next = LocalTime.NextSlot(new DateTimeOffset(2026, 10, 24, 6, 0, 0, TimeSpan.Zero), seven, ams);   // 08:00 summer time on the 24th
        Check("time: the next 07:00 after 08:00 is the next day's, across the clock change (06:00 UTC)",
              next is { } n && n.UtcDateTime == new DateTime(2026, 10, 25, 6, 0, 0, DateTimeKind.Utc));
        Check("time: the local day and the wall clock", LocalTime.Day(dstEnd, ams) == "2026-10-25" && LocalTime.Clock(dstEnd, ams) == "07:30:00");
        Check("time: an unknown TZ falls back to the system's zone", LocalTime.Zone("Nowhere/Never") == TimeZoneInfo.Local);

        // the Web API's one door: three calls in a row, each 0.4 s long, at least 2 s after the last one ended
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        var door = new WebApiDoor(2, clock);
        var starts = new List<DateTimeOffset>();
        for (var i = 0; i < 3; i++)
            door.Call(() =>
            {
                starts.Add(clock.Now);
                clock.Now += TimeSpan.FromSeconds(0.4);
                return 0;
            });
        var gaps = starts.Zip(starts.Skip(1), (a, b) => (b - a - TimeSpan.FromSeconds(0.4)).TotalSeconds).ToList();
        Check($"door: three calls, each at least 2 s after the last ended (gaps {string.Join(", ", gaps.Select(g => g.ToString("0.0", CultureInfo.InvariantCulture)))})",
              gaps.Count == 2 && gaps.All(g => g >= 2.0 - 1e-9));
        Check("door: a gap under 2 s is 2 s", new WebApiDoor(0.5, clock).Gap == TimeSpan.FromSeconds(2));
        var failing = new WebApiDoor(2, clock);
        var before = clock.Now;
        try
        {
            failing.Call<int>(() => throw new IOException("HTTP 502"));
        }
        catch (IOException)
        {
        }
        failing.Call(() => 0);
        Check("door: a call that failed still counts - the next waits its 2 s", clock.Now - before >= TimeSpan.FromSeconds(2));

        Check("words: hours and minutes", Words.Hm(1800) == "0h30m" && Words.Hm(3725) == "1h02m" && Words.Hm(-5) == "0h00m");
        Check("words: durations", Words.Duration(3725) == "1h02m" && Words.Duration(125) == "2m05s" && Words.Duration(42) == "42s");
        Check("words: sizes", Words.Size(730L << 20) == "730 MB" && Words.Size(3L << 29) == "1.5 GB");
        Check("words: chat-safe - quotes and semicolons, one line, at most the limit",
              Words.ChatSafe("a \"b\"; quit") == "a 'b', quit" && Words.ChatSafe("x\ny") == "x y" && Words.ChatSafe(new string('a', 130)).Length == 120);
    }
}
