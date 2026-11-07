using System.Globalization;

namespace OverStrike2.Core;

/// <summary>What the restart watcher (os2-server), the Workshop sync (os2-sync) and the deploy tools say to the bridge over HTTP: each message's
/// address and its body, written here once for both ends. The bridge's routes are &lt;bridge.url&gt;/&lt;address&gt;/&lt;bridge.secret&gt;; a body is
/// tab-separated lines, the first naming the message and its version (1). The senders build their bodies with these encoders, and the bridge
/// reads them with Fields and its own checks of what follows; every self-test holds the bodies to the words written out (core's above all).</summary>
public static class BridgeWire
{
    // ---- the addresses ----
    public const string Log = "log";                       // the game server's log push (the image's CS2_LOG_HTTP_URL ends in /log/<secret>)
    public const string Health = "health";                 // GET: is the bridge up and hearing the server; its answer starts with HealthWord
    public const string Ready = "ready";                   // the watcher: the queued maps on the disk, and the background download
    public const string Quitting = "quitting";             // the watcher, right before a planned quit, and once more if it did not go out
    public const string RestartNotice = "restart-notice";  // the watcher: a restart coming, or not after all
    public const string Updated = "updated";               // the watcher: the restart is done, and what it changed
    public const string Removed = "removed";               // the sync: the Workshop's verdicts on the listed maps
    public const string Reload = "reload";                 // the sync, or anyone: read the data files again now

    /// <summary>An address as the bridge's routes have it: "/ready/", the secret after it.</summary>
    public static string Route(string address) => $"/{address}/";

    /// <summary>The whole URL of an address: the bridge's root (bridge.url), the address, the secret.</summary>
    public static string Url(string root, string address, string secret) => $"{root.TrimEnd('/')}/{address}/{secret}";

    // ---- the bodies ----
    public const string HealthWord = "gg-health", ReadyWord = "gg-ready", QuitWord = "gg-quit", RestartWord = "gg-restart", UpdatedWord = "gg-updated", RemovedWord = "gg-removed";
    public const string Version = "1";
    public const string Cancel = "cancel";      // a quit or a restart announced that does not happen after all
    public const string Current = "current";    // the ready report's line of the background download

    /// <summary>The kinds of restart a notice names, as the bridge words them for the players: a CS2 update, an update of ours, the daily restart,
    /// stuck Workshop downloads.</summary>
    public const string GameUpdate = "game-update", OurUpdate = "our-update", Daily = "daily", Downloads = "downloads";

    /// <summary>When a restart goes: at the end of the match that is on, or now (its countdown in the notice's seconds).</summary>
    public const string AtMatchEnd = "match-end", AtOnce = "now";

    /// <summary>A body's first line: its message and the version, "gg-ready\t1".</summary>
    public static string Head(string word) => $"{word}\t{Version}";

    /// <summary>The watcher's report (POST /ready): the maps the queues name that it found on the disk, one Workshop id a line, and the background
    /// download running now, if one is (CurrentLine).</summary>
    public static string ReadyBody(IEnumerable<string> ids, string? current) =>
        Head(ReadyWord) + "\n" + string.Concat(ids.Select(id => id + "\n")) + (current is null ? "" : current + "\n");

    /// <summary>The background download's line: its Workshop id, a whole percent 0-100 or "-" before any progress, and its title, which is the
    /// rest of the line (no tab or line break in it).</summary>
    public static string CurrentLine(string id, int? percent, string title) =>
        $"{Current}\t{id}\t{(percent is { } p ? p.ToString(CultureInfo.InvariantCulture) : "-")}\t{title.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')}";

    /// <summary>The watcher's notice right before a planned quit (POST /quitting), with its own kind (daily, update, version, workshop).</summary>
    public static string QuitBody(string kind) => $"{Head(QuitWord)}\t{kind}\n";

    /// <summary>...and once more when that quit could not go out after all.</summary>
    public static string QuitCancelBody(string kind) => $"{Head(QuitWord)}\t{Cancel}\t{kind}\n";

    /// <summary>A restart coming (POST /restart-notice): its kind (GameUpdate, OurUpdate, Daily, Downloads), when (AtMatchEnd or AtOnce) and the
    /// seconds: with AtOnce the countdown to the quit, with AtMatchEnd from the match's end to the quit.</summary>
    public static string RestartBody(string kind, string when, int seconds) => $"{Head(RestartWord)}\t{kind}\t{when}\t{seconds.ToString(CultureInfo.InvariantCulture)}\n";

    /// <summary>The restart announced does not happen after all.</summary>
    public static string RestartCancelBody(string kind) => $"{Head(RestartWord)}\t{Cancel}\t{kind}\n";

    /// <summary>The restart is done (POST /updated): its kind, and what was installed before and is now - CS2's versions for GameUpdate, our
    /// parts' fingerprints for OurUpdate.</summary>
    public static string UpdatedBody(string kind, string from, string to) => $"{Head(UpdatedWord)}\t{kind}\t{from}\t{to}\n";

    /// <summary>The sync's verdicts on the listed maps (POST /removed): a Workshop id and its verdict a line (ok, removed, hidden, banned).</summary>
    public static string RemovedBody(IEnumerable<(string Id, string Verdict)> verdicts) =>
        Head(RemovedWord) + "\n" + string.Concat(verdicts.Select(v => $"{v.Id}\t{v.Verdict}\n"));

    /// <summary>The fields after the message and its version on a body's first line, each trimmed; null when the first line is not this message in
    /// version 1.</summary>
    public static string[]? Fields(string body, string word)
    {
        var f = body.Replace("\r", "").Split('\n')[0].Split('\t').Select(x => x.Trim()).ToArray();
        return f.Length >= 2 && f[0] == word && f[1] == Version ? f[2..] : null;
    }
}
