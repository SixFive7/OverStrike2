using System.Text.Json.Nodes;
using static OverStrike2.Core.SelfTest.T;

namespace OverStrike2.Core.SelfTest;

static class SettingsTests
{
    static readonly Dictionary<string, string> Env = new()
    {
        ["CS2_RCONPW"] = "rcon-secret-value",
        ["OVERSTRIKE2_BRIDGE_SECRET"] = "abc123",
        ["TEST_HUD_ITEM"] = "3823993566",
    };

    static string? FakeEnv(string name) => Env.TryGetValue(name, out var v) ? v : null;

    const string FakeKey = "0123456789ABCDEF0123456789ABCDEF";      // made up: the same as the sync's self-test (allow.txt, .gitleaks.toml)

    static string FakeFiles(string path) => path == "/run/secrets/steam-webapi-key.txt" ? FakeKey + "\n" : throw new FileNotFoundException(path);

    static Settings Read(string text, Part part = Part.All, Func<string, string>? files = null) =>
        Settings.Parse(text, part, FakeEnv, files ?? FakeFiles);

    public static void Run(string root)
    {
        // ---- the file's place, and none at all
        var noFile = Path.Combine(root, "no-such-dir", "overstrike2.jsonc");
        var bridgeNone = Settings.Load(Part.Bridge, noFile, FakeEnv);
        Check("settings: no file, for the bridge - fatal, as for every part: nothing to run on; the rest its default, and a warning says so",
              bridgeNone is { Fatal: { } bridgeFatal, Found: false, Source: "(defaults)" } && bridgeFatal == $"no settings file at {noFile}"
              && bridgeNone.Warnings.Count == 1 && bridgeNone.Warnings[0].StartsWith("no settings file at", StringComparison.Ordinal) && bridgeNone.Server.Port == 27015
              && bridgeNone.StartLine == $"settings: no settings file at {noFile} - not starting: no part runs without its settings");
        var pluginNone = Settings.Load(Part.Plugin, noFile, FakeEnv);
        Check("settings: no file, for the plugin - fatal, and it names no hud.addon", pluginNone.Fatal is { } pluginFatal
              && pluginFatal.StartsWith($"no settings file at {noFile} - hud.addon: missing", StringComparison.Ordinal) && pluginNone.Troubled);
        foreach (var part in new[] { Part.Server, Part.Sync })
        {
            var none = Settings.Load(part, noFile, FakeEnv);
            Check($"settings: no file, for {(part == Part.Server ? "os2-server" : "os2-sync")} - fatal, it names no hud.addon; the rest its default, and a warning says so",
                  none.Fatal is { } noFileFatal && noFileFatal.StartsWith("no settings file at", StringComparison.Ordinal) && noFileFatal.Contains("hud.addon: missing", StringComparison.Ordinal)
                  && none.Warnings.Count >= 1 && none.Warnings[0].StartsWith("no settings file at", StringComparison.Ordinal) && none.Warnings.Skip(1).All(w => w.StartsWith("sync.webApiKey", StringComparison.Ordinal))
                  && none.Server.Port == 27015
                  && none.Restarts.Daily == new TimeOfDay(7, 0) && none.Sync.ApiDayCap == 10000 && none.Source == "(defaults)");
        }
        var asDir = Path.Combine(Path.GetTempPath(), "os2-settings-dir-" + Environment.ProcessId);
        Directory.CreateDirectory(asDir);
        var dir = Settings.Load(Part.Server, asDir, FakeEnv);
        Check("settings: a folder where the file should be (Docker makes one for a missing bind source) is fatal: " + dir.Fatal,
              dir.Fatal != null && dir.Fatal.Contains("could not be read", StringComparison.Ordinal));
        Directory.Delete(asDir);
        var pointed = Settings.Load(Part.Server, null, n => n == Settings.PathVariable ? Path.Combine(root, "deploy", "overstrike2.example.jsonc") : FakeEnv(n));
        Check("settings: OVERSTRIKE2_SETTINGS points a reader at another file", pointed.Fatal is null && pointed.Source.EndsWith("overstrike2.example.jsonc", StringComparison.Ordinal));

        // ---- the file's text in an environment variable, ahead of the file at the default path, behind a file named for a test
        const string inlineText = "// a comment\n{ \"schema\": 1, \"server\": { \"port\": 27016, }, \"hud\": { \"addon\": \"3823993566\" }, \"bridge\": { \"secret\": \"env:OVERSTRIKE2_BRIDGE_SECRET\" }, }";
        string? Inline(string name, string? value, string? named = null) =>
            name == Settings.ContentVariable ? value : name == Settings.PathVariable ? named : FakeEnv(name);
        string Unread(string path) => path == Settings.DefaultPath ? throw new InvalidOperationException("the file was read: " + path) : FakeFiles(path);
        foreach (var part in new[] { Part.Bridge, Part.Plugin, Part.Server, Part.Sync })
        {
            foreach (var (how, given) in new[] { ("named nothing", (string?)null), ("named the default path, as the programs do when told no other", Settings.DefaultPath) })
            {
                var v = Settings.Load(part, given, n => Inline(n, inlineText), Unread);
                Check($"settings: {Settings.ContentVariable} for the {part}, {how} - parsed by the file's rules (comments, trailing commas, references), no file looked at, the variable's name as the source, found like a file",
                      v is { Fatal: null, Problems.Count: 0, Found: true, Source: Settings.ContentSource } && v.Bridge.Secret == "abc123" && v.Warnings.Count == 0);
            }
        }
        Check("settings: the source names the variable, never its value", Settings.ContentSource == "the environment variable OVERSTRIKE2_SETTINGS_CONTENT");
        var inFile = new Dictionary<string, string> { ["/x/test.jsonc"] = """{ "schema": 1, "server": { "port": 27017 }, "hud": { "addon": "3823993566" } }""" };
        var fileSays = Settings.Load(Part.Server, "/x/test.jsonc", n => Inline(n, inlineText), p => inFile[p]);
        Check("settings: a file named for a test (--settings) wins over the variable", fileSays is { Fatal: null, Source: "/x/test.jsonc" } && fileSays.Server.Port == 27017);
        var pathSays = Settings.Load(Part.Server, null, n => Inline(n, inlineText, "/x/test.jsonc"), p => inFile[p]);
        Check("settings: ... and so does OVERSTRIKE2_SETTINGS", pathSays is { Fatal: null, Source: "/x/test.jsonc" } && pathSays.Server.Port == 27017);
        var emptyVar = Settings.Load(Part.Server, null, n => Inline(n, ""), p => p == Settings.DefaultPath ? inFile["/x/test.jsonc"] : throw new FileNotFoundException(p));
        Check("settings: an empty variable is no variable: the file at the default path", emptyVar is { Fatal: null, Found: true, Source: Settings.DefaultPath } && emptyVar.Server.Port == 27017);
        var noneAnywhere = Settings.Load(Part.Bridge, null, n => Inline(n, null), p => throw new FileNotFoundException(p));
        Check("settings: neither variable nor file: not found, the defaults - and fatal, naming both places",
              noneAnywhere is { Found: false, Source: "(defaults)" } && noneAnywhere.Fatal == $"no settings file at {Settings.DefaultPath} and no {Settings.ContentVariable}");
        foreach (var (what, text, why) in new[]
        {
            ("no JSON", "{ nope, value-of-the-variable", "not JSON"),
            ("just spaces", "   ", "not JSON"),
            ("no object", "[1, 2]", "not a JSON object"),
            ("a schema this code does not know", """{ "schema": 2, "hud": { "addon": "3823993566" } }""", "schema 2"),
            ("no hud.addon", """{ "schema": 1 }""", "hud.addon: missing"),
            ("a hud.addon of letters", """{ "schema": 1, "hud": { "addon": "value-of-the-variable" } }""", "hud.addon: not digits"),
        })
        {
            var v = Settings.Load(Part.Server, null, n => Inline(n, text), Unread);
            Check($"settings: {Settings.ContentVariable} with {what} - the fatal path, like a bad file, naming the variable and never its value ({v.Fatal})",
                  v.Fatal is { } vf && vf.StartsWith(Settings.ContentSource + ": " + why, StringComparison.Ordinal) && !vf.Contains("value-of-the-variable", StringComparison.Ordinal) && v.Found && v.Source == Settings.ContentSource);
        }

        // ---- the example file: every reader takes it as it is
        var example = File.ReadAllText(Path.Combine(root, "deploy", "overstrike2.example.jsonc"));
        foreach (var part in new[] { Part.Bridge, Part.Plugin, Part.Server, Part.Sync })
        {
            var s = Read(example, part);
            Check($"settings: deploy/overstrike2.example.jsonc for the {part}: no problem, no warning ({string.Join("; ", s.Problems.Concat(s.Warnings))})",
                  s.Fatal is null && s.Problems.Count == 0 && s.Warnings.Count == 0);
        }
        var ex = Read(example, Part.All);
        Check("settings: the example's values, its references resolved", ex.Server.RconPassword == "rcon-secret-value" && ex.Bridge.Secret == "abc123"
              && ex.Hud.Addon == "3823993566" && ex.Hud.Check == "warn" && ex.Bridge.DiscoveryStock == 10 && ex.Restarts.Daily == new TimeOfDay(7, 0) && ex.Server.Name == "My CS2 server");

        // ---- comments, trailing commas, defaults for what is left out
        var s1 = Read("{ // a comment\n \"schema\": 1, /* and another */ \"server\": { \"port\": 27016, }, \"hud\": { \"addon\": \"3823993566\", }, }");
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
        var s3 = Read("""{ "schema": 1, "sync": { "apiDayCap": 1000, "apiRunCap": 9000 } }""", Part.Sync);
        Check("settings: a run's cap above the day's is clamped to it", s3.Sync.ApiRunCap == 1000 && s3.Problems.Any(p => p.Contains("clamped to it", StringComparison.Ordinal)));
        var sk = Read("""{ "schema": 1, "hud": { "check": "dryrun", "tab": "menu" }, "bridge": { "discoveryStock": 0 }, "plugin": { "grenadeWin": true } }""");
        Check($"settings: hud.check is off, warn or enforce (dryrun is none of them); hud.tab is off or down; a stock of 0 is the stock off; grenade-to-win is gone ({string.Join("; ", sk.Problems.Concat(sk.Warnings))})",
              sk.Hud.Check == "warn" && sk.Problems.Any(p => p.StartsWith("hud.check: 'dryrun' is not one of off, warn, enforce", StringComparison.Ordinal))
              && sk.Hud.Tab == "down" && sk.Problems.Any(p => p.StartsWith("hud.tab: 'menu' is not one of off, down", StringComparison.Ordinal))
              && sk.Bridge.DiscoveryStock == 0 && sk.Warnings.Any(w => w.StartsWith("plugin.grenadeWin: no such setting", StringComparison.Ordinal)));
        Check("keep: the keep set's queues are as long as the discovery stock - 10 by default, shorter for a shorter stock, nothing for none",
              KeepRules.ForStock(10).MinQueue == 10 && KeepRules.ForStock(40).MinQueue == 10 && KeepRules.ForStock(4).MinQueue == 4 && KeepRules.ForStock(0).MinQueue == 0
              && KeepRules.ForStock(4).Top == KeepRules.Default.Top);
        var s4 = Read("""{ "schema": 1, "restarts": { "daily": "off" }, "hud": { "tab": "DOWN", "check": "sideways" }, "sync": { "excludeCreators": ["76561197960265728", "12"], "excludeIds": ["3823993566", "abc"], "mail": { "to": ["nobody"] }, "changedSince": "2026-13-01" } }""");
        Check("settings: off switches the daily restart off; a choice ignores case", s4.Restarts.Daily is null && s4.Hud.Tab == "down");
        Check("settings: a choice outside its list, a list entry of the wrong kind, a date that is none: each keeps its default (" + s4.Problems.Count + ")",
              s4.Hud.Check == "warn" && s4.Sync.ExcludeCreators.Count == 0 && s4.Sync.ExcludeIds.Count == 0 && s4.Sync.Mail.To.Count == 0 && s4.Sync.ChangedSince == ""
              && s4.Problems.Count == 5);

        // ---- each reader checks its own keys only
        var bad = """{ "schema": 1, "bridge": { "port": "x" }, "sync": { "batch": "x" } }""";
        Check("settings: os2-server leaves the bridge's port and the sync's batch alone, the bridge and os2-sync each see their own wrong value",
              Read(bad, Part.Server).Problems.Count == 0 && Read(bad, Part.Plugin).Problems.Count == 0
              && Read(bad, Part.Bridge).Problems is [{ } bridgeSays] && bridgeSays.StartsWith("bridge.port", StringComparison.Ordinal)
              && Read(bad, Part.Sync).Problems is [{ } syncSays] && syncSays.StartsWith("sync.batch", StringComparison.Ordinal));
        var mixed = Read("""{ "schema": 1, "hud": { "addon": "3823993566" }, "paths": { "syncWork": "relative" }, "maps": { "delete": "maybe" } }""", Part.Server);
        Check($"settings: ...so a wrong sync path is os2-sync's problem alone, and os2-server's doctor lists its own keys ({string.Join("; ", mixed.Problems)})",
              mixed.Problems is [{ } own] && own.StartsWith("maps.delete", StringComparison.Ordinal) && !mixed.Describe().Any(d => d.StartsWith("sync.", StringComparison.Ordinal) || d.StartsWith("paths.sync", StringComparison.Ordinal))
              && Read("""{ "schema": 1, "paths": { "syncWork": "relative" } }""", Part.Sync).Problems is [{ } syncOwn] && syncOwn.StartsWith("paths.syncWork", StringComparison.Ordinal));

        // ---- unknown keys warn, never stop
        var s5 = Read("""{ "schema": 1, "$schema": "./settings.schema.json", "server": { "nmae": "x" }, "future": { "a": 1 }, "restarts": 5, "hud": { "addon": "3823993566" } }""");
        Check("settings: a misspelt key, a section nobody knows and a section that is no object are warnings, never fatal ("
              + string.Join("; ", s5.Warnings) + ")", s5.Fatal is null && s5.Warnings.Count == 3 && s5.Warnings.Any(w => w.StartsWith("server.nmae: no such setting", StringComparison.Ordinal))
              && s5.Warnings.Any(w => w.StartsWith("future: no such setting", StringComparison.Ordinal)) && s5.Warnings.Any(w => w.StartsWith("restarts: is a number, not a section", StringComparison.Ordinal)));

        // ---- references
        var key = Temp("webapi.txt");
        File.WriteAllText(key, "  0123456789ABCDEF0123456789ABCDEF\n");
        var s6 = Read($$"""{ "schema": 1, "server": { "rconPassword": "env:CS2_RCONPW" }, "sync": { "webApiKey": "file:{{key.Replace("\\", "/")}}" }, "bridge": { "secret": "env:NOT_SET" } }""",
                      Part.All, File.ReadAllText);
        Check("settings: env: and file: references - read, the file trimmed; an unset variable is empty and a warning",
              s6.Server.RconPassword == "rcon-secret-value" && s6.Sync.WebApiKey == "0123456789ABCDEF0123456789ABCDEF" && s6.Bridge.Secret == ""
              && s6.Warnings.Any(w => w.Contains("NOT_SET is not set", StringComparison.Ordinal)));
        var s7 = Read("""{ "schema": 1, "sync": { "webApiKey": "file:/no/such/file" } }""", Part.Sync);
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
        Check("settings: no schema at all is a problem, the rest is read", Read("""{ "server": { "port": 27016 }, "hud": { "addon": "3823993566" } }""") is { Fatal: null } g
              && g.Server.Port == 27016 && g.Problems.Count == 1);

        // ---- hud.addon is required: missing, empty or not digits, the file cannot be used by the parts that read it
        foreach (var (what, text, why) in new[]
        {
            ("no hud section", """{ "schema": 1 }""", "missing"),
            ("a hud section without it", """{ "schema": 1, "hud": { "tab": "off" } }""", "missing"),
            ("null", """{ "schema": 1, "hud": { "addon": null } }""", "missing"),
            ("a hud that is no section", """{ "schema": 1, "hud": 5 }""", "missing"),
            ("empty", """{ "schema": 1, "hud": { "addon": "" } }""", "empty"),
            ("a variable that is not set", """{ "schema": 1, "hud": { "addon": "env:NOT_SET" } }""", "empty"),
            ("a file that is not there", """{ "schema": 1, "hud": { "addon": "file:/no/such/file" } }""", "empty"),
            ("letters", """{ "schema": 1, "hud": { "addon": "value-from-the-file" } }""", "not digits"),
            ("digits and a space", """{ "schema": 1, "hud": { "addon": " 3823993566" } }""", "not digits"),
            ("a minus sign", """{ "schema": 1, "hud": { "addon": "-3823993566" } }""", "not digits"),
            ("a number, not a string", """{ "schema": 1, "hud": { "addon": 3823993566 } }""", "not a string"),
        })
        {
            foreach (var part in new[] { Part.Plugin, Part.Server, Part.Sync })
            {
                var s = Read(text, part);
                Check($"hud.addon: {what}, for the {part} - the file cannot be used, the words name the key and what it must hold, never the value ({s.Fatal})",
                      s.Fatal is { } fatal && fatal.StartsWith("hud.addon: " + why, StringComparison.Ordinal)
                      && fatal.Contains("required: the Workshop id of the HUD addon, digits only", StringComparison.Ordinal) && !fatal.Contains("value-from-the-file", StringComparison.Ordinal)
                      && s.Hud.Addon == "" && !s.Problems.Any(p => p.StartsWith("hud.addon", StringComparison.Ordinal)));
            }
        }
        const string withAddon = """{ "schema": 1, "hud": { "addon": "3823993566" } }""";
        Check("hud.addon: a Workshop id, for every reader - the file can be used",
              new[] { Part.Bridge, Part.Plugin, Part.Server, Part.Sync, Part.All }.All(p => Read(withAddon, p) is { Fatal: null, Problems.Count: 0 }) && Read(withAddon, Part.Server).Hud.Addon == "3823993566");
        Check("hud.addon: by reference too, as any string value: env:, and file: trimmed", Read("""{ "schema": 1, "hud": { "addon": "env:TEST_HUD_ITEM" } }""", Part.Server) is { Fatal: null, Hud.Addon: "3823993566" }
              && Read("""{ "schema": 1, "hud": { "addon": "file:/x/hud.txt" } }""", Part.Server, p => p == "/x/hud.txt" ? " 3823993566\n" : throw new FileNotFoundException(p)) is { Fatal: null, Hud.Addon: "3823993566" });
        Check("hud.addon: the bridge does not read it - a file without it, or with a wrong one, is the bridge's as ever, not even a problem",
              Read("""{ "schema": 1 }""", Part.Bridge) is { Fatal: null, Problems.Count: 0 } && Read("""{ "schema": 1, "hud": { "addon": "nope", "tab": "off" } }""", Part.Bridge) is { Fatal: null, Problems.Count: 0, Hud.Tab: "off" });
        Check("hud.addon: the rest of the file is still read, for doctor to show", Read("""{ "schema": 1, "server": { "port": 27016 }, "bridge": { "port": "x" } }""", Part.All) is { Fatal: not null, Server.Port: 27016, Problems.Count: 1 });
        Check("hud.addon: its default stays empty, no built-in id, and it is the one required key",
              new Settings().Hud.Addon == "" && Settings.Keys.Where(k => k.Required != null).Select(k => k.Path).SequenceEqual(["hud.addon"]) && Settings.Keys.Single(k => k.Required != null).Default == "");
        Check("hud.check: one player without the HUD still gets a warning only - warn by default", new Settings().Hud.Check == "warn" && Settings.Keys.Single(k => k.Path == "hud.check").Default == "warn");
        Check("settings: plugin.enabled false (the emergency switch) is a file like any other: it can be used, with hud.addon in it",
              Read("""{ "schema": 1, "hud": { "addon": "3823993566" }, "plugin": { "enabled": false } }""", Part.Server) is { Fatal: null, Plugin.Enabled: false }
              && Read("""{ "schema": 1, "plugin": { "enabled": false } }""", Part.Server).Fatal != null);
        Check("hud.addon: the example file says it is required, and offers no way to run without it", !example.Contains("no HUD", StringComparison.OrdinalIgnoreCase) && example.Contains("required", StringComparison.Ordinal));

        // ---- the times
        Check("settings: times - 07:00, 7:05 and off; not 24:00, 7:5, 0700 or none",
              TimeOfDay.TryParse("07:00", out var a) && a == new TimeOfDay(7, 0) && TimeOfDay.TryParse("7:05", out var b) && b == new TimeOfDay(7, 5)
              && TimeOfDay.TryParse("Off", out var c) && c is null && !TimeOfDay.TryParse("24:00", out _) && !TimeOfDay.TryParse("7:5", out _)
              && !TimeOfDay.TryParse("0700", out _) && !TimeOfDay.TryParse("none", out _));

        // ---- the line the bridge and the plugin log at their start: where from, the keys by name, never a value
        var lineFile = Settings.Load(Part.Bridge, "/x/line.jsonc", FakeEnv, _ => """{ "schema": 1, "server": { "port": "27x", "host": "secret-host" }, "bridge": { "nope": 1 } }""");
        Check($"settings: the start line names the file and the keys with a problem or a warning, never what they hold ({lineFile.StartLine})",
              lineFile.StartLine == "settings: the settings file /x/line.jsonc - 1 problem (server.port) - 1 warning (bridge.nope)" && lineFile.Troubled);
        var lineVar = Settings.Load(Part.Bridge, null, n => Inline(n, inlineText), Unread);
        Check("settings: ...the variable by its name", lineVar.StartLine == "settings: the environment variable OVERSTRIKE2_SETTINGS_CONTENT" && !lineVar.Troubled);
        var lineBad = Settings.Load(Part.Plugin, "/x/line.jsonc", FakeEnv, _ => """{ "schema": 1, "hud": { "addon": "letters-in-the-file" } }""");
        Check($"settings: ...settings that cannot be used: why, and that the part does not start ({lineBad.StartLine})",
              lineBad.StartLine.StartsWith("settings: the settings file /x/line.jsonc cannot be used (hud.addon: not digits", StringComparison.Ordinal)
              && lineBad.StartLine.EndsWith(" - not starting: no part runs on settings it cannot use", StringComparison.Ordinal) && !lineBad.StartLine.Contains("letters-in-the-file", StringComparison.Ordinal));

        // ---- the docs and the schema are made from the same table
        var schemaFile = Path.Combine(root, "docs", "settings.schema.json");
        Check("settings: docs/settings.schema.json is the one the table makes (dotnet run --project core/SelfTest -- --write)",
              File.Exists(schemaFile) && File.ReadAllText(schemaFile).ReplaceLineEndings("\n") == SettingsDocs.JsonSchema());
        var mdFile = Path.Combine(root, "docs", "settings.md");
        Check("settings: docs/settings.md's key tables are the ones the table makes",
              File.Exists(mdFile) && KeysBlock(File.ReadAllText(mdFile).ReplaceLineEndings("\n")) == SettingsDocs.KeysMarkdown().Trim('\n'));
        var schema = JsonNode.Parse(SettingsDocs.JsonSchema())!;
        Check("settings: the schema holds every key of the table, and its default (a required key has none)", Settings.Keys.All(k =>
        {
            JsonNode? n = schema["properties"];
            foreach (var p in k.Path.Split('.')[..^1])
                n = n?[p]?["properties"];
            var node = n?[k.Path.Split('.')[^1]];
            return node != null && (node["default"] != null) == (k.Required is null);
        }));
        var hudSchema = schema["properties"]?["hud"];
        var addonPattern = new System.Text.RegularExpressions.Regex(hudSchema?["properties"]?["addon"]?["pattern"]?.GetValue<string>() ?? "^$");
        Check("settings: the schema requires hud.addon, and its form: digits, or a reference; not empty, not letters",
              schema["required"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["schema", "hud"]) && hudSchema?["required"]?.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["addon"]) == true
              && addonPattern.IsMatch("3823993566") && addonPattern.IsMatch("env:NAME") && addonPattern.IsMatch("file:/run/hud.txt") && !addonPattern.IsMatch("") && !addonPattern.IsMatch("abc") && !addonPattern.IsMatch("12 3"));
        Check("settings: every key's default reads back as itself; a required key's is fatal instead", Settings.Keys.All(k =>
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
            return s.Problems.Count == 0 && (k.Secret || k.Show(s) == k.Show(new Settings()) || k.Default.StartsWith("file:", StringComparison.Ordinal))
                   && (k.Required is null || s.Fatal?.StartsWith(k.Path + ": empty", StringComparison.Ordinal) == true);
        }));
        var gameSide = Settings.Keys.Where(k => (k.Readers & (Part.Bridge | Part.Plugin)) != 0).Select(k => k.Path).ToList();
        Check($"settings: the bridge and the plugin read {gameSide.Count} keys, each in the docs", gameSide.Count > 0
              && gameSide.All(p => File.ReadAllText(mdFile).Contains("`" + p + "`", StringComparison.Ordinal)));
    }
}
