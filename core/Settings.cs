using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverStrike2.Core;

/// <summary>The programs that read the settings file: the bridge, the plugin, os2-server (the restart watcher and the maps folder, in the game
/// server's container) and os2-sync (the Workshop sync). Each one checks the keys it uses and leaves the others alone.</summary>
[Flags]
public enum Part
{
    Bridge = 1,
    Plugin = 2,
    Server = 4,
    Sync = 8,
    All = Bridge | Plugin | Server | Sync,
}

/// <summary>A local time of day, "07:00"; null in a setting means off.</summary>
public readonly record struct TimeOfDay(int Hour, int Minute)
{
    public override string ToString() => $"{Hour:00}:{Minute:00}";

    /// <summary>"HH:MM" (or "H:MM") into a time; "off" into null. False for anything else.</summary>
    public static bool TryParse(string? text, out TimeOfDay? value)
    {
        value = null;
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t == "off")
            return true;
        var parts = t.Split(':');
        if (parts.Length == 2 && parts[0].Length is 1 or 2 && parts[1].Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && h < 24 && m < 60)
        {
            value = new TimeOfDay(h, m);
            return true;
        }
        return false;
    }
}

/// <summary>THE SETTINGS FILE: one structured file every part reads - the bridge, the plugin, os2-server and os2-sync (<see cref="Part"/>).
/// JSON with comments and trailing commas: its text in <see cref="ContentVariable"/>, or a file at
/// <see cref="DefaultPath"/> in each container (OVERSTRIKE2_SETTINGS points a test elsewhere). docs/settings.md describes every key;
/// docs/settings.schema.json is made from the same table as this reader (<see cref="Keys"/>), and the self-test holds the two to each other.
///
/// The rules: a reader checks the keys it uses (each key names its readers) and leaves the others alone. A value of the wrong type or
/// out of its range keeps the default and is a <see cref="Problems"/> line - a typo must never silently widen a safety limit; a hard
/// limit clamps. A key nobody knows is a <see cref="Warnings"/> line, never fatal, so a part one release ahead never stops another.
/// A file that cannot be read, is no JSON object or names a schema this code does not know sets <see cref="Fatal"/>: every value is
/// then its default, and no part runs on it (the server programs and the bridge exit <see cref="RefusedExit"/>, the plugin does not
/// load). So do no settings at all, and a required key (<see cref="Key.Required"/>: hud.addon, "a server without the HUD cannot exist")
/// that is missing, empty or wrong, for the readers of that key: there is no default to run on, and the rest of the file is still read,
/// for doctor to show. A string value "env:NAME" is read from the environment variable NAME and "file:PATH" from that file (trimmed) -
/// the way a secret stays where it is kept: the image's own variables (CS2_RCONPW, ...) or a mounted secret file.</summary>
public sealed class Settings
{
    public const int Schema = 1;
    public const string DefaultPath = "/etc/overstrike2/overstrike2.jsonc";
    public const string PathVariable = "OVERSTRIKE2_SETTINGS";

    /// <summary>The environment variable that holds the file's text itself: how the settings stay inline in a compose file, on a read-only
    /// container too and across a recreate from inspect, neither of which an inline compose config survives.</summary>
    public const string ContentVariable = "OVERSTRIKE2_SETTINGS_CONTENT";

    /// <summary><see cref="Source"/> of settings read from <see cref="ContentVariable"/>: the variable's name, never its value.</summary>
    public const string ContentSource = "the environment variable " + ContentVariable;

    /// <summary>The exit code of a server program that refuses to run because <see cref="Fatal"/> is set, and of an os2-server install that
    /// finds no MultiAddonManager (EX_CONFIG of sysexits.h). The launcher pre.sh stops the container's start on exactly this code, and no
    /// other failure of the programs uses it.</summary>
    public const int RefusedExit = 78;

    public ServerSection Server { get; } = new();
    public BridgeSection Bridge { get; } = new();
    public HudSection Hud { get; } = new();
    public PluginSection Plugin { get; } = new();
    public RestartsSection Restarts { get; } = new();
    public MapsSection Maps { get; } = new();
    public SyncSection Sync { get; } = new();
    public SteamSection Steam { get; } = new();
    public PathsSection Paths { get; } = new();

    /// <summary>Values this reader uses that were wrong: each kept its default or was clamped, and the line says which.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>What was ignored: keys nobody knows, a reference that found nothing.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Why the file cannot be used, or null.</summary>
    public string? Fatal { get; private set; }

    /// <summary>Where the values came from: the file's path, <see cref="ContentSource"/>, or "(defaults)".</summary>
    public string Source { get; private set; } = "(defaults)";

    /// <summary>Whether there was a file or a <see cref="ContentVariable"/> to read at all, usable or not: false only for the defaults of
    /// no settings anywhere.</summary>
    public bool Found => Source != "(defaults)";

    public Part Reader { get; private set; } = Part.All;

    /// <summary>The settings file a program reads: the one it was named (--settings), else OVERSTRIKE2_SETTINGS, else <see cref="DefaultPath"/>
    /// - which is naming nothing: Load then takes the settings' text in <see cref="ContentVariable"/> first.</summary>
    public static string PathFor(string? given, Func<string, string?> env) => given ?? (env(PathVariable) is { Length: > 0 } p ? p : DefaultPath);

