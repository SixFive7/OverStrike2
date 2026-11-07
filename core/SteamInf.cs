namespace OverStrike2.Core;

/// <summary>steam.inf, the game's own version file (game/csgo/steam.inf in its install): its PatchVersion field, and only that field - never
/// ServerVersion or ClientVersion, which Valve's UpToDateCheck takes for current whatever they say (os2-server's Versions, the version trap).
/// Both server programs read the game's version here.</summary>
public static class SteamInf
{
    /// <summary>The PatchVersion field of steam.inf's text, or null when it has none.</summary>
    public static string? PatchVersion(string steamInf)
    {
        foreach (var line in steamInf.Split('\n'))
        {
            var i = line.IndexOf('=');
            var key = i < 0 ? line : line[..i];
            if (key.Trim() == "PatchVersion")
            {
                var value = i < 0 ? "" : line[(i + 1)..].Trim();
                return value.Length > 0 ? value : null;
            }
        }
        return null;
    }

    /// <summary>The PatchVersion of a steam.inf file, or null when it cannot be read or has none.</summary>
    public static string? ReadPatchVersion(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[65536];
            var n = f.Read(buf, 0, buf.Length);
            return PatchVersion(System.Text.Encoding.UTF8.GetString(buf, 0, n));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
