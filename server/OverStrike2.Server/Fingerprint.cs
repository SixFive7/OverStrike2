using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE VERSION OF OUR OWN PARTS: what decides that a new version is on disk and the engine must restart to run it. Five parts,
/// each a SHA-256 of what it is made of - the plugins' folder (every file and its path), this program's own file as shipped (the
/// read-only one pre.sh copies), pre.sh, the mode cfgs (cfg/*_server.cfg, the operator's files the game executes after its own), and the
/// settings file's values the engine side reads (every key the plugin reads, and plugin.enabled; a key only the bridge or the sync reads
/// changes nothing here). `install` records what it put in place (update-watch/installed.json); the watcher compares what is on disk with
/// it. "none" stands for a part that is not there, the same way both times.</summary>
public sealed record VersionParts(string Plugin, string Server, string PreSh, string Cfgs, string Settings)
{
    public string Total => Fingerprint.Hex(Encoding.UTF8.GetBytes($"plugin={Plugin}\nserver={Server}\npre.sh={PreSh}\ncfgs={Cfgs}\nsettings={Settings}\n"))[..16];

    public IEnumerable<(string Name, string Value)> Each() =>
        [("plugin", Plugin), ("server", Server), ("pre.sh", PreSh), ("cfgs", Cfgs), ("settings", Settings)];

    /// <summary>The names of the parts that differ from another version.</summary>
    public List<string> Changed(VersionParts other) => Each().Zip(other.Each()).Where(p => p.First.Value != p.Second.Value).Select(p => p.First.Name).ToList();

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var (name, value) in Each())
            o[name] = value;
        return o;
    }

    public static VersionParts? FromJson(JsonNode? n) =>
        n is JsonObject o && new[] { "plugin", "server", "pre.sh", "cfgs", "settings" }.All(k => o[k] is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            ? new VersionParts(o["plugin"]!.GetValue<string>(), o["server"]!.GetValue<string>(), o["pre.sh"]!.GetValue<string>(), o["cfgs"]!.GetValue<string>(), o["settings"]!.GetValue<string>())
            : null;
}

/// <summary>What `install` put in place before this engine started: update-watch/installed.json.</summary>
public sealed record Installed(VersionParts Parts, long At, string? ServerFrom, string? SettingsPath)
{
    public string Fingerprint => Parts.Total;

    public static Installed? Read(string path)
    {
        var n = Files.ReadJson(path) as JsonObject;
        if (n is null || KeepSet.Integer(n["version"]) != 1 || VersionParts.FromJson(n["parts"]) is not { } parts)
            return null;
        return new Installed(parts, KeepSet.Integer(n["at"]) ?? 0, n["serverFrom"] is JsonValue sf ? sf.GetValue<string>() : null,
                             n["settings"] is JsonValue st ? st.GetValue<string>() : null);
    }

    public void Write(string path)
    {
        var o = new JsonObject
        {
            ["version"] = 1,
            ["at"] = At,
            ["fingerprint"] = Fingerprint,
            ["parts"] = Parts.ToJson(),
            ["serverFrom"] = ServerFrom,
            ["settings"] = SettingsPath,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Files.WriteAtomic(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n");
    }
}

public static class Fingerprint
{
    public const string None = "none";

    public static string Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The settings file to read: --settings, else OVERSTRIKE2_SETTINGS, else the default path.</summary>
    public static string SettingsPath(string? given, Func<string, string?> env) =>
        given ?? (env(Core.Settings.PathVariable) is { Length: > 0 } p ? p : Core.Settings.DefaultPath);

    static IEnumerable<string> CfgFiles(ServerPaths p) =>
        Directory.Exists(p.CfgDir) ? Directory.EnumerateFiles(p.CfgDir, "*_server.cfg").OrderBy(x => x, StringComparer.Ordinal) : [];

    static IEnumerable<string> PluginFiles(ServerPaths p) =>
        Directory.Exists(p.Plugins)
            ? Directory.EnumerateFiles(p.Plugins, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                .OrderBy(f => Path.GetRelativePath(p.Plugins, f).Replace('\\', '/'), StringComparer.Ordinal)
            : [];

    static string HashFiles(string root, IEnumerable<string> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var any = false;
        foreach (var f in files)
        {
            any = true;
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            var data = File.ReadAllBytes(f);
            sha.AppendData(Encoding.UTF8.GetBytes($"{rel}\0{data.Length}\0"));
            sha.AppendData(data);
        }
        return any ? Convert.ToHexStringLower(sha.GetHashAndReset()) : None;
    }

    static string HashFile(string? path)
    {
        if (path is null || !File.Exists(path))
            return None;
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>The engine side's view of the settings, from where core's Load finds them (the file, or its text in an environment variable),
    /// hashed: every key the plugin reads, and plugin.enabled. None when there are none; throws InvalidDataException when they are there but
    /// cannot be used (then nothing may be decided on them).</summary>
    public static string HashSettings(string path, Func<string, string?> env)
    {
        var plugin = Core.Settings.Load(Part.Plugin, path, env);
        if (!plugin.Found)
            return None;
        var server = Core.Settings.Load(Part.Server, path, env);
        var fatal = plugin.Fatal ?? server.Fatal;
        if (fatal is not null)
            throw new InvalidDataException($"the settings are not usable: {fatal}");
        var lines = new StringBuilder();
        foreach (var key in Core.Settings.Keys)
        {
            if ((key.Readers & Part.Plugin) != 0)
                lines.Append(key.Path).Append('=').Append(key.Show(plugin)).Append('\n');
            else if (key.Path == "plugin.enabled")
                lines.Append(key.Path).Append('=').Append(key.Show(server)).Append('\n');
        }
        return Hex(Encoding.UTF8.GetBytes(lines.ToString()));
    }

    /// <summary>The five parts as they are on disk now; throws IOException (or InvalidDataException for a settings file that cannot be
    /// used) when one cannot be read - then nothing may be decided on it.</summary>
    public static VersionParts Compute(ServerPaths p, string? serverFrom, string settingsPath, Func<string, string?> env) =>
        new(HashFiles(p.Plugins, PluginFiles(p)), HashFile(serverFrom), HashFile(p.PreSh), HashFiles(p.CfgDir, CfgFiles(p)), HashSettings(settingsPath, env));

    /// <summary>A cheap look at the same files: their paths, sizes and times. Only when it changes is the version computed again.</summary>
    public static string Signature(ServerPaths p, string? serverFrom, string settingsPath)
    {
        var sb = new StringBuilder();
        void Add(string? f)
        {
            if (f is null)
                return;
            var i = new FileInfo(f);
            sb.Append(f).Append('|').Append(i.Exists ? i.Length.ToString(CultureInfo.InvariantCulture) + "|" + i.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "-").Append('\n');
        }
        foreach (var f in PluginFiles(p))
            Add(f);
        Add(serverFrom);
        Add(p.PreSh);
        foreach (var f in CfgFiles(p))
            Add(f);
        Add(settingsPath);
        return Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
