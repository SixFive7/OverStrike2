using CoreSettings = OverStrike2.Core.Settings;

namespace OverStrike2;

/// <summary>THE PLUGIN'S OWN SETTINGS, read once at its load: from the settings file when there is one that can be used (SettingsSource.cs;
/// the keys whose readers include the plugin - hud.addon, bridge.url and bridge.secret, hud.itemPage, steam.apiGapSeconds), else from the
/// cs2 service's environment as before, until the install step writes the file. No CounterStrikeSharp type in it: the self-test runs it as
/// it is.</summary>
public sealed record PluginSetup
{
    /// <summary>The cs2 service's log push address, http://bridge:8080/log/&lt;secret&gt;: without the settings file, the bridge's address and
    /// secret come from it.</summary>
    public const string LogUrlVariable = "CS2_LOG_HTTP_URL";

    /// <summary>The HUD addon's Workshop id (trimmed; empty: no HUD), and where it came from: a variable's name, or the file's key.</summary>
    public string Addon { get; init; } = "";
    public string AddonFrom { get; init; } = "";

    /// <summary>The bridge's address (http://host:port), or null: no bridge, and <see cref="BridgeWhy"/> says why. With an address,
    /// BridgeWhy is the address, for the log - never the secret.</summary>
    public string? Bridge { get; init; }
    public string BridgeSecret { get; init; } = "";
    public string BridgeWhy { get; init; } = "";

    /// <summary>Whether the HUD item's Workshop page may be visited (ItemWatch), and the name of the switch that says so, for its notes.</summary>
    public bool ItemPage { get; init; } = true;
    public string ItemPageSwitch { get; init; } = "";

    /// <summary>The least time between two Steam Web API requests (SteamWebApi.Gap): steam.apiGapSeconds, 2 s without the file.</summary>
    public double ApiGapSeconds { get; init; } = SteamWebApi.MinGapSeconds;

    /// <summary>The settings file's values.</summary>
    public static PluginSetup FromFile(CoreSettings s) => new()
    {
        Addon = s.Hud.Addon.Trim(),
        AddonFrom = "hud.addon",
        Bridge = s.Bridge.Secret != "" ? s.Bridge.Url : null,
        BridgeSecret = s.Bridge.Secret,
        BridgeWhy = s.Bridge.Secret != "" ? s.Bridge.Url : "bridge.secret is empty",
        ItemPage = s.Hud.ItemPage,
        ItemPageSwitch = "hud.itemPage",
        ApiGapSeconds = s.Steam.ApiGapSeconds,
    };

    /// <summary>The environment's values, as the plugin has always read them: the addon from addonVariable or its old name (HudAddon), the
    /// bridge from CS2_LOG_HTTP_URL, the page visit off when pageVariable says so (pageOff: HudCheck.PageOff).</summary>
    public static PluginSetup FromEnvironment(Func<string, string?> env, string addonVariable, string oldAddonVariable, string pageVariable, Func<string?, bool> pageOff)
    {
        var (addon, from) = HudAddon.Read(env, addonVariable, oldAddonVariable);
        var (bridge, secret, why) = FromLogUrl(env(LogUrlVariable));
        return new()
        {
            Addon = addon,
            AddonFrom = from,
            Bridge = bridge,
            BridgeSecret = secret,
            BridgeWhy = why,
            ItemPage = !pageOff(env(pageVariable)),
            ItemPageSwitch = pageVariable,
        };
    }

    /// <summary>The bridge's address and secret from the log push address (http://bridge:8080/log/&lt;secret&gt;; a "\/" in it reads as "/"), or
    /// no address and why.</summary>
    public static (string? Bridge, string Secret, string Why) FromLogUrl(string? url)
    {
        var log = (url ?? "").Replace("\\/", "/").Trim();
        var at = log.LastIndexOf("/log/", StringComparison.Ordinal);
        if (at < 0 || at + 5 >= log.Length)
            return (null, "", $"{LogUrlVariable} has no /log/<secret> to derive the bridge from");
        return (log[..at], log[(at + 5)..], log[..at]);
    }
}
