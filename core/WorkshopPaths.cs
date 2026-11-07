namespace OverStrike2.Core;

/// <summary>Where Steam keeps CS2's Workshop items: a workshop folder holds appworkshop_730.acf, the items' record, and content/730/&lt;id&gt;,
/// their folders. A game install has its workshop folder under game/bin/linuxsteamrt64/steamapps, a SteamCMD folder (the sync's staging)
/// under steamapps. Both server programs find them here.</summary>
public static class WorkshopPaths
{
    /// <summary>The workshop folder of a game install.</summary>
    public static string OfGame(string gameDir) => OfSteamCmd(Path.Combine(gameDir, "game", "bin", "linuxsteamrt64"));

    /// <summary>The workshop folder of a SteamCMD folder (its force_install_dir).</summary>
    public static string OfSteamCmd(string root) => Path.Combine(root, "steamapps", "workshop");

    public static string Acf(string workshop) => Path.Combine(workshop, "appworkshop_730.acf");

    public static string Content(string workshop) => Path.Combine(workshop, "content", "730");
}
