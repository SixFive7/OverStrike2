using System.Globalization;
using System.Security.Cryptography;

namespace OverStrike2.Server;

/// <summary>The watcher's console.log: proven live by an echo, then read for the freeze signature, quits, map loads and match ends
/// (see Watch.cs).</summary>
public sealed partial class Watch
{
    void ReadConsole(double now)
    {
        foreach (var tail in Console is { } c ? [c] : tails)
        {
            foreach (var line in tail.Lines())
            {
                if (probeToken is not null && Console is null && line.Contains(probeToken, StringComparison.Ordinal))
                {
                    Console = tail;
                    Log.Line($"console.log is live: {tail.Path} (echo seen {(now - probeSent).ToString("0.0", CultureInfo.InvariantCulture)} s after sending)");
                    probeToken = null;
                }
                if (tail != Console)
                    continue;
                try
                {
                    Workshop.Line.ConsoleLine(line, now);
                }
                catch (Exception e)   // a bug in the line must never cost the freeze recovery below
                {
                    Log.Once("download-line", $"WARNING: the download line ({e.GetType().Name}: {e.Message}) - no line");
                }
                if (ConsoleLines.IsQuitEcho(line))
                {
                    if (ArmedPid is not null)
                        Log.Line("a quit/restart was sent over RCON - the silence watch stands down until the engine answers again");
                    ArmedPid = null;
                    continue;
                }
                if (ConsoleLines.IsFreezeSignature(line))
                {
                    if (signatureAt is null)
                        Log.Line($"freeze signature seen: {Clip(line.Trim(), 120)}");
                    signatureAt ??= now;
                    otherLevel = false;
                    continue;
                }
                if (ConsoleLines.LevelStarted(line) is { } level && level != "error")
                {
                    if (signatureAt is not null)
                        Log.Line($"a new map is loading after the freeze signature ({level}) - not a freeze after all");
                    (signatureAt, otherLevel) = (null, true);
                }
                if (ConsoleLines.IsGameOver(line))
                {
                    matchEnds.Add(now);
                    if (matchEnds.Count > 10)
                        matchEnds.RemoveAt(0);
                }
            }
        }
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Prove a console.log is live: RCON `echo &lt;token&gt;` is logged (sv_logecho) and must show up in it.</summary>
    void ProbeConsole(double now)
    {
        if (Console is not null || ProbeTriesDone >= ProbeTries || now < probeNext || A2sInfo is null)
        {
            if (probeToken is not null && now - probeSent > 10)
            {
                probeToken = null;
                Log.Line($"console.log probe {ProbeTriesDone} of {ProbeTries}: the echo token did not appear within 10 s");
                if (ProbeTriesDone >= ProbeTries)
                    Log.Line($"ALERT: no console.log showed the echo token after {ProbeTries} tries - freeze recovery is OFF and planned restarts wait for an empty "
                             + "server. Check the engine's -condebug and sv_logecho 1 (the image's CS2_ADDITIONAL_ARGS and CS2_LOG_ECHO).");
            }
            return;
        }
        ProbeTriesDone++;
        probeNext = now + ProbeRetry;
        probeToken = "os2-watch-probe-" + RandomNumberGenerator.GetHexString(12, true);
        probeSent = now;
        if (!Engine.Rcon("echo " + probeToken).Opened)
        {
            probeToken = null;
            // The session never opened, so nothing was echoed: that says nothing about console.log. At boot the first probe lands about a
            // second after A2S answers, before RCON listens, and counting it would leave freeze recovery off for the retry after every start.
            if (ProbeRconMissesDone < ProbeRconMisses)
            {
                ProbeRconMissesDone++;
                ProbeTriesDone--;
                probeNext = now + ProbeRconRetry;
            }
        }
    }
}
