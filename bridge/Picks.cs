// A player's pick: the map a click or a typed command names, the confirmation a risky map asks for, and !previous.
using System.Text.RegularExpressions;

static partial class Program
{
    /// <summary>!map: the map a click names (Find), refused when it is no playable map (Unplayable) or its mode does not suit the players on (B6: no
    /// a Wingman map with spawn points for fewer than are on), asked about once more when it is risky (ConfirmGate, per player) - then selected: a vote with it on the ballot and
    /// this player's vote on it, or onto the vote that is open (VoteRound.cs).</summary>
    static void PickMap(string by, string steamId, string query, string inMode = "")
    {
        var now = Now();
        if (Find(query) is not [var map]) { Tell(steamId, M.Refused($"No map is '{M.A(Clip(query, 40))}'")); return; }
        if (inMode != "" && Modes.Find(inMode) is { } asked && ModeOf(map).Id != asked.Id && ListedIn(asked, map) is { } there) map = there; // a card's Play under the mode filter
        if (Unplayable(map) is { } no) { Tell(steamId, no); return; }
        if (ModeRefusal(map) is { } full) { Tell(steamId, full); return; }
        if (PicksDue.TryGetValue(Key(map), out var due)) { Tell(steamId, M.Refused($"{M.A(Clip(Label(map), 40))} goes to the vote in {LeftWords(due.At, now)} - Click its countdown to cancel")); return; }
        var risky = RecordWords(map) != null;
        if (ConfirmGate(map, steamId, now) is { } warning) { Post(Messages.PlayAsksFirst, warning, steamId); return; }
        if (risky && GoesThrough(map, now)) { CountPickDown(by, steamId, map, now, previous: false); return; }
        SelectMap(by, steamId, map, now);
    }

    /// <summary>!previous: the last map before this one, selected like any map - a vote with it on the ballot.</summary>
    static void Previous(string by, string steamId)
    {
        var now = Now();
        var on = Pending?.Map ?? S.Map;
        var prev = Enumerable.Reverse(S.History).FirstOrDefault(m => on == null || Key(m) != Key(on));
        if (prev == null) { Tell(steamId, M.Refused("There is no previous map on record yet")); return; }
        if (Unplayable(prev) is { } no) { Tell(steamId, no); return; }
        if (ModeRefusal(prev) is { } full) { Tell(steamId, full); return; }
        if (PicksDue.TryGetValue(Key(prev), out var due)) { Tell(steamId, M.Refused($"{M.A(Clip(Label(prev), 40))} goes to the vote in {LeftWords(due.At, now)} - Click its countdown to cancel")); return; }
        var risky = RecordWords(prev) != null;
        if (ConfirmGate(prev, steamId, now, "Previous") is { } warning) { Post(Messages.PlayAsksFirst, warning, steamId); return; }
        if (risky && GoesThrough(prev, now)) { CountPickDown(by, steamId, prev, now, previous: true); return; }
        SelectMap(by, steamId, prev, now);
    }

    const int PickConfirmSeconds = (int)HudBrowser.ConfirmSeconds; // the window of Restart's second click too: one confirmation idiom, one number to remember

    /// <summary>A risky pick's ask (PickConfirmNote), as its plain note ends: Withdraw knows it by it.</summary>
    static bool IsPickAsk(string note) => note.EndsWith(" to vote anyway", StringComparison.Ordinal);

    /// <summary>The note that asks for the confirmation: which map, what is on record, and how to agree to it, in the words of the card's lock (HudMenu.Unlocked; Previous
    /// is a button of its own). The label is clipped at 34 characters, so the "click it again" half always fits however long the map is called.</summary>
    static string PickConfirmNote(MapInfo map, string record, string again = "Play")
    {
        var name = Label(map);
        return $"{M.A(name.Length > 34 ? name[..34] : name)} {record}. Click {(again == "Play" ? "" : $"{again} ")}again within {M.V($"{PickConfirmSeconds} s")} to vote anyway";
    }

    /// <summary>The confirmation gate in front of a pick: the first click arms a window for that player alone, and only that
    /// player repeating the same map inside PickConfirmSeconds gets through - another player's pick leaves it standing. Returns the note that
    /// holds the pick back, or null to let it through. The ballot card shows the same record to everybody (HudVote.Cards).</summary>
    static string? ConfirmGate(MapInfo map, string steamId, DateTime now, string again = "Play")
    {
        if (RecordWords(map) is not { } record) { PickConfirms.Remove(steamId); return null; }                                   // nothing on record: straight through
        if (PickConfirms.TryGetValue(steamId, out var armed) && armed.Key == Key(map) && now <= armed.Until) // confirmed in time: the ask is settled
        {
            PickConfirms.Remove(steamId);
            Withdraw(steamId, IsPickAsk);
            return null;
        }
        PickConfirms[steamId] = (Key(map), now.AddSeconds(PickConfirmSeconds));
        return PickConfirmNote(map, record, again);
    }

    // ---- a risky map's confirmed pick counts down first, in the button that confirmed it, and anybody may cancel it ----

    /// <summary>How long a risky map's confirmed pick counts down before it goes to the vote: the restarts' own countdown, so its Cancel has the same window.</summary>
    public const int PickCountdownSeconds = PressedSeconds;

    /// <summary>A risky map's pick, confirmed (its second click), counting down to the vote: who, their SteamID, the map, when it goes, and whether it came by Previous - whose
    /// button counts it down then, else the map's card.</summary>
    sealed record PickDue(string By, string SteamId, MapInfo Map, DateTime At, bool Previous);

