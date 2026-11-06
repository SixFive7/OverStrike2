// The bridge's configuration: every value it reads at its start, from the settings (core/Settings.cs). Settings it cannot use, or none at all,
// stop it (StopCode).
using CoreSettings = OverStrike2.Core.Settings;
using Part = OverStrike2.Core.Part;

/// <summary>THE BRIDGE'S CONFIGURATION, made once at the start. Program's fields take every value from <see cref="Values"/> - a class of
/// its own, so no part of the partial Program can read it before it is made. The settings file's keys are those whose readers include the
/// bridge (docs/settings.md); its map list and broken list are maps.json and broken.json in paths.bridgeData. Main logs
/// <see cref="Settings"/>' StartLine.</summary>
static class BridgeConfig
{
    public static readonly CoreSettings Settings = CoreSettings.Load(Part.Bridge);
    public static readonly BridgeValues Values = BridgeValues.FromFile(Settings);   // settings it cannot use: their defaults, which Main never uses

    /// <summary>The exit code the bridge stops with before anything else, or null to start: settings it cannot use, or none at all
    /// (Settings.Fatal), stop it with the server programs' code, 78.</summary>
    public static int? StopCode(CoreSettings settings) => settings.Fatal != null ? CoreSettings.RefusedExit : null;
}

/// <summary>Every value the bridge reads at its start.</summary>
sealed record BridgeValues
{
    public string StateDir { get; init; } = "";
    public string RconHost { get; init; } = "";
    public int RconPort { get; init; }
    public string RconPassword { get; init; } = "";
    public string LogSecret { get; init; } = "";
    public string MapsFile { get; init; } = "";
    public string BrokenFile { get; init; } = "";
    public string StartMap { get; init; } = "";
    public int HttpPort { get; init; }
    public int ChangeDelay { get; init; }
    public int RestartCooldown { get; init; }
    public string Tab { get; init; } = "";      // HudTab.Parse reads it
    public string Check { get; init; } = "";    // HudCheck.Parse reads it
    public List<string> OfficialMaps { get; init; } = [];
    public int DiscoveryStock { get; init; }

    /// <summary>The settings' values (paths.bridgeData is an absolute path in the container, so its files are joined with '/').</summary>
    public static BridgeValues FromFile(CoreSettings s) => new()
    {
        StateDir = s.Paths.BridgeData,
        RconHost = s.Server.Host,
        RconPort = s.Server.Port,
        RconPassword = s.Server.RconPassword,
        LogSecret = s.Bridge.Secret,
        MapsFile = s.Paths.BridgeData.TrimEnd('/') + "/maps.json",
        BrokenFile = s.Paths.BridgeData.TrimEnd('/') + "/broken.json",
        StartMap = s.Server.StartMap,
        HttpPort = s.Bridge.Port,
        ChangeDelay = s.Bridge.ChangeDelaySeconds,
        RestartCooldown = s.Bridge.RestartCooldownSeconds,
        Tab = s.Hud.Tab,
        Check = s.Hud.Check,
        OfficialMaps = [.. s.Bridge.OfficialMaps],
        DiscoveryStock = s.Bridge.DiscoveryStock,
    };
}
