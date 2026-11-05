// Where a part's settings come from while no install step writes the settings file yet: the file (core/Settings.cs) when there is one
// that can be used, else the environment variables the part has always read. Shared like Hud.cs: the bridge compiles it (BridgeConfig.cs,
// tested in --selftest) and the OverStrike2 plugin links it in (PluginSetup.cs, tested in its self-test). Plain BCL and core only.
using CoreSettings = OverStrike2.Core.Settings;
using Part = OverStrike2.Core.Part;

/// <summary>A part's settings file, read, and whether the part takes its values from it: only when the file was there and could be used.
/// With no file at the path - nothing writes one yet - or one that cannot be used (unreadable, no JSON object, a schema this code does not
/// know), the part reads its environment as it always has: the switch to the file is the install step's alone, and a broken file never
/// takes a running deployment's settings away. <see cref="Line"/> is the line the part logs at its start - where its settings came from,
/// and the keys with a problem or a warning by name only, never a value (core's reasons can quote what the file says).</summary>
public sealed class SettingsSource
{
    /// <summary>The file as read: the part's values when <see cref="FromFile"/>; else every value is its default and unused.</summary>
    public CoreSettings File { get; }

    /// <summary>The path read: OVERSTRIKE2_SETTINGS, else /etc/overstrike2/overstrike2.jsonc.</summary>
    public string Path { get; }

    /// <summary>Whether the part takes the file's values (its problems and warnings aside): the file was there and could be used.</summary>
    public bool FromFile { get; }

    SettingsSource(CoreSettings file, string path)
    {
        File = file;
        Path = path;
        FromFile = file.Fatal == null && file.Source == path; // Settings.Load names the path as the source only when it found the file
    }

    /// <summary>Reads the part's settings file. Never throws: a file that is not there or cannot be used means the environment.</summary>
    public static SettingsSource Load(Part part, Func<string, string?>? env = null, Func<string, string>? readFile = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var path = env(CoreSettings.PathVariable) is { Length: > 0 } p ? p : CoreSettings.DefaultPath;
        return new SettingsSource(CoreSettings.Load(part, path, env, readFile), path);
    }

    /// <summary>Whether the line is worth a warning rather than a plain note: a problem in the file used, or a file that cannot be used.</summary>
    public bool Troubled => FromFile ? File.Problems.Count > 0 : File.Fatal != null;

    /// <summary>The one line for the part's log, e.g. "settings: the settings file /etc/overstrike2/overstrike2.jsonc - 1 problem (server.port)"
    /// or "settings: the environment (no settings file at /etc/overstrike2/overstrike2.jsonc)". No key's value is in it.</summary>
    public string Line =>
        FromFile ? $"settings: the settings file {Path}{Keys("problem", File.Problems)}{Keys("warning", File.Warnings)}"
        : File.Fatal is { } fatal ? $"settings: the environment - the settings file {Path} cannot be used ({fatal})"
        : $"settings: the environment (no settings file at {Path})";

    /// <summary>" - 2 problems (server.port, bridge.admins)": the keys by name, each once - every line of core's starts with its key.</summary>
    static string Keys(string what, List<string> lines)
    {
        if (lines.Count == 0)
            return "";
        var keys = lines.Select(l => l.IndexOf(':') is var i and > 0 ? l[..i] : "?").Distinct();
        return $" - {lines.Count} {what}{(lines.Count == 1 ? "" : "s")} ({string.Join(", ", keys)})";
    }
}
