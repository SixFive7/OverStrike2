// The bridge's configuration: every value it reads at its start, from the settings file when there is one (SettingsSource.cs), else from
// its environment variables as before. A settings file it cannot use stops it (StopCode).
using System.Globalization;
using CoreSettings = OverStrike2.Core.Settings;
using Part = OverStrike2.Core.Part;

/// <summary>THE BRIDGE'S CONFIGURATION, made once at the start. Program's fields take every value from <see cref="Values"/> - a class of
/// its own, so no part of the partial Program can read it before it is made. The settings file's keys are those whose readers include the
/// bridge (docs/settings.md); MAPS_FILE and BROKEN_FILE have no key: with the file they are maps.json and broken.json in paths.bridgeData.
/// Main logs <see cref="Source"/>'s line.</summary>
static class BridgeConfig
{
    public static readonly SettingsSource Source = SettingsSource.Load(Part.Bridge);
    public static readonly BridgeValues Values = Source.FromFile || Source.Refusal != null ? BridgeValues.FromFile(Source.File) // a refused file: its defaults, which Main never uses
        : BridgeValues.FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>The exit code the bridge stops with before anything else, or null to start: a settings file it cannot use (SettingsSource.Refusal) is
    /// the server programs' code, 78.</summary>
    public static int? StopCode(SettingsSource source) => source.Refusal != null ? CoreSettings.RefusedExit : null;
}

/// <summary>Every value the bridge reads at its start. Without the settings file each comes from its environment variable, read exactly as
/// before the file existed - a number that is no number stops the start.</summary>
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
    public bool RandomOnStart { get; init; }
    public bool GuideOnJoin { get; init; }
    public string Tab { get; init; } = "";      // HudTab.Parse reads it
    public string Check { get; init; } = "";    // HudCheck.Parse reads it
    public List<string> OfficialMaps { get; init; } = [];
    public int DiscoveryStock { get; init; }

    /// <summary>The settings file's values (paths.bridgeData is an absolute path in the container, so its files are joined with '/').</summary>
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
        RandomOnStart = s.Bridge.RandomOnStart,
        GuideOnJoin = s.Bridge.GuideOnJoin,
        Tab = s.Hud.Tab,
        Check = s.Hud.Check,
        OfficialMaps = [.. s.Bridge.OfficialMaps],
        DiscoveryStock = s.Bridge.DiscoveryStock,
    };

    /// <summary>The environment's values, as the bridge has always read them: an empty variable counts as unset.</summary>
    public static BridgeValues FromEnvironment(Func<string, string?> env)
    {
        string E(string key, string fallback) => env(key) is { Length: > 0 } v ? v : fallback;
        static bool Yes(string v) => v is "1" or "true" or "yes";
        static List<string> Names(string v) => [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return new()
        {
            StateDir = E("STATE_DIR", "/data"),
            RconHost = E("RCON_HOST", "cs2"),
            RconPort = int.Parse(E("RCON_PORT", "27015"), CultureInfo.InvariantCulture),
            RconPassword = E("RCON_PASSWORD", ""),
            LogSecret = E("LOG_SECRET", ""),
            MapsFile = E("MAPS_FILE", "maps.json"),
            BrokenFile = E("BROKEN_FILE", ""),
            StartMap = E("START_MAP", "ar_shoots"),
            HttpPort = int.Parse(E("HTTP_PORT", "8080"), CultureInfo.InvariantCulture),
            ChangeDelay = int.Parse(E("CHANGE_DELAY_SECONDS", "8"), CultureInfo.InvariantCulture),
            RestartCooldown = int.Parse(E("RESTART_COOLDOWN_SECONDS", "300"), CultureInfo.InvariantCulture),
            RandomOnStart = Yes(E("RANDOM_ON_START", "1")),
            GuideOnJoin = Yes(E("GUIDE_ON_JOIN", "1")),
            Tab = E("HUD_TAB", HudTab.Name(HudTab.Default)),
            Check = E("HUD_CHECK", "warn"),
            OfficialMaps = Names(E("OFFICIAL_MAPS", "ar_baggage,ar_pool_day,ar_shoots,ar_shoots_night")),
            DiscoveryStock = Math.Clamp(int.TryParse(E("DISCOVERY_STOCK", "10"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 10, 0, 40),
        };
    }
}
