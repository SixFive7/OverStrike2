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
///   - a round frozen past its own end time on the bridge's clock (VoteState.EndsAtMs: the vote's end while it is open, the map change's moment during its countdown) and
///     GraceSeconds more frees everybody, and that round stays free: a bridge that died or hangs never holds anybody past the vote's limit. MaxSeconds is the same for a word without an end time.</summary>
public sealed class FreezePlan
{
    public const double StaleSeconds = 40; // Hud.LongPollSeconds (25) and 15 more: an idle long poll answers every Hud.LongPollSeconds
    public const double GraceSeconds = 10, MaxSeconds = 300;

    /// <summary>Why everybody is frozen or free now: Vote frozen; Over no vote freezes anybody; Stale the bridge has not answered for
    /// StaleSeconds; OldMap the bridge has not answered since this map started; TooLong this round is past its end time.</summary>
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
    /// the plugin loaded mid-map, its state is new), and now; the round's end time and the bridge's clock, unix milliseconds (0: no end time known). True when Frozen changed.</summary>
    public bool Tick(long voteRound, bool freeze, DateTime answeredAt, DateTime mapStartedAt, DateTime now, long endsAtMs = 0, long bridgeNowMs = 0)
    {
        var why = voteRound <= 0 || !freeze ? Why.Over
            : answeredAt == DateTime.MinValue || answeredAt < mapStartedAt ? Why.OldMap
            : (now - answeredAt).TotalSeconds > StaleSeconds ? Why.Stale
            : voteRound == tooLong ? Why.TooLong
            : Why.Vote;
        if (why == Why.Over) round = 0;
        if (why == Why.Vote && voteRound != round) (round, since) = (voteRound, now); // a new round: its own clock
        if (why == Why.Vote && (endsAtMs > 0 ? bridgeNowMs > endsAtMs + (long)(GraceSeconds * 1000) : (now - since).TotalSeconds > MaxSeconds)) (tooLong, why) = (voteRound, Why.TooLong);
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
        Why.TooLong => $"this vote is {GraceSeconds:0} s past its end, and the bridge has not ended it",
        _ => "the vote is over",
    };
}
