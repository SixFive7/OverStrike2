// A player's pick: the map a click or a typed command names, the confirmation a risky map asks for, and !previous.
using System.Text.RegularExpressions;

static partial class Program
{
    /// <summary>!map: the map a click names (Find), refused when it is no playable map (Unplayable) or its mode does not suit the players on (B6: no
    /// Wingman with five or more), asked about once more when it is risky (ConfirmGate, per player) - then selected: a vote with it on the ballot and
    /// this player's vote on it, or onto the vote that is open (VoteRound.cs).</summary>
    static void PickMap(string by, string steamId, string query, string inMode = "")
    {
        var now = Now();
        if (Find(query) is not [var map]) { Tell(steamId, M.Refused($"No map is '{M.A(Clip(query, 40))}'")); return; }
        if (inMode != "" && Modes.Find(inMode) is { } asked && ModeOf(map).Id != asked.Id && ListedIn(asked, map) is { } there) map = there; // a card's Play under the mode filter
        if (Unplayable(map) is { } no) { Tell(steamId, no); return; }
        if (ModeRefusal(map) is { } full) { Tell(steamId, full); return; }
        if (ConfirmGate(map, steamId, now) is { } warning) { Post(Messages.PlayAsksFirst, warning, steamId); return; }
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
        if (ConfirmGate(prev, steamId, now, "Previous") is { } warning) { Post(Messages.PlayAsksFirst, warning, steamId); return; }
        SelectMap(by, steamId, prev, now);
    }

    const int PickConfirmSeconds = 30; // same window as Restart: one confirmation idiom, one number to remember

    /// <summary>What is on record against this map, or null when nothing is. Two sources, the swept list first because it is the
    /// stronger evidence: BROKEN_FILE, maps that crashed the server on every test load, then the live per-map crash counter.</summary>
    static string? PickRecord(MapInfo map)
    {
        var key = Key(map);
        var tests = Broken.GetValueOrDefault(key);
        var why = ReasonOf(key); // BrokenWhy.cs: broken.json's reason, or a player-crash mark
        return S.PlayerCrashes.ContainsKey(key) ? $"is broken: {why}"
            : tests > 0 ? $"crashed {tests} of {tests} test loads and may restart the server"
            : Broken.ContainsKey(key) ? (why != "" ? $"is broken: {why}" : "is on the broken list")
            : S.Crashes.GetValueOrDefault(key) is var crashes && crashes > 0 ? $"crashed the server {crashes} time(s)"
            : null;
    }

    /// <summary>The note that asks for the confirmation: which map, what is on record, and how to agree to it, in the words of the card's lock (HudMenu.Unlocked; Previous
    /// is a button of its own). The label is clipped at 34 characters, so the "click it again" half always fits however long the map is called.</summary>
    static string PickConfirmNote(MapInfo map, string record, string again = "Play")
    {
        var name = Label(map);
        return $"{M.A(name.Length > 34 ? name[..34] : name)} {record}. Click {(again == "Play" ? "" : $"{again} ")}again within {M.V($"{PickConfirmSeconds} s")} to vote anyway";
    }

    /// <summary>The confirmation gate in front of a pick: the first click arms a window for that player alone, and only that
    /// player repeating the same map inside PickConfirmSeconds gets through - another player's pick no longer clears it. Returns the note that
    /// holds the pick back, or null to let it through. The ballot card shows the same record to everybody (HudVote.Cards).</summary>
    static string? ConfirmGate(MapInfo map, string steamId, DateTime now, string again = "Play")
    {
        if (PickRecord(map) is not { } record) { PickConfirms.Remove(steamId); return null; }                                   // nothing on record: straight through
        if (PickConfirms.TryGetValue(steamId, out var armed) && armed.Key == Key(map) && now <= armed.Until) { PickConfirms.Remove(steamId); return null; } // confirmed in time
        PickConfirms[steamId] = (Key(map), now.AddSeconds(PickConfirmSeconds));
        return PickConfirmNote(map, record, again);
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
