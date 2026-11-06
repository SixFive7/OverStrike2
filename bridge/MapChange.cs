// The one way the map changes: a change counting down, then sent; a load that never arrives is given up.

static partial class Program
{
    /// <summary>The only path that changes the map: say so - the kind of message it is, the vote's result having its own (its window says it, the change's line
    /// would say it twice) - let the HUD's banner count it down, then FireChange. A server restart, or a list that made the map unplayable, clears Pending, and the stale callback
    /// sees that and stops.</summary>
    static void StartChange(Change change, string announcement, Messages.Kind? kind = null)
    {
        Pending = change;
        change.FiresAt = Now().AddSeconds(ChangeDelay);
        Post(kind ?? Messages.MapCountdown, announcement, seconds: ChangeDelay); // the countdown banner counts it down; chat and the board say it
        After(ChangeDelay * 1000, () => FireChange(change));
    }

    /// <summary>The countdown is over: host_workshop_map / changelevel goes out, the mode's commands first (ModeSwitch.cs).</summary>
    static void FireChange(Change change)
    {
        if (Pending != change) return; // cancelled in the meantime
        var now = Now();
        Pending = null;
        var command = change.Map.Official ? $"changelevel {change.Map.Name}" : $"host_workshop_map {change.Map.Id}";
        var reply = PrepareMode(change.Map) ? Send(command) : null; // ModeSwitch.cs: os_mode, game_type and game_mode go first
        Log($"{command} -> {reply ?? "(rcon failed)"}");
        if (reply == null) ModeBack();
        if (reply == null && change.Auto && StartRollOwed) { Log("start: the start roll did not reach the server - trying again"); return; }
        if (reply == null) // the server did not take it (it is restarting, or RCON is down): nothing is loading, so nothing may be waited for
        {
            Announce($"The server did not answer - {Clip(Label(change.Map), 40)} was not loaded");
            return;
        }
        StartRollOwed = false; // the server has left its start map one way or another
        ModeReloads = 0;       // ModeSwitch.cs: a new change may load once more for its mode
        S.Map = change.Map; S.PickedBy = change.By;
        AwaitLoad(now);
        Save();
        if (Unlisted(change.Map)) // the bridge stays off the internet: only loading proves an unlisted id is a map
            Announce($"{Clip(Label(change.Map), 40)} is not on the list: if nothing happens within a minute it is no playable map - Start a vote to replace it", 60);
    }

    public const int LoadMinutes = 10;            // a change that has not logged its map start in this long is given up...
    public const int LoadBehindDownloadMinutes = 30; // ...unless the restart watcher reports a background download it may wait behind: then up to this long

    /// <summary>A map command went out: its map start is awaited from now, LoadMinutes at the most (LoadDeadline).</summary>
    static void AwaitLoad(DateTime now)
    {
        AnnouncePending = true; AnnounceDeadline = now.AddMinutes(LoadMinutes); AnnounceSentAt = now; LoadBehindDownload = false;
    }

    // ---- the change that counts down ----

    /// <summary>A map change counting down CHANGE_DELAY_SECONDS: the vote's result, or one of the dice's automatic changes (Auto: the start roll,
    /// the way back to Arms Race on an empty server, a map gone from the Workshop or one that crashes the players' games).</summary>
    sealed class Change(MapInfo map, string by)
    {
        public MapInfo Map { get; } = map;
        public string By { get; } = by;
        public bool Auto { get; init; }
        public DateTime FiresAt; // when the countdown ends and the change goes out (StartChange); the HUD's banner counts down to it
    }

    /// <summary>The dice's automatic change (one helper for the four): the start roll, the empty server's way back to Arms Race, a map gone from
    /// the Workshop, a map that crashes the players' games - the times nobody can vote. A map on the server's disk (RandomQueue.cs). Lead says why.
    /// False: nothing to roll.</summary>
    static bool AutoRoll(string lead, Modes.Mode? mode = null)
    {
        if (RandomNext(mode) is not { } map) return false;
        StartChange(new Change(map, "the dice") { Auto = true }, $"{lead} - Rolling the dice: {Label(map)}{Detail(map, MapTags.WhyWords(TagsOf(map), picked: false))}{ModeTag(map)}");
        return true;
    }

    static void LoadDeadline(DateTime now)
    {
        if (AnnouncePending && BackgroundNow() is { } behind && !LoadBehindDownload)
        {
            LoadBehindDownload = true;
            Log($"load: {(S.Map != null ? Label(S.Map) : "?")} may wait behind the background download of {behind.Id} - it has {LoadBehindDownloadMinutes} min to start");
        }
        if (AnnouncePending && now >= AnnounceDeadline && LoadBehindDownload && AnnounceDeadline < AnnounceSentAt.AddMinutes(LoadBehindDownloadMinutes))
            AnnounceDeadline = AnnounceSentAt.AddMinutes(LoadBehindDownloadMinutes);
        if (AnnouncePending && now >= AnnounceDeadline)
        {
            AnnouncePending = false;
            Log($"load timed out: {(S.Map != null ? Label(S.Map) : "?")} never logged a map start");
            if (S.Map != null) Announce($"{Label(S.Map)} never finished loading");
            S.Map = null; S.PickedBy = null;
            ModeBack(); // ModeSwitch.cs: the mode it was going to goes back
            Save();
        }
    }
}
