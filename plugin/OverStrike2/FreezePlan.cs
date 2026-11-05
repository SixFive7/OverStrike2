namespace OverStrike2;

/// <summary>When the vote freezes everybody, decided from the bridge's word on the plugin's own clock. No CounterStrikeSharp type in it: the
/// self-test drives it on a handed clock; VoteFreeze.cs does what it says, in the game.
///
/// Everybody is frozen while the bridge's live state says so (VoteState.Freeze: a vote is open, or its result counts down to a map change),
/// and only while that word is this map's and fresh:
///   - an answer from before this map started freezes nobody: the last state of the old map's vote stays in memory across the map change
///     until the first answer on the new one;
///   - no answer for StaleSeconds frees everybody, so a lost bridge never leaves anybody frozen; its next answer freezes them again if the
///     vote is still on;
///   - one round frozen for longer than MaxSeconds frees everybody until the next round - a vote runs MapVote.Seconds, at the most
///     MapVote.HardCapSeconds, and its countdown a few seconds more, so only a bridge stuck on one answer gets there.</summary>
public sealed class FreezePlan
{
    public const double StaleSeconds = 10, MaxSeconds = 300;

    /// <summary>Why everybody is frozen or free now: Vote frozen; Over no vote freezes anybody; Stale the bridge has not answered for
    /// StaleSeconds; OldMap the bridge has not answered since this map started; TooLong this round has been frozen for MaxSeconds.</summary>
    public enum Why { Over, Vote, Stale, OldMap, TooLong }

    long round;                       // the round whose freeze clock runs (0: none)
    DateTime since = DateTime.MinValue; // ...frozen first at
    long tooLong;                     // a round frozen past MaxSeconds: nobody is frozen for it again

    /// <summary>Everybody is frozen now.</summary>
    public bool Frozen { get; private set; }

    /// <summary>Why, as of the last tick.</summary>
    public Why Reason { get; private set; } = Why.Over;

    /// <summary>The round whose freeze clock runs, and when it first froze everybody.</summary>
    public long Round => round;
    public DateTime Since => since;

    /// <summary>One tick: the vote's round and freeze as the bridge last said them, when it last answered, when this map started (MinValue:
    /// the plugin loaded mid-map, its state is new), and now. True when Frozen changed.</summary>
    public bool Tick(long voteRound, bool freeze, DateTime answeredAt, DateTime mapStartedAt, DateTime now)
    {
        var why = voteRound <= 0 || !freeze ? Why.Over
            : answeredAt == DateTime.MinValue || answeredAt < mapStartedAt ? Why.OldMap
            : (now - answeredAt).TotalSeconds > StaleSeconds ? Why.Stale
            : voteRound == tooLong ? Why.TooLong
            : Why.Vote;
        if (why == Why.Over) round = 0;
        if (why == Why.Vote && voteRound != round) (round, since) = (voteRound, now); // a new round: its own clock
        if (why == Why.Vote && (now - since).TotalSeconds > MaxSeconds) (tooLong, why) = (voteRound, Why.TooLong);
        Reason = why;
        var frozen = why == Why.Vote;
        if (frozen == Frozen) return false;
        Frozen = frozen;
        return true;
    }

    /// <summary>The words for the log: why everybody is free now.</summary>
    public string FreeBecause() => Reason switch
    {
        Why.Stale => $"the bridge has not answered for {StaleSeconds:0} s",
        Why.OldMap => "a new map: the vote's last word is from the map before",
        Why.TooLong => $"this vote has frozen everybody for {MaxSeconds:0} s, the most one vote may",
        _ => "the vote is over",
    };
}
