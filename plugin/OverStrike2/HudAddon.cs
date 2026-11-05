namespace OverStrike2;

/// <summary>The HUD addon's Workshop id, from the cs2 service's environment: Hud.AddonVariable (OS2_HUD_ADDON), or - only while that is not set
/// at all - its old name, Hud.OldAddonVariable (GUNGAME_HUD_ADDON): a one-time fallback for a deployment that still says it the old way, to
/// be removed in the next release. Set but empty means no HUD. No CounterStrikeSharp type in it: the self-test runs it as it is.</summary>
public static class HudAddon
{
    /// <summary>The id (trimmed; empty: no HUD) and the name of the variable it came from.</summary>
    public static (string Addon, string From) Read(Func<string, string?> env, string name, string oldName) =>
        env(name) is { } now ? (now.Trim(), name) : env(oldName) is { } old ? (old.Trim(), oldName) : ("", name);
}