    /// <summary>The settings for one reader. Where from, the first that says: a file named for a test (path, which is what --settings gives,
    /// else OVERSTRIKE2_SETTINGS); the file's text in <see cref="ContentVariable"/>, by the same rules and the same fatal path as a file;
    /// the file at <see cref="DefaultPath"/>. Naming DefaultPath is naming nothing: it is what a caller passes that was told no other.
    /// Nothing at all: every value is its default, a warning says so, and <see cref="Fatal"/> too - no part runs without its settings, and
    /// for a reader of hud.addon it names that key as well. Never throws.</summary>
    public static Settings Load(Part reader, string? path = null, Func<string, string?>? env = null, Func<string, string>? readFile = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        readFile ??= File.ReadAllText;
        path = PathFor(path, env);
        if (path == DefaultPath && env(ContentVariable) is { Length: > 0 } content)
        {
            var inline = Parse(content, reader, env, readFile);
            inline.Source = ContentSource;
            if (inline.Fatal is { } bad)
                inline.Fatal = $"{ContentSource}: {bad}";
            return inline;
        }
        string text;
        try
        {
            text = readFile(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            var s = Parse("{ \"schema\": " + Schema + " }", reader, env, readFile);   // the defaults, their references read
            s.Source = "(defaults)";
            s.Warnings.Insert(0, $"no settings file at {path}: every value is its default");
            var none = path == DefaultPath ? $"no settings file at {path} and no {ContentVariable}" : $"no settings file at {path}";
            s.Fatal = s.Fatal is { } missing ? $"{none} - {missing}" : none;
            return s;
        }
        catch (Exception e)
        {
            var s = new Settings { Reader = reader, Source = path };
            s.Fatal = $"{path} could not be read ({e.Message})";
            return s;
        }
        var parsed = Parse(text, reader, env, readFile);
        parsed.Source = path;
        return parsed;
    }

    /// <summary>The settings for one reader from the text of a settings file. Never throws.</summary>
    public static Settings Parse(string text, Part reader, Func<string, string?>? env = null, Func<string, string>? readFile = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        readFile ??= File.ReadAllText;
        var s = new Settings { Reader = reader, Source = "(text)" };
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            s.Fatal = $"not JSON: {e.Message}";
            return s;
        }
        if (root is not JsonObject obj)
        {
            s.Fatal = "not a JSON object";
            return s;
        }
        if (obj["schema"] is not JsonValue sv || !sv.TryGetValue<int>(out var schema))
            s.Problems.Add($"schema: missing or not a number - read as schema {Schema}");
        else if (schema != Schema)
        {
            s.Fatal = $"schema {schema}: this program knows schema {Schema} only";
            return s;
        }
        var known = new HashSet<string>(Keys.Select(k => k.Path)) { "schema", "$schema" };
        var sections = new HashSet<string>(Keys.SelectMany(k => Prefixes(k.Path)));
        foreach (var (path, why) in Unknown(obj, "", known, sections))
            s.Warnings.Add($"{path}: {why} - ignored");
        foreach (var key in Keys)
        {
            if ((key.Readers & reader) == 0)
                continue;
            var node = Find(obj, key.Path);
            if (node is null && (key.Default.StartsWith("env:", StringComparison.Ordinal) || key.Default.StartsWith("file:", StringComparison.Ordinal)))
                node = JsonValue.Create(key.Default);        // a default that is a reference is read like a value
            if (node is null)
            {
                if (key.Required is { } rule)
                    s.Fatal ??= $"{key.Path}: missing - required: {rule}";
                continue;
            }
            var ctx = new Ctx(s, key, env, readFile);
            try
            {
                key.Apply(s, node, ctx);
            }
            catch (InvalidOperationException e) when (key.Required is { } rule)
            {
                s.Fatal ??= $"{key.Path}: {e.Message} - required: {rule}";
            }
            catch (InvalidOperationException e)
            {
                s.Problems.Add($"{key.Path}: {e.Message} - kept the default {key.Default}");
            }
        }
        s.CrossChecks();
        return s;
    }

    void CrossChecks()
    {
        if ((Reader & Part.Sync) != 0 && Sync.ApiRunCap > Sync.ApiDayCap)
        {
            Problems.Add($"sync.apiRunCap: {Sync.ApiRunCap} is above sync.apiDayCap ({Sync.ApiDayCap}) - clamped to it");
            Sync.ApiRunCap = Sync.ApiDayCap;
        }
    }

