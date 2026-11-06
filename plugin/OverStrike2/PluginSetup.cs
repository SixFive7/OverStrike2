using CoreSettings = OverStrike2.Core.Settings;

namespace OverStrike2;

/// <summary>THE PLUGIN'S OWN SETTINGS, read once at its load: the keys whose readers include the plugin - hud.addon, bridge.url and
/// bridge.secret, hud.itemPage, steam.apiGapSeconds. Settings it cannot use, or none at all, stop the plugin (OverStrike2.Load). No
/// CounterStrikeSharp type in it: the self-test runs it as it is.</summary>
public sealed record PluginSetup
{
    /// <summary>The HUD addon's Workshop id (trimmed), which the settings require.</summary>
    public string Addon { get; init; } = "";

    /// <summary>The bridge's address (http://host:port), or null: no bridge, and <see cref="BridgeWhy"/> says why. With an address,
    /// BridgeWhy is the address, for the log - never the secret.</summary>
    public string? Bridge { get; init; }
    public string BridgeSecret { get; init; } = "";
    public string BridgeWhy { get; init; } = "";

    /// <summary>Whether the HUD item's Workshop page may be visited (ItemWatch), and the name of the switch that says so, for its notes.</summary>
    public bool ItemPage { get; init; } = true;
    public string ItemPageSwitch { get; init; } = "";

    /// <summary>The least time between two Steam Web API requests (SteamWebApi.Gap): steam.apiGapSeconds.</summary>
    public double ApiGapSeconds { get; init; } = SteamWebApi.MinGapSeconds;

    /// <summary>The settings' values.</summary>
    public static PluginSetup FromFile(CoreSettings s) => new()
    {
        Addon = s.Hud.Addon.Trim(),
        Bridge = s.Bridge.Secret != "" ? s.Bridge.Url : null,
        BridgeSecret = s.Bridge.Secret,
        BridgeWhy = s.Bridge.Secret != "" ? s.Bridge.Url : "bridge.secret is empty",
        ItemPage = s.Hud.ItemPage,
        ItemPageSwitch = "hud.itemPage",
        ApiGapSeconds = s.Steam.ApiGapSeconds,
    };
}
