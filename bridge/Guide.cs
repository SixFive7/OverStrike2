// How the bridge asks the plugin to welcome a player who really joined. Shared by the two programs: the plugin opens the welcome window (HudScreen.Joined). Plain BCL only.
public static class Guide
{
    /// <summary>The console command the plugin registers and the bridge sends over RCON when a player really joins.</summary>
    public const string Command = "css_guide";

    /// <summary>How the plugin's reply to Command starts when it took it; any other reply means it did not.</summary>
    public const string Shown = "guide: showing";

    /// <summary>The server's name, as it stands in the window's head.</summary>
    public const string Name = "Over Strike 2";
}