    /// <summary>The picks counting down, by the map's key: one a map - a click on its countdown cancels it - and one a player - a newer one of theirs takes its place.</summary>
    static readonly Dictionary<string, PickDue> PicksDue = new();

    /// <summary>Whether a pick would go through now, so its countdown is worth starting: a vote is open, or one may be called with it (not the map that is on). Otherwise
    /// SelectMap refuses it at once, saying why, and nobody waits ten seconds for that.</summary>
    static bool GoesThrough(MapInfo map, DateTime now) => Ballot is { Open: true } || (!IsOn(map) && CallRefusal(now) == null);

    /// <summary>A risky map's pick, confirmed: it goes to the vote PickCountdownSeconds from now (FirePick), its button counts it down for everybody (the live state's
    /// PicksDue), and a click there cancels it (CancelPick). A pick of this player's counting down already gives way to it.</summary>
    static void CountPickDown(string by, string steamId, MapInfo map, DateTime now, bool previous)
    {
        foreach (var (key, old) in PicksDue.Where(p => p.Value.SteamId == steamId).ToList())
        {
            PicksDue.Remove(key);
            Log($"pick: {by} {steamId}'s pick of {Label(old.Map)} gives way to their pick of {Label(map)}");
        }
        var due = new PickDue(by, steamId, map, now.AddSeconds(PickCountdownSeconds), previous);
        PicksDue[Key(map)] = due;
        Log($"pick: {by} {steamId} confirms {Label(map)}{(previous ? " by Previous" : "")} - it goes to the vote in {PickCountdownSeconds} s, unless somebody cancels it");
        Tell(steamId, M.Done($"{M.A(Clip(Label(map), 40))} goes to the vote in {M.V($"{PickCountdownSeconds} s")} - Click its countdown to cancel"), PickCountdownSeconds);
        After(PickCountdownSeconds * 1000, () => FirePick(due));
    }

    /// <summary>A pick's countdown is over: it goes to the vote as any pick (SelectMap), with what refuses one now - unless it was cancelled, gave way to a newer one, or its
    /// player left. A map that became unplayable or does not suit the players on any more says so to its player.</summary>
    static void FirePick(PickDue due)
    {
        if (!PicksDue.TryGetValue(Key(due.Map), out var still) || still != due) return;
        PicksDue.Remove(Key(due.Map));
        var now = Now();
        if (!PresentIds(now).Contains(due.SteamId)) { Log($"pick: {due.By} {due.SteamId} left before their pick of {Label(due.Map)} went to the vote - it does not"); return; }
        if (Unplayable(due.Map) is { } no) { Tell(due.SteamId, no); return; }
        if (ModeRefusal(due.Map) is { } full) { Tell(due.SteamId, full); return; }
        Log($"pick: {due.By} {due.SteamId}'s pick of {Label(due.Map)} goes to the vote");
        SelectMap(due.By, due.SteamId, due.Map, now);
    }

    /// <summary>!cancel pick &lt;key&gt; (the map's card) or !cancel previous: the pick counting down stops - anybody may, as anybody may press that button; the news line says who.</summary>
    static void CancelPick(string by, string steamId, string what)
    {
        var now = Now();
        var due = what == "previous" ? PicksDue.Values.FirstOrDefault(p => p.Previous) : PicksDue.GetValueOrDefault(what);
        if (due == null || due.At <= now) { Tell(steamId, M.Refused("No pick counts down there any more")); return; }
        PicksDue.Remove(Key(due.Map));
        Log($"pick: {by} {steamId} cancels {due.By} {due.SteamId}'s pick of {Label(due.Map)}, {LeftWords(due.At, now)} before it went to the vote");
        Announce($"{M.N(Clip(by, 24))} cancelled {(due.SteamId == steamId ? "their" : $"{M.N(Clip(due.By, 24))}'s")} pick of {M.A(Clip(Label(due.Map), 32))} - It does not go to the vote");
    }

    /// <summary>Into the live state: the picks counting down, so every player's button counts each one down - the map's card, or Previous.</summary>
    static void PutPicksDue(HudState h, DateTime now)
    {
        foreach (var p in PicksDue.Values.Where(p => p.At > now).OrderBy(p => p.At)) h.PicksDue.Add((Key(p.Map), p.By, HudState.Ms(p.At), p.Previous));
    }

    [GeneratedRegex(@"^(?<id>\d+)$|[?&]id=(?<id>\d+)")] private static partial Regex IdRx(); // a bare number, or the id= of a pasted workshop URL

    /// <summary>The map a click names (Hud.IsMapArg): a rank of Arms Race's list, a Workshop id or URL - a map any mode lists, else an unlisted one
    /// in the mode the server is in - or an official map's whole name, Arms Race's own first, else the first mode that has it. One map or none:
    /// the plugin resolves names itself (HudScreen.Play), so no name search is left here.</summary>
    static List<MapInfo> Find(string query)
    {
        var q = query.Trim();
        if (IdRx().Match(q) is { Success: true } m && long.TryParse(m.Groups["id"].Value, out var n))
        {
            var known = Maps.Where(x => n <= Maps.Count ? x.Rank == n : x.Id == n).ToList();
            if (known.Count > 0 || n is < 1_000_000 or > 9_999_999_999) return known;
            return [ModeMapById(n) ?? new MapInfo(0, n, $"workshop {n}", 0, false, CurrentMode.ArmsRace ? "" : CurrentMode.Id)]; // another mode's listed map; else unlisted, in the mode the server is in
        }
        if (Officials.FirstOrDefault(o => o.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) is { } official) return [official];
        return OfficialElsewhere(q) is { } other ? [other] : []; // ModeSwitch.cs: another mode's official map by its whole name (a card's Play names it so)
    }
}