    /// <summary>One line per key this reader uses, its value as read; secrets show as set or empty, never their value.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var key in Keys.Where(k => (k.Readers & Reader) != 0))
            yield return $"{key.Path} = {(key.Secret ? (string.IsNullOrEmpty(key.Show(this)) ? "(empty)" : "(set)") : key.Show(this))}";
    }

    /// <summary>Whether <see cref="StartLine"/> is worth a warning rather than a plain note: settings that cannot be used, or a problem in them.</summary>
    public bool Troubled => Fatal != null || Problems.Count > 0;

    /// <summary>The one line the bridge and the plugin log at their start - where their settings came from, and the keys with a problem or
    /// a warning by name only, never a value (core's reasons can quote what the file says): "settings: the settings file
    /// /etc/overstrike2/overstrike2.jsonc - 1 problem (server.port)", "settings: the environment variable OVERSTRIKE2_SETTINGS_CONTENT", or
    /// why they stop.</summary>
    public string StartLine =>
        !Found ? $"settings: {Fatal} - not starting: no part runs without its settings"
        : Fatal is { } why ? $"settings: {Where} cannot be used ({why}) - not starting: no part runs on settings it cannot use"
        : $"settings: {Where}{Named("problem", Problems)}{Named("warning", Warnings)}";

    string Where => Source == ContentSource ? Source : $"the settings file {Source}";

    /// <summary>" - 2 problems (server.port, bridge.port)": the keys by name, each once - every line of a key starts with it.</summary>
    static string Named(string what, List<string> lines)
    {
        if (lines.Count == 0)
            return "";
        var keys = lines.Select(l => l.IndexOf(':') is var i and > 0 ? l[..i] : "?").Distinct();
        return $" - {lines.Count} {what}{(lines.Count == 1 ? "" : "s")} ({string.Join(", ", keys)})";
    }

    static IEnumerable<string> Prefixes(string path)
    {
        var parts = path.Split('.');
        for (var i = 1; i < parts.Length; i++)
            yield return string.Join('.', parts.Take(i));
    }

    /// <summary>The keys of the file this code does not know, and sections that are no object, with why.</summary>
    static IEnumerable<(string Path, string Why)> Unknown(JsonObject obj, string prefix, HashSet<string> known, HashSet<string> sections)
    {
        foreach (var (name, value) in obj)
        {
            var path = prefix.Length == 0 ? name : prefix + "." + name;
            if (known.Contains(path))
                continue;
            if (!sections.Contains(path))
                yield return (path, "no such setting");
            else if (value is JsonObject inner)
                foreach (var u in Unknown(inner, path, known, sections))
                    yield return u;
            else
                yield return (path, $"is {Kind(value)}, not a section - its settings keep their defaults");
        }
    }

    static JsonNode? Find(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(part, out node))
                return null;
        }
        return node;
    }

    // ------------------------------------------------------------------------------------------------------------ the table

    /// <summary>One key of the file: its path, who reads it, its JSON type and default (for the docs and the schema), one line about it,
    /// and how it is read. Required: what a key the file cannot do without must hold, in words -
    /// null for every other key; Pattern: its form in the JSON schema.</summary>
    public sealed record Key(string Path, Part Readers, string Type, string Default, string Doc, bool Secret,
                             Action<Settings, JsonNode, Ctx> Apply, Func<Settings, string> Show, string[]? Choices = null, double? Min = null,
                             double? Max = null, string? Required = null, string? Pattern = null);

    /// <summary>The context of reading one key: its references and its problems.</summary>
    public sealed class Ctx(Settings settings, Key key, Func<string, string?> env, Func<string, string> readFile)
    {
        public string Resolve(string value)
        {
            if (value.StartsWith("env:", StringComparison.Ordinal))
            {
                var name = value[4..];
                var v = env(name);
                if (v is null)
                    settings.Warnings.Add($"{key.Path}: the environment variable {name} is not set - empty");
                return v ?? "";
            }
            if (value.StartsWith("file:", StringComparison.Ordinal))
            {
                var path = value[5..];
                try
                {
                    return readFile(path).Trim();
                }
                catch (Exception e)
                {
                    settings.Warnings.Add($"{key.Path}: {path} could not be read ({e.GetType().Name}) - empty");
                    return "";
                }
            }
            return value;
        }

        public void Clamped(string why) => settings.Problems.Add($"{key.Path}: {why}");
    }

    static string Text(JsonNode node, Ctx ctx)
    {
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return ctx.Resolve(v.GetValue<string>());
        throw new InvalidOperationException($"not a string ({Kind(node)})");
    }

    static bool Flag(JsonNode node)
    {
        if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            return v.GetValue<bool>();
        throw new InvalidOperationException($"not true or false ({Kind(node)})");
    }

    static double Number(JsonNode node)
    {
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d))
            return d;
        throw new InvalidOperationException($"not a number ({Kind(node)})");
    }

    static List<string> TextList(JsonNode node, Ctx ctx, Func<string, bool>? each = null, string? what = null)
    {
        if (node is not JsonArray a)
            throw new InvalidOperationException($"not a list ({Kind(node)})");
        var list = new List<string>();
        foreach (var item in a)
        {
            if (item is null)
                throw new InvalidOperationException("a list entry is null");
            var t = Text(item, ctx);
            if (each != null && !each(t))
                throw new InvalidOperationException($"'{Clip(t)}' is not {what}");
            list.Add(t);
        }
        return list;
    }

    static string Kind(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "a list",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "true or false",
            _ => v.GetValueKind().ToString(),
        },
        _ => "?",
    };

    static string Clip(string s) => s.Length <= 40 ? s : s[..37] + "...";

    static bool Digits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
    static bool SteamId64(string s) => s.Length == 17 && Digits(s) && s.StartsWith("7656119", StringComparison.Ordinal);
    static bool Date(string s) => s.Length == 0 || DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    static Key Str(string path, Part readers, string def, string doc, Func<Settings, string> get, Action<Settings, string> set,
                   Func<string, bool>? valid = null, string? what = null, bool secret = false) =>
        new(path, readers, "string", def, doc, secret, (s, n, c) =>
        {
            var t = Text(n, c);
            if (valid != null && !valid(t))
                throw new InvalidOperationException($"'{(secret ? "..." : Clip(t))}' is not {what}");
            set(s, t);
        }, get);

    /// <summary>A string the file cannot do without: <see cref="Parse"/> makes it fatal for its readers when it is missing or this throws. Its
    /// default is empty and passes nothing, and the words never quote the value: they end up in the parts' logs.</summary>
    static Key Must(string path, Part readers, string doc, string rule, string pattern, Func<Settings, string> get, Action<Settings, string> set,
                    Func<string, bool> valid, string what) =>
        new(path, readers, "string", "", doc, false, (s, n, c) =>
        {
            var t = Text(n, c);
            if (t.Length == 0)
                throw new InvalidOperationException("empty");
            if (!valid(t))
                throw new InvalidOperationException("not " + what);
            set(s, t);
        }, get, Required: rule, Pattern: pattern);

    static Key Choice(string path, Part readers, string def, string[] choices, string doc, Func<Settings, string> get,
                      Action<Settings, string> set) =>
        new(path, readers, "string", def, doc, false, (s, n, c) =>
        {
            var t = Text(n, c).Trim().ToLowerInvariant();
            if (!choices.Contains(t))
                throw new InvalidOperationException($"'{Clip(t)}' is not one of {string.Join(", ", choices)}");
            set(s, t);
        }, get, choices);

    static Key Bool(string path, Part readers, bool def, string doc, Func<Settings, bool> get, Action<Settings, bool> set) =>
        new(path, readers, "boolean", def ? "true" : "false", doc, false, (s, n, _) => set(s, Flag(n)), s => get(s) ? "true" : "false");

    static Key Int(string path, Part readers, int def, int min, int max, string doc, Func<Settings, int> get, Action<Settings, int> set,
                   bool clampHigh = false) =>
        new(path, readers, "integer", def.ToString(CultureInfo.InvariantCulture), doc, false, (s, n, c) =>
        {
            var d = Number(n);
            if (d != Math.Floor(d))
                throw new InvalidOperationException($"{d.ToString(CultureInfo.InvariantCulture)} is not a whole number");
            if (clampHigh && d > max)
            {
                c.Clamped($"{d.ToString(CultureInfo.InvariantCulture)} is above the hard limit {max} - clamped to it");
                d = max;
            }
            if (d < min || d > max)
                throw new InvalidOperationException($"{d.ToString(CultureInfo.InvariantCulture)} is outside {min}..{max}");
            set(s, (int)d);
        }, s => get(s).ToString(CultureInfo.InvariantCulture), Min: min, Max: max);

    static Key Num(string path, Part readers, double def, double min, double max, string doc, Func<Settings, double> get,
                   Action<Settings, double> set, bool clampLow = false) =>
        new(path, readers, "number", def.ToString(CultureInfo.InvariantCulture), doc, false, (s, n, c) =>
        {
            var d = Number(n);
            if (clampLow && d < min)
            {
                c.Clamped($"{d.ToString(CultureInfo.InvariantCulture)} is below the hard limit {min.ToString(CultureInfo.InvariantCulture)} - clamped to it");
                d = min;
            }
            if (d < min || d > max)
                throw new InvalidOperationException($"{d.ToString(CultureInfo.InvariantCulture)} is outside {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}");
            set(s, d);
        }, s => get(s).ToString(CultureInfo.InvariantCulture), Min: min, Max: max);

    static Key List(string path, Part readers, string def, string doc, Func<Settings, List<string>> get,
                    Action<Settings, List<string>> set, Func<string, bool>? each = null, string? what = null) =>
        new(path, readers, "string list", def, doc, false, (s, n, c) => set(s, TextList(n, c, each, what)),
            s => "[" + string.Join(", ", get(s)) + "]");

    static Key Time(string path, Part readers, string def, string doc, Func<Settings, TimeOfDay?> get, Action<Settings, TimeOfDay?> set) =>
        new(path, readers, "time", def, doc, false, (s, n, c) =>
        {
            var t = Text(n, c);
            if (!TimeOfDay.TryParse(t, out var v))
                throw new InvalidOperationException($"'{Clip(t)}' is not HH:MM or off");
            set(s, v);
        }, s => get(s)?.ToString() ?? "off");

    // Who reads a key, as the docs' "Read by" column names them: the bridge, the plugin, os2-server ("server") and os2-sync ("sync").
    const Part B = Part.Bridge, P = Part.Plugin, S = Part.Server, Y = Part.Sync;

    /// <summary>EVERY KEY OF THE FILE, in the order the docs list them.</summary>
    public static readonly Key[] Keys =
    [
        // server: the game server, as the other parts see it
        Str("server.name", Y, "CS2 server", "The server's name in the sync's mails and on the dashboard (the image's CS2_SERVERNAME is the one players see).",
            s => s.Server.Name, (s, v) => s.Server.Name = v),
        Str("server.host", B | Y, "cs2", "How the bridge and the sync reach the game server: its container's name on their network.",
            s => s.Server.Host, (s, v) => s.Server.Host = v, v => v.Length > 0, "a host name"),
        Int("server.port", B | S | Y, 27015, 1, 65535, "The game server's port: A2S (UDP) and RCON (TCP). The image's CS2_PORT must say the same.",
            s => s.Server.Port, (s, v) => s.Server.Port = v),
        Str("server.rconPassword", B | S, "env:CS2_RCONPW", "The RCON password: by default the image's own CS2_RCONPW, which it needs anyway, so the bridge's container gets that variable too.",
            s => s.Server.RconPassword, (s, v) => s.Server.RconPassword = v, secret: true),
        Str("server.startMap", B | S, "ar_shoots", "The map a server start loads; the image's CS2_STARTMAP must say the same.",
            s => s.Server.StartMap, (s, v) => s.Server.StartMap = v, v => v.Length > 0, "a map name"),

        // bridge
        Str("bridge.url", P | S | Y, "http://bridge:8080", "How the game server (the plugin, the restart watcher) and the sync reach the bridge.",
            s => s.Bridge.Url, (s, v) => s.Bridge.Url = v.TrimEnd('/'), v => v.StartsWith("http://", StringComparison.Ordinal) || v.StartsWith("https://", StringComparison.Ordinal), "an http(s) URL"),
        Int("bridge.port", B, 8080, 1, 65535, "The port the bridge listens on.",
            s => s.Bridge.Port, (s, v) => s.Bridge.Port = v),
        Str("bridge.secret", Part.All, "", "The secret in the bridge's addresses (/log, /ready, /quitting, /removed, /reload ...): letters and digits. The image's CS2_LOG_HTTP_URL ends in /log/<it>.",
            s => s.Bridge.Secret, (s, v) => s.Bridge.Secret = v, v => v.Length == 0 || v.All(char.IsAsciiLetterOrDigit), "letters and digits", secret: true),
        List("bridge.officialMaps", B, "[\"ar_baggage\", \"ar_pool_day\", \"ar_shoots\", \"ar_shoots_night\"]", "The official maps the votes and the random picks may offer.",
            s => s.Bridge.OfficialMaps, (s, v) => s.Bridge.OfficialMaps = v, v => v.Length > 0, "a map name"),
        Int("bridge.discoveryStock", B | S | Y, 10, 0, 40, "The discovery stock: per mode, how many maps new to the server the bridge rolls ahead (the vote's surprise, the automatic picks), which the game server downloads in the background; 0 turns it off. The keep set wants each of the stock's lanes this long.",
            s => s.Bridge.DiscoveryStock, (s, v) => s.Bridge.DiscoveryStock = v),
        Int("bridge.diceMaxMb", B, 300, 0, 100000, "The most a map the dice roll may take to download, in MB: the start roll, the way back to Arms Race, Random and nobody voting, a replaced card, and the discovery stock's rolls. A map new to a player's game asks them to confirm its download, and a big one keeps them waiting. Official maps always fit; when no eligible map fits, the dice take the smallest. 0: no cap.",
            s => s.Bridge.DiceMaxMb, (s, v) => s.Bridge.DiceMaxMb = v),
        Int("bridge.changeDelaySeconds", B, 8, 0, 120, "The countdown before a map change.",
            s => s.Bridge.ChangeDelaySeconds, (s, v) => s.Bridge.ChangeDelaySeconds = v),
        Int("bridge.restartCooldownSeconds", B, 300, 0, 86400, "The least time between two !restart requests.",
            s => s.Bridge.RestartCooldownSeconds, (s, v) => s.Bridge.RestartCooldownSeconds = v),

        // hud: the in-game menu
        Must("hud.addon", P | S | Y, "Required: a server without the HUD cannot exist. The Workshop id of the HUD addon, digits only: the game server mounts it, and every client downloads it and keeps it across every map change. Missing, empty or not digits, the file cannot be used: the server programs refuse to run and the plugin does not load.",
             "the Workshop id of the HUD addon, digits only", "^([0-9]+|env:.+|file:.+)$", s => s.Hud.Addon, (s, v) => s.Hud.Addon = v, Digits, "digits"),
        Choice("hud.tab", B, "down", ["off", "down"], "The TAB chord: down puts the board down while TAB is held; off leaves TAB alone.",
            s => s.Hud.Tab, (s, v) => s.Hud.Tab = v),
        Choice("hud.check", B, "warn", ["off", "warn", "enforce"], "The HUD check: warn a player not seen clicking in the HUD of this release, and with enforce disconnect them; warn by default, so every player's HUD version is learned at their first click.",
            s => s.Hud.Check, (s, v) => s.Hud.Check = v),
        Bool("hud.itemPage", P, true, "Visit the HUD item's Workshop page to see whether Steam shows it to everybody (off on a test copy).",
            s => s.Hud.ItemPage, (s, v) => s.Hud.ItemPage = v),

        // plugin: the game server's own layer
        Bool("plugin.enabled", S, true, "Install Metamod:Source, CounterStrikeSharp and the plugin before every start; false runs the server vanilla.",
            s => s.Plugin.Enabled, (s, v) => s.Plugin.Enabled = v),
        Bool("plugin.testBots", P, false, "A test copy's: bots may play - the plugin's block leaves bot_quota and bot_quota_mode to RCON, so the live lab has bots to look with (one bot_add_ct gives two, one a side, and bot_quota 2: CS2's own). Off on production, where the only bots are the players' own, from FUN, Bots.",
            s => s.Plugin.TestBots, (s, v) => s.Plugin.TestBots = v),

        // restarts: the restart watcher
        Time("restarts.daily", S | Y, "07:00", "The daily restart, local time, only while nobody is on; \"off\" for none. The sync keeps clear of it.",
            s => s.Restarts.Daily, (s, v) => s.Restarts.Daily = v),
        Bool("restarts.updates", S, true, "Restart for a new CS2 build: at the end of the match, or at once with nobody on.",
            s => s.Restarts.Updates, (s, v) => s.Restarts.Updates = v),
        Int("restarts.updateCheckMinutes", S, 30, 10, 1440, "How often Valve is asked whether the build is current.",
            s => s.Restarts.UpdateCheckMinutes, (s, v) => s.Restarts.UpdateCheckMinutes = v),
        Bool("restarts.newVersions", S, true, "Restart for a deployed new version of the server's own parts the same way: at the end of the match, or at once with nobody on.",
            s => s.Restarts.NewVersions, (s, v) => s.Restarts.NewVersions = v),
        Bool("restarts.freezeRecovery", S, true, "Kill a frozen engine so Docker starts it again.",
            s => s.Restarts.FreezeRecovery, (s, v) => s.Restarts.FreezeRecovery = v),
        Int("restarts.freezeGraceSeconds", S, 10, 5, 600, "The silence before that kill.",
            s => s.Restarts.FreezeGraceSeconds, (s, v) => s.Restarts.FreezeGraceSeconds = v),
        Bool("restarts.downloadRestart", S, true, "Restart an engine process whose Workshop downloads are stuck, only while nobody is on.",
            s => s.Restarts.DownloadRestart, (s, v) => s.Restarts.DownloadRestart = v),

        // maps: the server's own Workshop maps
        Choice("maps.layout", S, "folders", ["folders", "off"], "Keep the maps in folders per type, linked from Steam's folder; off leaves Steam's layout alone.",
            s => s.Maps.Layout, (s, v) => s.Maps.Layout = v),
        Choice("maps.delete", S, "on", ["on", "dryrun", "off"], "At every engine start, delete the maps outside the keep set; dryrun lists them only.",
            s => s.Maps.Delete, (s, v) => s.Maps.Delete = v),
        Bool("maps.preview", S, false, "Change nothing and only write what a start would do - the keep set and the maps it would delete - into paths.preview, for a rehearsal on production's volume. Set maps.layout off beside it: a build that predates this key ignores it and then leaves the maps alone.",
            s => s.Maps.Preview, (s, v) => s.Maps.Preview = v),
        Bool("maps.backgroundDownloads", S, true, "Download the maps of the bridge's discovery stock (queues.json) in the background, also while people play.",
            s => s.Maps.BackgroundDownloads, (s, v) => s.Maps.BackgroundDownloads = v),
        Bool("maps.downloadLine", S, true, "Say on the HUD's top line what the server downloads for a waiting player, with its progress.",
            s => s.Maps.DownloadLine, (s, v) => s.Maps.DownloadLine = v),

        // sync: the Workshop sync
        Bool("sync.enabled", Y, true, "Runs at all; a manual run (run-now) works either way.",
            s => s.Sync.Enabled, (s, v) => s.Sync.Enabled = v),
        Bool("sync.publish", Y, true, "Write the results into the bridge's data; false runs every step and writes nothing.",
            s => s.Sync.Publish, (s, v) => s.Sync.Publish = v),
        Int("sync.runHours", Y, 3, 1, 12, "The longest run; the next starts while nobody plays and work waits.",
            s => s.Sync.RunHours, (s, v) => s.Sync.RunHours = v),
        Int("sync.apiEveryHours", Y, 4, 1, 48, "Discovery, the check and the ratings at most this often.",
            s => s.Sync.ApiEveryHours, (s, v) => s.Sync.ApiEveryHours = v),
        List("sync.tags", Y, "[\"Armsrace\", \"Deathmatch\", \"Wingman\"]", "The Workshop tags searched.",
            s => s.Sync.Tags, (s, v) => s.Sync.Tags = v, v => v.Length > 0, "a tag"),
        List("sync.excludeIds", Y, "[]", "Workshop ids that are never candidates (the HUD item, hud.addon, never is).",
            s => s.Sync.ExcludeIds, (s, v) => s.Sync.ExcludeIds = v, Digits, "a Workshop id"),
        List("sync.excludeCreators", Y, "[]", "SteamID64s of Workshop authors whose maps are listed by hand (your own).",
            s => s.Sync.ExcludeCreators, (s, v) => s.Sync.ExcludeCreators = v, SteamId64, "a SteamID64"),
        Str("sync.changedSince", Y, "", "A listed map updated after this day (YYYY-MM-DD) counts as changed at its first check; empty: none does.",
            s => s.Sync.ChangedSince, (s, v) => s.Sync.ChangedSince = v, Date, "a YYYY-MM-DD date"),
        Bool("sync.modeBackfill", Y, false, "Listed Arms Race maps that carry a mode's tag join that mode's list too, untested in it.",
            s => s.Sync.ModeBackfill, (s, v) => s.Sync.ModeBackfill = v),
        Int("sync.apiRunCap", Y, 500, 0, 10000, "Web API calls in one run, at most (never above sync.apiDayCap).",
            s => s.Sync.ApiRunCap, (s, v) => s.Sync.ApiRunCap = v, clampHigh: true),
        Int("sync.apiDayCap", Y, 10000, 0, 10000, "Web API calls in any 24 hours, at most; 10,000 is the hard limit (10% of Valve's daily allowance per key).",
            s => s.Sync.ApiDayCap, (s, v) => s.Sync.ApiDayCap = v, clampHigh: true),
        Int("sync.maxPages", Y, 200, 1, 2000, "Discovery's pages of 100, at most.",
            s => s.Sync.MaxPages, (s, v) => s.Sync.MaxPages = v),
        Num("sync.downloadPauseSeconds", Y, 2, 2, 600, "The pause between two SteamCMD downloads; at least 2 s.",
            s => s.Sync.DownloadPauseSeconds, (s, v) => s.Sync.DownloadPauseSeconds = v, clampLow: true),
        Int("sync.holdSeconds", Y, 20, 5, 300, "How long the test engine must hold a map with two bots for a pass.",
            s => s.Sync.HoldSeconds, (s, v) => s.Sync.HoldSeconds = v),
        Int("sync.batch", Y, 100, 1, 1500, "Downloads per batch; a batch is downloaded, tested and published before the next.",
            s => s.Sync.Batch, (s, v) => s.Sync.Batch = v),
        Int("sync.quietMinutes", Y, 10, 0, 240, "How long the server must have had nobody on before a run, after somebody was seen or the service started.",
            s => s.Sync.QuietMinutes, (s, v) => s.Sync.QuietMinutes = v),
        Int("sync.testPort", Y, 27215, 1024, 65535, "The test engine's port inside the sync's container.",
            s => s.Sync.TestPort, (s, v) => s.Sync.TestPort = v),
        Int("sync.engineStartsMax", Y, 200, 1, 400, "Test engine starts in one run, at most; each is an anonymous game-server logon.",
            s => s.Sync.EngineStartsMax, (s, v) => s.Sync.EngineStartsMax = v),
        Bool("sync.ratings", Y, true, "Fetch the Workshop's scores, subscriptions and trend once a day (the keep set's top lists).",
            s => s.Sync.Ratings, (s, v) => s.Sync.Ratings = v),
        Bool("sync.handover", Y, true, "Hand the keep set's maps and their tested updates over to the game server.",
            s => s.Sync.Handover, (s, v) => s.Sync.Handover = v),
        Int("sync.diskLowGb", Y, 100, 1, 100000, "Less free space than this on the game's volume is a problem for the mail.",
            s => s.Sync.DiskLowGb, (s, v) => s.Sync.DiskLowGb = v),
        Str("sync.webApiKey", Y, "file:/run/secrets/steam-webapi-key.txt", "The Steam Web API key, for discovery and the ratings; without one those steps are skipped.",
            s => s.Sync.WebApiKey, (s, v) => s.Sync.WebApiKey = v, v => v.Length == 0 || (v.Length == 32 && v.All(char.IsAsciiHexDigit)), "a 32-character hex key", secret: true),
        Str("sync.mail.smtpHost", Y, "", "The SMTP relay for the problem mails; empty: no mail.",
            s => s.Sync.Mail.SmtpHost, (s, v) => s.Sync.Mail.SmtpHost = v),
        Int("sync.mail.smtpPort", Y, 25, 1, 65535, "Its port (STARTTLS when offered).",
            s => s.Sync.Mail.SmtpPort, (s, v) => s.Sync.Mail.SmtpPort = v),
        Str("sync.mail.from", Y, "", "The mails' sender.",
            s => s.Sync.Mail.From, (s, v) => s.Sync.Mail.From = v),
        List("sync.mail.to", Y, "[]", "The mails' recipients.",
            s => s.Sync.Mail.To, (s, v) => s.Sync.Mail.To = v, v => v.Contains('@'), "an address"),
        Str("sync.mail.dashboardUrl", Y, "", "The dashboard's address every problem mail points to.",
            s => s.Sync.Mail.DashboardUrl, (s, v) => s.Sync.Mail.DashboardUrl = v),

        // steam: the Steam Web API, per program
        Num("steam.apiGapSeconds", P | S | Y, 2, 2, 600, "Each program's Steam Web API calls go one at a time, at least this long after the last one ended; never under 2 s.",
            s => s.Steam.ApiGapSeconds, (s, v) => s.Steam.ApiGapSeconds = v, clampLow: true),

        // paths: where each part finds its files, inside its container
        Str("paths.bridgeData", B | S | Y, "/data", "The bridge's data folder: read-write in the bridge and the sync, read-only in the game server - mounted at the same path in all three.",
            s => s.Paths.BridgeData, (s, v) => s.Paths.BridgeData = v, v => v.StartsWith('/'), "an absolute path"),
        Str("paths.gameDir", S | Y, "/home/steam/cs2-dedicated", "The game server's install (the image's STEAMAPPDIR); the sync mounts it read-only at the same path.",
            s => s.Paths.GameDir, (s, v) => s.Paths.GameDir = v, v => v.StartsWith('/'), "an absolute path"),
        Str("paths.handover", S, "/home/steam/cs2-dedicated/workshop-sync/handover", "The hand-over folder as the game server sees it: inside paths.gameDir, so a take-in is one rename.",
            s => s.Paths.Handover, (s, v) => s.Paths.Handover = v, v => v.StartsWith('/'), "an absolute path"),
        Str("paths.syncState", Y, "/home/steam/sync-state", "The sync's state, ledger, reports and backups (kept, backed up).",
            s => s.Paths.SyncState, (s, v) => s.Paths.SyncState = v, v => v.StartsWith('/'), "an absolute path"),
        Str("paths.syncWork", Y, "/home/steam/workshop-sync", "The sync's staging, test engine tree and hand-overs (<it>/handover is paths.handover in the game server); on the game's volume.",
            s => s.Paths.SyncWork, (s, v) => s.Paths.SyncWork = v, v => v.StartsWith('/'), "an absolute path"),
        Str("paths.syncCfg", Y, "", "Mode cfgs copied over the test engine's own; empty: none.",
            s => s.Paths.SyncCfg, (s, v) => s.Paths.SyncCfg = v, v => v.Length == 0 || v.StartsWith('/'), "an absolute path"),
        Str("paths.steamCmd", Y, "/home/steam/steamcmd/steamcmd.sh", "SteamCMD in the sync's container (the image's own).",
            s => s.Paths.SteamCmd, (s, v) => s.Paths.SteamCmd = v, v => v.StartsWith('/'), "an absolute path"),
        Str("paths.depotcache", S | Y, "/home/steam/Steam/depotcache", "Where the Steam client keeps the manifests it verified; on a volume, so a new container re-checks nothing.",
            s => s.Paths.Depotcache, (s, v) => s.Paths.Depotcache = v, v => v.StartsWith('/'), "an absolute path"),
        Str("paths.plugins", S, "/home/steam/cs2-plugins", "The CounterStrikeSharp plugins to install, one folder each (read-only): copied into the game before every engine start.",
            s => s.Paths.Plugins, (s, v) => s.Paths.Plugins = v, v => v.StartsWith('/'), "an absolute path"),
        Str("paths.preview", S, "", "Where maps.preview writes keep-preview.json and its list of what a start would delete - a rehearsal's own folder; empty: preview/ in the game's update-watch.",
            s => s.Paths.Preview, (s, v) => s.Paths.Preview = v, v => v.Length == 0 || v.StartsWith('/'), "an absolute path"),
    ];
}

