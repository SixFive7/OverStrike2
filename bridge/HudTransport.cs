// The live state's way to the plugin - the one place it is made and sent (the plugin's half is BridgeLink.cs).
//
// The state is published when it changes, at once: after every piece of work on the event loop - a log line, a click, a report, the tick - the
// bridge builds it again and, when the text differs, gives it the next version. Its countdowns ride as end times (HudState.AsOf, VoteState.AsOf),
// so a state that is only counting down does not change, and the plugin counts down itself.
//
// GET /hud/<secret>?v=<version> is a long poll: the bridge answers at once when its version is another, or holds the request until the state
// changes, LongPollSeconds at the most, and then answers that nothing did - a few bytes. Every answer starts with a line the plugin reads first,
// ver<TAB><version><TAB><the bridge's clock, unix milliseconds>, so it can count against the bridge's clock. A click (POST /act/<secret>?v=1)
// answers with the state after it in the same form. A plugin that sends no version gets the old text with its seconds, once a poll.
using System.Globalization;
using System.Net;

static partial class Program
{
    /// <summary>How long a poll waits for a change before it is answered with none.</summary>
    const int LongPollSeconds = Hud.LongPollSeconds;

    static readonly object HudLock = new();
    static string HudTextNow = new HudState().Encode(endTimes: true); // the change-driven text: end times, no seconds left
    static long HudVersion = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond; // starts new with every bridge, so a plugin always takes the first answer
    static readonly List<(HttpListenerContext Request, DateTime Until)> HudWaiting = new();

    /// <summary>After every piece of work on the event loop: the state again, and when it changed, the next version and every waiting poll answered.
    /// Polls that have waited LongPollSeconds are answered with no change.</summary>
    static void PublishHud()
    {
        var now = Now();
        var text = BuildHud(now).Encode(endTimes: true) + BuildVoteState(now).Encode(endTimes: true);
        var wake = new List<(HttpListenerContext Request, bool Changed)>();
        lock (HudLock)
        {
            var changed = text != HudTextNow;
            if (changed) { HudTextNow = text; HudVersion++; }
            var due = DateTime.UtcNow;
            for (var i = HudWaiting.Count - 1; i >= 0; i--)
                if (changed || due >= HudWaiting[i].Until) { wake.Add((HudWaiting[i].Request, changed)); HudWaiting.RemoveAt(i); }
        }
        foreach (var (request, changed) in wake) Answer(request, changed ? StateAnswer() : UnchangedAnswer());
    }

    /// <summary>Never lets a failure to publish end the event loop.</summary>
    static void PublishHudSafe() { try { PublishHud(); } catch (Exception e) { Log("hud: publishing the state failed: " + e.Message); } }

    /// <summary>The version a poll or a click asks with (?v=n), or null when it asks with none (an older plugin).</summary>
    static long? HudVersionAsked(string? query)
    {
        foreach (var part in (query ?? "").TrimStart('?').Split('&'))
            if (part.StartsWith("v=", StringComparison.Ordinal) && long.TryParse(part[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        return null;
    }

    /// <summary>A poll with a version: answered now when the bridge has another, else held until a change or LongPollSeconds (on the HTTP thread).</summary>
    static void HudLongPoll(HttpListenerContext c, long have)
    {
        lock (HudLock)
        {
            if (have == HudVersion) { HudWaiting.Add((c, DateTime.UtcNow.AddSeconds(LongPollSeconds))); return; }
        }
        Answer(c, StateAnswer());
    }

    /// <summary>The state in the long poll's form: the version line, then the text without its own first line.</summary>
    static string StateAnswer()
    {
        lock (HudLock) return $"{HudState.Magic}\n{VersionLine()}{HudTextNow[(HudState.Magic.Length + 1)..]}";
    }

    static string UnchangedAnswer()
    {
        lock (HudLock) return $"{HudState.Magic}\n{VersionLine()}{Hud.Unchanged}\n";
    }

    static string VersionLine() => $"{Hud.VersionLine}\t{HudVersion.ToString(CultureInfo.InvariantCulture)}\t{HudState.Ms(DateTime.UtcNow).ToString(CultureInfo.InvariantCulture)}\n";

    static void Answer(HttpListenerContext c, string body)
    {
        try { Reply(c, body); } catch (Exception e) { Log("hud: a poll's answer could not be sent: " + e.Message); } // the plugin went away meanwhile
    }

    /// <summary>The health page's line about the transport.</summary>
    static string TransportHealth()
    {
        lock (HudLock) return $"hud: version {HudVersion}, {HudWaiting.Count} poll(s) waiting, {HudTextNow.Length} characters";
    }
}
