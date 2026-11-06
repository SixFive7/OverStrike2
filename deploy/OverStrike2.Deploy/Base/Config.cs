using System.Globalization;

namespace OverStrike2.Deploy;

/// <summary>The private config, as every command reads it: a value by its dotted name, "env:NAME" read from that environment variable.</summary>
interface IConfig
{
    /// <summary>The file it came from; null for a config made up in memory.</summary>
    string? Path { get; }

    /// <summary>The value at a dotted name; dflt when it is missing. required: a missing or empty value ends the command with exit 2.</summary>
    object? Get(string dotted, object? dflt = null, bool required = false);
}

static class ConfigExtensions
{
    /// <summary>A value as text: str() of what is there, dflt when it is missing.</summary>
    public static string Str(this IConfig cfg, string dotted, string dflt = "", bool required = false) =>
        cfg.Get(dotted, dflt, required) switch
        {
            null => "",
            string s => s,
            var x => Py.Str(x),
        };

    /// <summary>Python's truth of a value (bool, a number, a text, a list or a table).</summary>
    public static bool Truthy(object? x) => x switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        long l => l != 0,
        double d => d != 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    /// <summary>A list of values (cfg.get(x, []) or []).</summary>
    public static List<object?> List(this IConfig cfg, string dotted) => cfg.Get(dotted, null) as List<object?> ?? [];

    /// <summary>A table of values (cfg.get(x, {}) or {}).</summary>
    public static Dictionary<string, object?> Table(this IConfig cfg, string dotted) =>
        cfg.Get(dotted, null) as Dictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>A boolean value, dflt when it is missing.</summary>
    public static bool Bool(this IConfig cfg, string dotted, bool dflt) => Truthy(cfg.Get(dotted, dflt));

    /// <summary>"env:NAME" read from the environment, anything else as its text.</summary>
    public static string Resolve(object? value) =>
        value is string s && s.StartsWith("env:", StringComparison.Ordinal) ? Environment.GetEnvironmentVariable(s[4..]) ?? "" : Py.Str(value);

    public static int Int(object? x, int dflt) => x switch
    {
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => dflt,
    };
}

/// <summary>The private config file (TOML): $OVERSTRIKE2_CONFIG, else .private/config.toml in this checkout (gitignored).
/// deploy/config.example.toml shows every key. Nothing printed here contains a credential.</summary>
sealed class Config : IConfig
{
    public string? Path { get; }
    public Dictionary<string, object?> Data { get; }

    public Config(string? path = null)
    {
        Path = path ?? NonEmpty(Environment.GetEnvironmentVariable("OVERSTRIKE2_CONFIG")) ?? System.IO.Path.Combine(Repo.Root, ".private", "config.toml");
        if (!File.Exists(Path))
            throw Out.Fail($"no private config at {Path}: copy deploy/config.example.toml there (or set OVERSTRIKE2_CONFIG) and fill it in", 2);
        try
        {
            Data = Toml.Parse(File.ReadAllText(Path));
        }
        catch (TomlError e)
        {
            throw Out.Fail($"{Path} is not valid TOML: {e.Message}", 2);
        }
    }

    static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Whether there is a private config to read where Config would look.</summary>
    public static bool Exists(string? path = null) =>
        File.Exists(path ?? NonEmpty(Environment.GetEnvironmentVariable("OVERSTRIKE2_CONFIG")) ?? System.IO.Path.Combine(Repo.Root, ".private", "config.toml"));

    public object? Get(string dotted, object? dflt = null, bool required = false)
    {
        object? node = Data;
        foreach (var part in dotted.Split('.'))
        {
            if (node is not Dictionary<string, object?> d || !d.TryGetValue(part, out node))
            {
                if (required)
                    throw Out.Fail($"{Path} has no '{dotted}'", 2);
                return dflt;
            }
        }
        if (node is string s && s.StartsWith("env:", StringComparison.Ordinal))
        {
            var value = Environment.GetEnvironmentVariable(s[4..]) ?? "";
            if (required && value.Length == 0)
                throw Out.Fail($"'{dotted}' reads the environment variable {s[4..]}, which is not set", 2);
            return value;
        }
        if (required && (node is null || node is string { Length: 0 }))
            throw Out.Fail($"'{dotted}' is empty in {Path}", 2);
        return node;
    }
}

/// <summary>A config of values in memory, by dotted name - the self-tests' deployment. A required value that is missing or empty ends the
/// command with exit 2, as the file's does.</summary>
sealed class MemoryConfig(Dictionary<string, object?> values, string? path = "the-private-config.toml") : IConfig
{
    public string? Path { get; } = path;
    public Dictionary<string, object?> Values { get; } = values;

    public object? Get(string dotted, object? dflt = null, bool required = false)
    {
        if (Values.TryGetValue(dotted, out var v) && v is not null && v is not string { Length: 0 })
            return v;
        if (required)
            throw new Exit(2);
        return Values.TryGetValue(dotted, out var w) ? w : dflt;
    }
}