public sealed class ServerSection
{
    public string Name { get; set; } = "CS2 server";
    public string Host { get; set; } = "cs2";
    public int Port { get; set; } = 27015;
    public string RconPassword { get; set; } = "";
    public string StartMap { get; set; } = "ar_shoots";
}

public sealed class BridgeSection
{
    public string Url { get; set; } = "http://bridge:8080";
    public int Port { get; set; } = 8080;
    public string Secret { get; set; } = "";
    public List<string> OfficialMaps { get; set; } = ["ar_baggage", "ar_pool_day", "ar_shoots", "ar_shoots_night"];
    public int DiscoveryStock { get; set; } = 10;
    public int DiceMaxMb { get; set; } = 300;
    public int ChangeDelaySeconds { get; set; } = 8;
    public int RestartCooldownSeconds { get; set; } = 300;
}

public sealed class HudSection
{
    public string Addon { get; set; } = "";
    public string Tab { get; set; } = "down";
    public string Check { get; set; } = "warn";
    public bool ItemPage { get; set; } = true;
}

public sealed class PluginSection
{
    public bool Enabled { get; set; } = true;
    public bool TestBots { get; set; }
}

public sealed class RestartsSection
{
    public TimeOfDay? Daily { get; set; } = new TimeOfDay(7, 0);
    public bool Updates { get; set; } = true;
    public int UpdateCheckMinutes { get; set; } = 30;
    public bool NewVersions { get; set; } = true;
    public bool FreezeRecovery { get; set; } = true;
    public int FreezeGraceSeconds { get; set; } = 10;
    public bool DownloadRestart { get; set; } = true;
}

