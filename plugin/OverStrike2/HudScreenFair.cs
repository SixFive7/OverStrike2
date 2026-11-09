namespace OverStrike2;

sealed partial class HudScreen
{
    /// <summary>The fairness restart's ask (EarlyJoinWatch, ../../bridge/Shared/EarlyJoin.cs): EarlyJoin.Command on /act for the last player in, as a click of
    /// theirs would go - the bridge counts the restart down and sends os_restart. False without a bridge.</summary>
    public bool AskFairRestart(long account, string name)
    {
        if (bridge == null || account <= 0) return false;
        bridge.Act(account, name, EarlyJoin.Command);
        return true;
    }
}
