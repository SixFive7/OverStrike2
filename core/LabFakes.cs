namespace OverStrike2.Core;

/// <summary>THE LAB'S FAKE PLAYERS: the made-up accounts the client harness speaks for on a test copy or the release rehearsal (deploy's bridgepost:
/// Test A to Test D connect, enter the game, chat and vote through the bridge's /log and /act; HUDtest is the player an os_hud click without a real one
/// acts as). They exist only in what is sent to the bridge - no game server has them, and production's bridge never hears of them - but in the log
/// stream they look like any human with a Steam account. So the bridge counts them as players where the lab needs them (a vote's present, its
/// voters) and nowhere it keeps: the match history and the ballots' record take real humans alone. Bots and CS2's other fake clients (GOTV) never enter
/// the log with a Steam account, so they are no humans to begin with.</summary>
public static class LabFakes
{
    /// <summary>The four fake players, by their test names (public: the screenshots show them).</summary>
    public static readonly (string Name, long Account)[] Players =
        [("Test A", 42424251), ("Test B", 42424252), ("Test C", 42424253), ("Test D", 42424254)];

    /// <summary>The player an os_hud click acts as when no real one is named (docshots): no such player on any server.</summary>
    public static readonly (string Name, long Account) HudTest = ("HUDtest", 42424243);

    /// <summary>A fake account as the log writes it: [U:1:n].</summary>
    public static string SteamId(long account) => $"[U:1:{account}]";

    /// <summary>Whether an account is one of the lab's fakes: never a real human.</summary>
    public static bool IsFake(long account) => account == HudTest.Account || Array.Exists(Players, p => p.Account == account);
}