public sealed class MapsSection
{
    public string Layout { get; set; } = "folders";
    public string Delete { get; set; } = "on";
    public bool Preview { get; set; }
    public bool BackgroundDownloads { get; set; } = true;
    public bool DownloadLine { get; set; } = true;
}

public sealed class SyncSection
{
    public bool Enabled { get; set; } = true;
    public bool Publish { get; set; } = true;
    public int RunHours { get; set; } = 3;
    public int ApiEveryHours { get; set; } = 4;
    public List<string> Tags { get; set; } = ["Armsrace", "Deathmatch", "Wingman"];
    public List<string> ExcludeIds { get; set; } = [];
    public List<string> ExcludeCreators { get; set; } = [];
    public string ChangedSince { get; set; } = "";
    public bool ModeBackfill { get; set; }
    public int ApiRunCap { get; set; } = 500;
    public int ApiDayCap { get; set; } = 10000;
    public int MaxPages { get; set; } = 200;
    public double DownloadPauseSeconds { get; set; } = 2;
    public int HoldSeconds { get; set; } = 20;
    public int Batch { get; set; } = 100;
    public int QuietMinutes { get; set; } = 10;
    public int TestPort { get; set; } = 27215;
    public int EngineStartsMax { get; set; } = 200;
    public bool Ratings { get; set; } = true;
    public bool Handover { get; set; } = true;
    public int DiskLowGb { get; set; } = 100;
    public string WebApiKey { get; set; } = "";
    public MailSection Mail { get; } = new();
}

public sealed class MailSection
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 25;
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
    public string DashboardUrl { get; set; } = "";
}

public sealed class SteamSection
{
    public double ApiGapSeconds { get; set; } = 2;
}

public sealed class PathsSection
{
    public string BridgeData { get; set; } = "/data";
    public string GameDir { get; set; } = "/home/steam/cs2-dedicated";
    public string Handover { get; set; } = "/home/steam/cs2-dedicated/workshop-sync/handover";
    public string SyncState { get; set; } = "/home/steam/sync-state";
    public string SyncWork { get; set; } = "/home/steam/workshop-sync";
    public string SyncCfg { get; set; } = "";
    public string SteamCmd { get; set; } = "/home/steam/steamcmd/steamcmd.sh";
    public string Depotcache { get; set; } = "/home/steam/Steam/depotcache";
    public string Plugins { get; set; } = "/home/steam/cs2-plugins";
    public string Preview { get; set; } = "";
}
