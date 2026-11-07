using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE VERSION TRAP. Valve's UpToDateCheck does a numeric >= against required_version and reports "up to date" for almost any
/// number (measured: version=14186 -> up_to_date false, required 14188; 14188 -> true; 2000919, steam.inf's ServerVersion of a build
/// known to be stale -> true). The only correct input is steam.inf's dotted PatchVersion with the dots removed (1.41.8.8 -> 14188):
/// PatchVersion reads that field and nothing else, and VersionInt rejects anything without a dot.</summary>
public static partial class Versions
{
    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)+$")] private static partial Regex Dotted();   // at least one dot: rejects ServerVersion

    public const string ValveUrl = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v1/?appid=730&version={0}";

    public static bool IsDotted(string? s) => s != null && Dotted().IsMatch(s);

    /// <summary>1.41.8.8 -> 14188; null for anything not dotted or outside a plausible range.</summary>
    public static int? VersionInt(string? dotted)
    {
        if (!IsDotted(dotted))
            return null;
        return long.TryParse(dotted!.Replace(".", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 1000 and <= 99999999
            ? (int)v : null;
    }

    /// <summary>The PatchVersion field of steam.inf, and only that field (never ServerVersion or ClientVersion).</summary>
    public static string? PatchVersion(string steamInf)
    {
        foreach (var line in steamInf.Split('\n'))
        {
            var i = line.IndexOf('=');
            var key = i < 0 ? line : line[..i];
            if (key.Trim() == "PatchVersion")
            {
                var value = i < 0 ? "" : line[(i + 1)..].Trim();
                return value.Length > 0 ? value : null;
            }
        }
        return null;
    }

    public static string? ReadPatchVersion(string steamInfPath)
    {
        try
        {
            using var f = File.OpenRead(steamInfPath);
            var buf = new byte[65536];
            var n = f.Read(buf, 0, buf.Length);
            return PatchVersion(System.Text.Encoding.UTF8.GetString(buf, 0, n));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>An UpToDateCheck reply for build `vint`: ("current", null), ("stale", required) or ("unknown", why). Every field is
    /// type-checked; required_version must be strictly newer than ours.</summary>
    public static (string Answer, int? Required, string? Why) ValveVerdict(string raw, int vint)
    {
        JsonElement doc;
        try
        {
            doc = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException e)
        {
            return ("unknown", null, $"response is not JSON ({e.Message})");
        }
        if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("response", out var body) || body.ValueKind != JsonValueKind.Object)
            return ("unknown", null, "no response object");
        if (!body.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            return ("unknown", null, $"success is not true ({Show(body, "success")})");
        body.TryGetProperty("up_to_date", out var upToDate);
        if (upToDate.ValueKind == JsonValueKind.True)
            return ("current", null, null);
        if (upToDate.ValueKind != JsonValueKind.False)
            return ("unknown", null, $"up_to_date is not a boolean ({Show(body, "up_to_date")})");
        if (!body.TryGetProperty("required_version", out var req) || req.ValueKind != JsonValueKind.Number || !req.TryGetInt32(out var required))
            return ("unknown", null, $"required_version is absent or not an integer ({Show(body, "required_version")})");
        if (required <= vint)
            return ("unknown", null, $"required_version {required} is not newer than the local {vint}");
        return ("stale", required, null);
    }

    static string Show(JsonElement body, string key) => body.TryGetProperty(key, out var v) ? v.GetRawText() : "None";
}

/// <summary>Console lines. Anchored so a player's chat (echoed as "Name&lt;..&gt;" say "...") can never imitate them: the patterns only match
/// before the first double quote of a line.</summary>
public static partial class ConsoleLines
{
    const string Prefix = @"^[^""]*?(?:L )?\d\d/\d\d/\d{4} - \d\d:\d\d:\d\d(?:\.\d+)?(?: - |: )";

    [GeneratedRegex(Prefix + "Game Over: ")] private static partial Regex GameOver();
    [GeneratedRegex(@"^[^""]*Level loading started for 'error'")] private static partial Regex Freeze1();
    [GeneratedRegex(@"^[^""]*LoopInit:\s+in ""error"" map")] private static partial Regex Freeze2();
    [GeneratedRegex(@"^[^""]*Assertion Failed in function LoopInit\(\)")] private static partial Regex Freeze3();
    [GeneratedRegex(@"^[^""]*Level loading started for '([^']*)'")] private static partial Regex Level();
    // The echo of an RCON quit or restart, from anyone (the bridge's !restart quits): the engine is going down on purpose.
    [GeneratedRegex(Prefix + @"rcon from ""[^""]*"": command ""(?:quit|_restart|restart)(?: [^""]*)?""\s*$")] private static partial Regex QuitEcho();

    public static bool IsGameOver(string line) => GameOver().IsMatch(line);
    public static bool IsFreezeSignature(string line) => Freeze1().IsMatch(line) || Freeze2().IsMatch(line) || Freeze3().IsMatch(line);
    public static string? LevelStarted(string line) => Level().Match(line) is { Success: true } m ? m.Groups[1].Value : null;
    public static bool IsQuitEcho(string line) => QuitEcho().IsMatch(line);
}

/// <summary>The restart kinds. In the state file: update (a CS2 build Valve calls stale), version (a new version of our own parts on disk),
/// daily, workshop (the engine's Workshop downloads are stuck), freeze (a kill), prefetch (made no more; read so that an old history does not
/// fault the file). On the wire to the bridge: game-update, our-update, daily, downloads.</summary>
public static class Kinds
{
    public static readonly string[] All = ["update", "version", "daily", "freeze", "workshop", "prefetch"];
    public static readonly string[] Planned = ["update", "version", "daily"];       // share the cap and the floor
    public static readonly string[] EmptyOnly = ["daily", "workshop"];              // wait for an empty server: never a kick
    public static bool IsPlanned(string k) => Planned.Contains(k);

    public static string Wire(string kind) => kind switch
    {
        "update" => BridgeWire.GameUpdate,
        "version" => BridgeWire.OurUpdate,
        "workshop" => BridgeWire.Downloads,
        _ => kind,
    };
}

/// <summary>The decisions: pure, unit-tested.</summary>
public static class Rules
{
    public const double DailyWindow = 7200;       // the daily restart may happen up to 2 h after its slot, then that day is skipped
    public const double PlannedMinGap = 600;      // floor between two planned restarts
    public const int PlannedMaxPerDay = 4;        // cap on planned restarts in any rolling 24 h
    public const int UpdateMaxMisses = 3;         // build restarts that changed nothing before the update breaker latches
    public static readonly double[] UpdateRetry = [1800, 7200];   // after a miss, wait 30 min, then 2 h (Valve's CDN can lag its own API)
    public const double FreezeMinGap = 180;       // floor between two freeze kills
    public const int FreezeMaxPerDay = 10;        // cap on freeze kills in any rolling 24 h
    public const int FreezeMaxMisses = 2;         // freeze kills after which the server never came back, before that breaker latches
    public const double WorkshopRestartGap = 7200;    // at most one download restart per 2 h...
    public const int WorkshopRestartMax = 3;      // ...and 3 per rolling 24 h; past that, warn only
    public const double Window = 86400;
    public const int DefaultBackIn = 30;          // announced "back in" until restarts were measured (container start -> A2S: 6-12 s)
    public const int SilentPolls = 3;             // consecutive failed polls, A2S and RCON each, before silence alone may kill

    public static DateTimeOffset At(double t) => DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(t * TimeSpan.TicksPerSecond));

    /// <summary>A moment as local wall-clock time, HH:MM:SS. A gate names WHEN the restart or kill it waits on happened, never how long
    /// ago: an age changes every second, and LogOnce would take every line for a new one.</summary>
    public static string AtClock(double t, TimeZoneInfo tz) => LocalTime.Clock(At(t), tz);

    public static double? NextSlot(double after, TimeOfDay? hhmm, TimeZoneInfo tz) =>
        LocalTime.NextSlot(At(after), hhmm, tz) is { } s ? Watch.Seconds(s) : null;

    public static string LocalDay(double t, TimeZoneInfo tz) => LocalTime.Day(At(t), tz);

    /// <summary>Is the daily restart due now? From today's slot until DailyWindow after it, unless done that day.</summary>
    public static bool DailyDue(double now, TimeOfDay? hhmm, TimeZoneInfo tz, string doneDay) =>
        LocalTime.DailyDue(At(now), hhmm, tz, doneDay, TimeSpan.FromSeconds(DailyWindow));

    /// <summary>Path a: kill only on all of the error-map signature, continuous A2S silence that began no later than shortly after it and
    /// has lasted `grace`, a live engine process, and no other map load started since the signature.</summary>
    public static bool FreezeConfirmed(double now, double? signatureAt, double? silentSince, bool engineAlive, bool otherLevel, double grace)
    {
        if (signatureAt is not { } sig || silentSince is not { } silent || !engineAlive || otherLevel)
            return false;
        if (silent > sig + 30)                    // the silence must cover the freeze, not start long after it
            return false;
        return now - sig >= grace && now - silent >= grace;
    }

    /// <summary>Path b: kill on silence alone only on all of an armed engine (this process answered A2S since it started, and no quit or
    /// restart has been seen since), still running, not being taken down by the watcher itself, and A2S AND RCON both failing on every
    /// poll for `grace` - at least `polls` polls each, so a lost packet is never a streak.</summary>
    public static bool SilenceConfirmed(double now, double? a2sSince, int a2sFails, double? rconSince, int rconFails, bool armed, bool engineAlive,
                                        bool goingDown, double grace, int polls = SilentPolls)
    {
        if (!armed || !engineAlive || goingDown || a2sSince is not { } a || rconSince is not { } r)
            return false;
        if (a2sFails < polls || rconFails < polls)
            return false;
        return now - a >= grace && now - r >= grace;
    }

    /// <summary>Null, or (alert, why) for a planned restart: at most PlannedMaxPerDay in 24 h, PlannedMinGap apart.</summary>
    public static (bool Alert, string Why)? PlannedBlock(WatchState state, double now, TimeZoneInfo tz)
    {
        var recent = state.Restarts.Count(e => now - e.At >= 0 && now - e.At < Window && Kinds.IsPlanned(e.Kind));
        if (recent >= PlannedMaxPerDay)
            return (true, $"{recent} planned restarts in the last 24 h, the cap is {PlannedMaxPerDay}");
        var last = state.Restarts.Where(e => Kinds.IsPlanned(e.Kind)).Select(e => (double)e.At).DefaultIfEmpty(0).Max();
        if (last > now)
            return (true, "the last restart is in the future (the clock moved backwards)");
        if (last > 0 && now - last < PlannedMinGap)
            return (false, $"the last planned restart was at {AtClock(last, tz)}, the floor is {PlannedMinGap:0} s");
        return null;
    }

    /// <summary>Null, or why a download restart may not happen now: at most one per WorkshopRestartGap and WorkshopRestartMax per rolling
    /// 24 h (a persistent Steam problem must not become a restart loop), and never inside PlannedMinGap of a planned restart.</summary>
    public static string? WorkshopBlock(WatchState state, double now, TimeZoneInfo tz)
    {
        var mine = state.Restarts.Where(e => e.Kind == "workshop" && now - e.At >= 0 && now - e.At < Window).Select(e => (double)e.At).ToList();
        if (mine.Count >= WorkshopRestartMax)
            return $"{mine.Count} download restart(s) in the last 24 h, the cap is {WorkshopRestartMax}";
        if (mine.Count > 0 && now - mine.Max() < WorkshopRestartGap)
            return $"the last download restart was at {AtClock(mine.Max(), tz)}, the floor is {Words.Hm(WorkshopRestartGap)}";
        var last = state.Restarts.Where(e => Kinds.IsPlanned(e.Kind)).Select(e => (double)e.At).DefaultIfEmpty(0).Max();
        if (last > 0 && now - last >= 0 && now - last < PlannedMinGap)
            return $"a planned restart was at {AtClock(last, tz)}, the floor is {PlannedMinGap:0} s";
        return null;
    }

    public static (bool Alert, string Why)? FreezeBlock(WatchState state, double now, TimeZoneInfo tz)
    {
        if (state.FreezeTripped != 0)
            return (true, $"the freeze breaker is latched ({state.FreezeMisses} kills did not bring the server back)");
        var kills = state.Restarts.Where(e => e.Kind == "freeze").Select(e => (double)e.At).ToList();
        var recent = kills.Count(k => now - k >= 0 && now - k < Window);
        if (recent >= FreezeMaxPerDay)
            return (true, $"{recent} freeze kills in the last 24 h, the cap is {FreezeMaxPerDay}");
        if (kills.Count > 0 && kills.Max() > now)
            return (true, "the last kill is in the future (the clock moved backwards)");
        if (kills.Count > 0 && now - kills.Max() < FreezeMinGap)
            return (false, $"the last freeze kill was at {AtClock(kills.Max(), tz)}, the floor is {FreezeMinGap:0} s");
        return null;
    }

    /// <summary>Seconds to announce: the slowest of the last five measured restarts + 5 s, in 5 s steps, 20..300.</summary>
    public static int BackIn(IReadOnlyList<int> durations)
    {
        if (durations.Count == 0)
            return DefaultBackIn;
        var slowest = durations.Skip(Math.Max(0, durations.Count - 5)).Max();
        return (int)Math.Min(300, Math.Max(20, Math.Ceiling((slowest + 5) / 5.0) * 5));
    }

    public static string SayBackIn(int seconds) => seconds <= 90 ? $"about {seconds} seconds" : $"about {(int)Math.Ceiling(seconds / 60.0)} minutes";

    /// <summary>The download restart's one line, right before its quit at an empty server: for the log, nobody being on. Every other planned
    /// restart says nothing of its own: the bridge words what players see (a restart notice it did not take warns nobody).</summary>
    public static string WorkshopLine(int back) => $"Workshop downloads are broken in this server process - restarting it while nobody is on (the restart watcher). Back in {SayBackIn(back)}.";
}
