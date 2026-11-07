namespace OverStrike2;

/// <summary>When each player last really joined - css_guide, which the bridge sends on a real join, or os_hud level for the client harness -
/// for the Players page's time on and the join banner of the contents v4.0.0 to v4.5.0 (HudScreen). Kept by account, never by slot: a map
/// change disconnects everybody, and a player's time goes on through its reconnect whatever slot they come back in, while a player who takes a
/// slot somebody else left has none of theirs - only their own, from their own real join. No CounterStrikeSharp type in it: the self-test
/// drives it.</summary>
public sealed class JoinTimes
{
    readonly Dictionary<long, DateTime> joined = new(); // account -> its last real join

    /// <summary>A real join of this account, now. An account the game has not told (below 0) gets no time.</summary>
    public void Joined(long account, DateTime now)
    {
        if (account >= 0) joined[account] = now;
    }

    /// <summary>How long this account has been on since its last real join; null when the plugin has seen none since it loaded.</summary>
    public TimeSpan? Since(long account, DateTime now) => account >= 0 && joined.TryGetValue(account, out var at) ? now - at : null;
}
