// A map's standing: what is held against it. Seven facts, each kept where it is read - blocked (Blocked.cs), gone from the Workshop
// (Removed.cs), no playable map at all (a non-map, BrokenList.cs), on the broken list (BrokenList.cs), crashes on record (CrashWatch.cs), a
// player-crash mark (BrokenWhy.cs), an ads mark (Sizes.cs) - and the four questions every part asks of them, answered here alone: may it be
// played at all (Unplayable), is anything on record against it (Risky; RecordWords, the confirm's words), does the keep set leave it off
// (Bad), and may the dice roll it (CanRoll).

static partial class Program
{
    /// <summary>Why a map cannot be played at all, or null - the one check for a map asked for, a card on the ballot, a change
    /// counting down and a list that changed: blocked, gone from the Workshop, or no playable map (a pack). Each refusal says why.</summary>
    static string? Unplayable(MapInfo m) => IsBlocked(m) ? BlockedRefusal(m) : IsRemoved(m) ? RemovedRefusal(m) : IsPack(m) ? PackRefusal(m) : null;

    /// <summary>Unplayable, as a yes or no, by the map's key: a Workshop map's key is its id, which the three lists go by.</summary>
    static bool IsUnplayable(MapInfo m, string key) => !m.Official && (Blocked.ContainsKey(key) || Removed.ContainsKey(key) || Packs.ContainsKey(key));

    /// <summary>Something on record against the map with this key: a player-crash mark, the broken list, a crash of the server.</summary>
    static bool Risky(string key) => S.PlayerCrashes.ContainsKey(key) || Broken.ContainsKey(key) || S.Crashes.GetValueOrDefault(key) > 0;

    /// <summary>What is on record against this map, or null when nothing is (Risky): the words a pick's confirm says. The swept list comes first,
    /// because it is the stronger evidence: BROKEN_FILE, maps that crashed the server on every test load, then the live per-map crash counter.</summary>
    static string? RecordWords(MapInfo map)
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

    /// <summary>What the keep set leaves off its lists: unplayable, or anything on record against it.</summary>
    static bool Bad(MapInfo m) { var key = Key(m); return IsUnplayable(m, key) || Risky(key); }

    /// <summary>The rule every random pick applies to a candidate: not the map that is on, nothing against it (Bad), not marked as showing ads, and
    /// not in <paramref name="exclude"/> (the ballot's own maps). The stock (RandomQueue.cs) rolls with it and checks it again when an entry is used.
    /// It runs for every candidate of every roll, so it goes by the key alone.</summary>
    static bool CanRoll(MapInfo m, ISet<string>? exclude = null)
    {
        var current = CurrentKey; // by Workshop id when the bridge picked it, else by the name the server logs
        var key = Key(m);
        return key != current && m.Name != current && !IsUnplayable(m, key) && !Risky(key) && AdsVotes(key) == 0 && exclude?.Contains(key) != true;
    }
}
