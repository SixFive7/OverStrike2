namespace OverStrike2;

sealed partial class HudScreen
{
    /// <summary>The fairness restart's ask (EarlyJoinWatch, ../../bridge/Shared/EarlyJoin.cs): EarlyJoin.Command on /act for a player who joined, as a click of
    /// theirs would go - the bridge counts the restart down, folds every join meanwhile into it, and sends os_restart. False without a bridge.</summary>
    public bool AskFairRestart(long account, string name)
    {
        if (bridge == null || account <= 0) return false;
        bridge.Act(account, name, EarlyJoin.Command);
        return true;
    }
}
