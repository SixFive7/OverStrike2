namespace OverStrike2.Sync;

/// <summary>SteamCMD as a run's two downloading steps use it - the hand-over (Serve) and the test downloads (Download): one anonymous session
/// at a time, logged in again after a long wait or a stall, and the stops. Steam's push-back (a rate limit, Access Denied twice in a row) ends
/// the day's downloads; 3 failures in a row, a second stall in the run, or SteamCMD gone silent end the step's. A stall alone is routine.</summary>
public sealed class SteamSession(ISteamCmd steam, DownloadBudget budget, Func<string, string, DownloadBudget, bool> stalled, Action<string> log)
{
    public const double FreshAfter = 300;            // seconds a step may wait for an empty server before its session counts as stale: a new login

    bool started;
    int row, denied;

    /// <summary>Logged in for the next download - again when the step has waited over FreshAfter seconds. Null when it is, else the step's
    /// stop.</summary>
    public Stop? Ready(double waited)
    {
        if (started && waited > FreshAfter)
            End();
        if (started)
            return null;
        string why;
        (started, why) = steam.Start();
        if (started)
        {
            log($"SteamCMD: {why}");
            return null;
        }
        if (SteamCmdWords.StopRx().IsMatch(why))
            budget.RefuseForToday($"SteamCMD answered '{why}'");
        return new Stop(Stop.SteamCmd, $"SteamCMD did not log in anonymously: {why}");
    }

    /// <summary>One `workshop_download_item`: (ok, reason, bytes).</summary>
    public (bool Ok, string Reason, long Bytes) Download(string wid) => steam.Download(wid);

    /// <summary>A download that worked: the failures in a row start again.</summary>
    public void Worked() => (row, denied) = (0, 0);

    /// <summary>A download that failed, booked: the step's stop it means, or null to go on with the next map.</summary>
    public Stop? Failed(string wid, string reason)
    {
        row++;
        budget.Fail();
        if (SteamCmdWords.AccessDeniedRx().IsMatch(reason))
            denied++;
        if (SteamCmdWords.StopRx().IsMatch(reason) || denied >= 2)
        {
            budget.RefuseForToday($"SteamCMD answered '{reason}'");
            return new Stop(Stop.Pushback, $"SteamCMD answered '{reason}'{(denied >= 2 ? " twice in a row" : "")} - no more downloads today");
        }
        if (row >= 3)
            return new Stop(Stop.InARow, "3 downloads in a row failed - no more in this run");
        if (reason.StartsWith(SteamCmdWords.Stall, StringComparison.Ordinal))
        {
            if (stalled(wid, reason, budget))
                return new Stop(Stop.Stalls, $"SteamCMD stalled twice in this run ({reason}) - no more downloads in it");
            End();                                       // a fresh session for the next map
            return null;
        }
        if (reason == SteamCmdWords.NotRunning || reason.StartsWith(SteamCmdWords.Stdin, StringComparison.Ordinal))
            return new Stop(Stop.SteamCmd, $"SteamCMD stopped answering ({reason})");
        return null;
    }

    /// <summary>SteamCMD told to quit, its acf complete then; the next download logs in again.</summary>
    public void End()
    {
        steam.Stop();
        started = false;
    }
}
