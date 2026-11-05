using System.Text.Json.Nodes;
using static OverStrike2.Core.SelfTest.T;

namespace OverStrike2.Core.SelfTest;

static class SettingsTests
{
    static readonly Dictionary<string, string> Env = new()
    {
        ["CS2_RCONPW"] = "rcon-secret-value",
        ["OVERSTRIKE2_BRIDGE_SECRET"] = "abc123",
    };

    static string? FakeEnv(string name) => Env.TryGetValue(name, out var v) ? v : null;

    const string FakeKey = "0123456789ABCDEF0123456789ABCDEF";      // made up: the same as the sync's self-test (allow.txt, .gitleaks.toml)

    static string FakeFiles(string path) => path == "/run/secrets/steam-webapi-key.txt" ? FakeKey + "\n" : throw new FileNotFoundException(path);

    static Settings Read(string text, Part part = Part.All, Func<string, string>? files = null) =>
        Settings.Parse(text, part, FakeEnv, files ?? FakeFiles);

    public static void Run(string root)
    {
        // ---- the file's place, and none at all
        var none = Settings.Load(Part.Server, Path.Combine(root, "no-such-dir", "overstrike2.jsonc"), FakeEnv);
        Check("settings: no file - every value its default, and a warning says so", none.Fatal is null && none.Warnings.Count >= 1
              && none.Warnings[0].StartsWith("no settings file at", StringComparison.Ordinal) && none.Warnings.Skip(1).All(w => w.StartsWith("sync.webApiKey", StringComparison.Ordinal))
              && none.Server.Port == 27015
              && none.Restarts.Daily == new TimeOfDay(7, 0) && none.Sync.ApiDayCap == 10000 && none.Source == "(defaults)");
        var asDir = Path.Combine(Path.GetTempPath(), "os2-settings-dir-" + Environment.ProcessId);
        Directory.CreateDirectory(asDir);
        var dir = Settings.Load(Part.Server, asDir, FakeEnv);
        Check("settings: a folder where the file should be (Docker makes one for a missing bind source) is fatal: " + dir.Fatal,
              dir.Fatal != null && dir.Fatal.Contains("could not be read", StringComparison.Ordinal));
        Directory.Delete(asDir);
        var pointed = Settings.Load(Part.Server, null, n => n == Settings.PathVariable ? Path.Combine(root, "deploy", "overstrike2.example.jsonc") : FakeEnv(n));
        Check("settings: OVERSTRIKE2_SETTINGS points a reader at another file", pointed.Fatal is null && pointed.Source.EndsWith("overstrike2.example.jsonc", StringComparison.Ordinal));

        // ---- the example file: every reader takes it as it is
        var example = File.ReadAllText(Path.Combine(root, "deploy", "overstrike2.example.jsonc"));
        foreach (var part in new[] { Part.Bridge, Part.Plugin, Part.Server })
        {
            var s = Read(example, part);
            Check($"settings: deploy/overstrike2.example.jsonc for the {part}: no problem, no warning ({string.Join("; ", s.Problems.Concat(s.Warnings))})",
                  s.Fatal is null && s.Problems.Count == 0 && s.Warnings.Count == 0);
        }
        var ex = Read(example, Part.All);
        Check("settings: the example's values, its references resolved", ex.Server.RconPassword == "rcon-secret-value" && ex.Bridge.Secret == "abc123"
              && ex.Hud.Addon == "3823993566" && ex.Hud.Check == "warn" && ex.Bridge.DiscoveryStock == 10 && ex.Restarts.Daily == new TimeOfDay(7, 0) && ex.Server.Name == "My CS2 server");

        // ---- comments, trailing commas, defaults for what is left out
        var s1 = Read("{ // a comment\n \"schema\": 1, /* and another */ \"server\": { \"port\": 27016, }, }");
        Check("settings: JSON with comments and trailing commas; a key left out has its default",
              s1.Fatal is null && s1.Server.Port == 27016 && s1.Server.Host == "cs2" && s1.Problems.Count == 0);

        // ---- wrong values keep their defaults; hard limits clamp
        var s2 = Read("""{ "schema": 1, "server": { "port": "x" }, "restarts": { "freezeGraceSeconds": 1, "daily": "25:00" }, "sync": { "apiDayCap": 20000, "runHours": 2.5 }, "steam": { "apiGapSeconds": 0.5 } }""");
        Check("settings: a string for a number keeps the default (" + string.Join("; ", s2.Problems) + ")",
              s2.Server.Port == 27015 && s2.Problems.Any(p => p.StartsWith("server.port: not a number", StringComparison.Ordinal)));
        Check("settings: out of range keeps the default", s2.Restarts.FreezeGraceSeconds == 10 && s2.Problems.Any(p => p.StartsWith("restarts.freezeGraceSeconds: 1 is outside 5..600", StringComparison.Ordinal)));
        Check("settings: a time that is no time keeps the default", s2.Restarts.Daily == new TimeOfDay(7, 0) && s2.Problems.Any(p => p.StartsWith("restarts.daily: '25:00' is not HH:MM or off", StringComparison.Ordinal)));
        Check("settings: a hard limit clamps - 20,000 Web API calls a day becomes 10,000", s2.Sync.ApiDayCap == 10000
              && s2.Problems.Any(p => p.StartsWith("sync.apiDayCap: 20000 is above the hard limit 10000", StringComparison.Ordinal)));
        Check("settings: a gap under 2 s between Web API calls becomes 2 s", s2.Steam.ApiGapSeconds == 2 && s2.Problems.Any(p => p.StartsWith("steam.apiGapSeconds", StringComparison.Ordinal)));
        Check("settings: a whole number wanted, a fraction given: the default", s2.Sync.RunHours == 3 && s2.Problems.Any(p => p.StartsWith("sync.runHours: 2.5 is not a whole number", StringComparison.Ordinal)));
        var s3 = Read("""{ "schema": 1, "sync": { "apiDayCap": 1000, "apiRunCap": 9000 } }""", Part.Server);
        Check("settings: a run's cap above the day's is clamped to it", s3.Sync.ApiRunCap == 1000 && s3.Problems.Any(p => p.Contains("clamped to it", StringComparison.Ordinal)));
        var sk = Read("""{ "schema": 1, "hud": { "check": "dryrun", "tab": "menu" }, "bridge": { "discoveryStock": 0 }, "plugin": { "grenadeWin": true } }""");
        Check($"settings: hud.check's old name dryrun means warn; hud.tab is off or down; a stock of 0 is the stock off; grenade-to-win is gone ({string.Join("; ", sk.Problems.Concat(sk.Warnings))})",
              sk.Hud.Check == "warn" && sk.Hud.Tab == "down" && sk.Problems.Any(p => p.StartsWith("hud.tab: 'menu' is not one of off, down", StringComparison.Ordinal))
              && sk.Bridge.DiscoveryStock == 0 && sk.Warnings.Any(w => w.StartsWith("plugin.grenadeWin: no such setting", StringComparison.Ordinal)));
        Check("keep: the keep set's queues are as long as the discovery stock - 10 by default, shorter for a shorter stock, nothing for none",
              KeepRules.ForStock(10).MinQueue == 10 && KeepRules.ForStock(40).MinQueue == 10 && KeepRules.ForStock(4).MinQueue == 4 && KeepRules.ForStock(0).MinQueue == 0
              && KeepRules.ForStock(4).Top == KeepRules.Default.Top);
        var s4 = Read("""{ "schema": 1, "restarts": { "daily": "off" }, "hud": { "tab": "DOWN", "check": "sideways" }, "bridge": { "admins": ["76561197960265728", "12"] }, "sync": { "excludeIds": ["3823993566", "abc"], "mail": { "to": ["nobody"] }, "changedSince": "2026-13-01" } }""");
        Check("settings: off switches the daily restart off; a choice ignores case", s4.Restarts.Daily is null && s4.Hud.Tab == "down");
        Check("settings: a choice outside its list, a list entry of the wrong kind, a date that is none: each keeps its default (" + s4.Problems.Count + ")",
              s4.Hud.Check == "warn" && s4.Bridge.Admins.Count == 0 && s4.Sync.ExcludeIds.Count == 0 && s4.Sync.Mail.To.Count == 0 && s4.Sync.ChangedSince == ""
              && s4.Problems.Count == 5);

        // ---- each reader checks its own keys only
        var bad = """{ "schema": 1, "bridge": { "port": "x" }, "sync": { "batch": "x" } }""";
        Check("settings: the server program leaves the bridge's port alone, the bridge its own wrong port is a problem",
              Read(bad, Part.Server).Problems.Count == 1 && Read(bad, Part.Bridge).Problems.Count == 1 && Read(bad, Part.Plugin).Problems.Count == 0
              && Read(bad, Part.Bridge).Problems[0].StartsWith("bridge.port", StringComparison.Ordinal));

        // ---- unknown keys warn, never stop
        var s5 = Read("""{ "schema": 1, "$schema": "./settings.schema.json", "server": { "nmae": "x" }, "future": { "a": 1 }, "hud": 5 }""");
        Check("settings: a misspelt key, a section nobody knows and a section that is no object are warnings, never fatal ("
              + string.Join("; ", s5.Warnings) + ")", s5.Fatal is null && s5.Warnings.Count == 3 && s5.Warnings.Any(w => w.StartsWith("server.nmae: no such setting", StringComparison.Ordinal))
              && s5.Warnings.Any(w => w.StartsWith("future: no such setting", StringComparison.Ordinal)) && s5.Warnings.Any(w => w.StartsWith("hud: is a number, not a section", StringComparison.Ordinal)));

        // ---- references
        var key = Temp("webapi.txt");
        File.WriteAllText(key, "  0123456789ABCDEF0123456789ABCDEF\n");
        var s6 = Read($$"""{ "schema": 1, "server": { "rconPassword": "env:CS2_RCONPW" }, "sync": { "webApiKey": "file:{{key.Replace("\\", "/")}}" }, "bridge": { "secret": "env:NOT_SET" } }""",
                      Part.All, File.ReadAllText);
        Check("settings: env: and file: references - read, the file trimmed; an unset variable is empty and a warning",
              s6.Server.RconPassword == "rcon-secret-value" && s6.Sync.WebApiKey == "0123456789ABCDEF0123456789ABCDEF" && s6.Bridge.Secret == ""
              && s6.Warnings.Any(w => w.Contains("NOT_SET is not set", StringComparison.Ordinal)));
        var s7 = Read("""{ "schema": 1, "sync": { "webApiKey": "file:/no/such/file" } }""", Part.Server);
        Check("settings: a file reference that finds nothing is empty and a warning - the sync then skips the steps that need a key",
              s7.Sync.WebApiKey == "" && s7.Warnings.Any(w => w.StartsWith("sync.webApiKey: /no/such/file could not be read", StringComparison.Ordinal)));
        var s8 = Read("""{ "schema": 1, "sync": { "webApiKey": "not-a-key" }, "bridge": { "secret": "has spaces" } }""");
        Check("settings: a secret of the wrong shape is a problem that never shows the secret", s8.Problems.Count == 2
              && s8.Problems.All(p => !p.Contains("not-a-key", StringComparison.Ordinal) && !p.Contains("has spaces", StringComparison.Ordinal)));
        var described = string.Join("\n", Read(example, Part.Bridge).Describe());
        Check("settings: Describe shows a secret as set, never its value", described.Contains("server.rconPassword = (set)", StringComparison.Ordinal)
              && !described.Contains("rcon-secret-value", StringComparison.Ordinal) && described.Contains("bridge.port = 8080", StringComparison.Ordinal)
              && !described.Contains("sync.", StringComparison.Ordinal));

        // ---- a file that is not used at all
        Check("settings: not JSON is fatal", Read("{ nope").Fatal?.StartsWith("not JSON", StringComparison.Ordinal) == true);
        Check("settings: a list instead of an object is fatal", Read("[1, 2]").Fatal == "not a JSON object");
        Check("settings: a schema this code does not know is fatal, and nothing of it is read",
              Read("""{ "schema": 2, "server": { "port": 1 } }""") is { Fatal: not null } f && f.Server.Port == 27015);
        Check("settings: no schema at all is a problem, the rest is read", Read("""{ "server": { "port": 27016 } }""") is { Fatal: null } g
              && g.Server.Port == 27016 && g.Problems.Count == 1);

        // ---- the times
        Check("settings: times - 07:00, 7:05 and off; not 24:00, 7:5 or 0700",
              TimeOfDay.TryParse("07:00", out var a) && a == new TimeOfDay(7, 0) && TimeOfDay.TryParse("7:05", out var b) && b == new TimeOfDay(7, 5)
              && TimeOfDay.TryParse("Off", out var c) && c is null && !TimeOfDay.TryParse("24:00", out _) && !TimeOfDay.TryParse("7:5", out _)
              && !TimeOfDay.TryParse("0700", out _));

        // ---- the docs and the schema are made from the same table
        var schemaFile = Path.Combine(root, "docs", "settings.schema.json");
        Check("settings: docs/settings.schema.json is the one the table makes (dotnet run --project core/SelfTest -- --write)",
              File.Exists(schemaFile) && File.ReadAllText(schemaFile).ReplaceLineEndings("\n") == Settings.JsonSchema());
        var mdFile = Path.Combine(root, "docs", "settings.md");
        Check("settings: docs/settings.md's key tables are the ones the table makes",
              File.Exists(mdFile) && KeysBlock(File.ReadAllText(mdFile).ReplaceLineEndings("\n")) == Settings.KeysMarkdown().Trim('\n'));
        var schema = JsonNode.Parse(Settings.JsonSchema())!;
        Check("settings: the schema holds every key of the table, and its default", Settings.Keys.All(k =>
        {
            JsonNode? n = schema["properties"];
            foreach (var p in k.Path.Split('.')[..^1])
                n = n?[p]?["properties"];
            return n?[k.Path.Split('.')[^1]]?["default"] != null;
        }));
        Check("settings: every key's default reads back as itself", Settings.Keys.All(k =>
        {
            var json = k.Type switch
            {
                "string" or "time" => "\"" + k.Default + "\"",
                _ => k.Default,
            };
            var parts = k.Path.Split('.');
            var text = "{ \"schema\": 1, " + string.Concat(parts[..^1].Select(p => "\"" + p + "\": { ")) + "\"" + parts[^1] + "\": " + json
                       + new string('}', parts.Length - 1) + " }";
            var s = Settings.Parse(text, Part.All, n => n == "CS2_RCONPW" ? "" : null, _ => "");
            return s.Problems.Count == 0 && (k.Secret || k.Show(s) == k.Show(new Settings()) || k.Default.StartsWith("file:", StringComparison.Ordinal));
        }));
        var gameSide = Settings.Keys.Where(k => (k.Readers & (Part.Bridge | Part.Plugin)) != 0).Select(k => k.Path).ToList();
        Check($"settings: the bridge and the plugin read {gameSide.Count} keys, each in the docs", gameSide.Count > 0
              && gameSide.All(p => File.ReadAllText(mdFile).Contains("`" + p + "`", StringComparison.Ordinal)));
    }
}
