// --selftest, the settings' part: the bridge's values come from its settings (BridgeConfig.cs, core/Settings.cs) - the settings file, or its
// text in OVERSTRIKE2_SETTINGS_CONTENT - and the line it logs says where from, with no value in it. No settings, or settings it cannot use: no start.
using CoreSettings = OverStrike2.Core.Settings;
using Part = OverStrike2.Core.Part;

static partial class Program
{
    static void SettingsSelfTest(Action<string, bool> Check)
    {
        const string standard = "/etc/overstrike2/overstrike2.jsonc";
        static string? NoEnv(string _) => null;
        static string NoFile(string p) => throw new FileNotFoundException(p);
        Func<string, string?> EnvOf(Dictionary<string, string> vars) => n => vars.GetValueOrDefault(n);
        CoreSettings Load(Func<string, string?> env, Func<string, string> read) => CoreSettings.Load(Part.Bridge, null, env, read);

        // ---- no settings at all: no start - the variables the settings replaced stand in for nothing ----
        var none = Load(EnvOf(new() { ["RCON_PASSWORD"] = "p", ["LOG_SECRET"] = "s", ["STATE_DIR"] = "/d", ["HTTP_PORT"] = "2" }), NoFile);
        Check("Settings: no settings anywhere - the bridge stops with 78, the old variables (RCON_PASSWORD, LOG_SECRET ...) stand in for nothing, and the line names where it looked",
            BridgeConfig.StopCode(none) == 78 && none.Troubled && BridgeValues.FromFile(none) is { RconPassword: "", LogSecret: "", StateDir: "/data", HttpPort: 8080 }
            && none.StartLine == $"settings: no settings file at {standard} and no OVERSTRIKE2_SETTINGS_CONTENT - not starting: no part runs without its settings");
        var elsewhere = Load(EnvOf(new() { ["OVERSTRIKE2_SETTINGS"] = "/x/test.jsonc" }), NoFile);
        Check("Settings: OVERSTRIKE2_SETTINGS names the file read", BridgeConfig.StopCode(elsewhere) == 78 && elsewhere.StartLine.StartsWith("settings: no settings file at /x/test.jsonc - "));

        // ---- a file: every value the bridge reads comes from it; its secrets from the environment by reference ----
        const string text = """
            // the bridge's keys, none at its default
            {
              "schema": 1,
              "server": { "host": "game-host", "port": 27016, "rconPassword": "env:TEST_RCON", "startMap": "ar_pool_day" },
              "bridge": { "port": 8081, "secret": "env:TEST_SECRET", "officialMaps": ["ar_baggage", "ar_shoots"],
                          "discoveryStock": 7, "changeDelaySeconds": 3, "restartCooldownSeconds": 60 },
              "hud": { "tab": "off", "check": "enforce" },
              "paths": { "bridgeData": "/srv/bridge-data/" },
            }
            """;
        var vars = new Dictionary<string, string> { ["TEST_RCON"] = "rconvalue71", ["TEST_SECRET"] = "secretvalue72", ["RCON_HOST"] = "env-host", ["HUD_CHECK"] = "off" };
        var file = Load(EnvOf(vars), p => p == standard ? text : NoFile(p));
        var v = BridgeValues.FromFile(file);
        Check("Settings: a file that can be used - its values, the environment's variables only where it refers to them",
            BridgeConfig.StopCode(file) == null && !file.Troubled && v.RconHost == "game-host" && v.RconPort == 27016 && v.RconPassword == "rconvalue71" && v.StartMap == "ar_pool_day"
            && v.HttpPort == 8081 && v.LogSecret == "secretvalue72" && v.OfficialMaps.SequenceEqual(["ar_baggage", "ar_shoots"])
            && v.DiscoveryStock == 7 && v.ChangeDelay == 3 && v.RestartCooldown == 60
            && HudTab.Parse(v.Tab) == HudTab.Mode.Off && HudCheck.Parse(v.Check) == HudCheck.Mode.Enforce
            && v.StateDir == "/srv/bridge-data/" && v.MapsFile == "/srv/bridge-data/maps.json" && v.BrokenFile == "/srv/bridge-data/broken.json");
        Check("Settings: the line names the file and nothing it holds", file.StartLine == $"settings: the settings file {standard}"
            && new[] { "rconvalue71", "secretvalue72", "game-host", "27016", "ar_pool_day", "8081", "bridge-data" }.All(x => !file.StartLine.Contains(x)));

        // ---- a file with problems: still the file; the line names the keys, never what they hold ----
        const string wrong = """{ "schema": 1, "server": { "port": "port-text-81" }, "bridge": { "port": "port-text-82" }, "unknownkey": 1 }""";
        var image = EnvOf(new() { ["CS2_RCONPW"] = "imagepw73" });
        var bad = Load(image, _ => wrong);
        var bv = BridgeValues.FromFile(bad);
        Check("Settings: a wrong value keeps its default and the file is still used; the line names the keys only - no value, no reason",
            BridgeConfig.StopCode(bad) == null && bad.Troubled && bv.RconPort == 27015 && bv.HttpPort == 8080
            && bad.StartLine == $"settings: the settings file {standard} - 2 problems (server.port, bridge.port) - 1 warning (unknownkey)"
            && !bad.StartLine.Contains("port-text-81") && !bad.StartLine.Contains("port-text-82"));
        Check("Settings: no server.rconPassword in the file - the image's own CS2_RCONPW, its default", bv.RconPassword == "imagepw73");
        var unset = Load(image, _ => """{ "schema": 1, "bridge": { "secret": "env:TEST_UNSET" } }""");
        Check("Settings: a secret whose variable is not set is empty - a warning naming the key, so the bridge refuses to start",
            BridgeConfig.StopCode(unset) == null && BridgeValues.FromFile(unset).LogSecret == "" && unset.StartLine.EndsWith(" - 1 warning (bridge.secret)"));

        // ---- the same text in an environment variable, where a stack has no file to mount ----
        var inline = Load(EnvOf(new() { ["OVERSTRIKE2_SETTINGS_CONTENT"] = """{ "schema": 1, "server": { "host": "inline-host" } }""", ["CS2_RCONPW"] = "x" }), NoFile);
        Check("Settings: the settings as text in OVERSTRIKE2_SETTINGS_CONTENT count as the file, and the line names the variable, never the text",
            BridgeConfig.StopCode(inline) == null && BridgeValues.FromFile(inline).RconHost == "inline-host" && inline.StartLine == "settings: the environment variable OVERSTRIKE2_SETTINGS_CONTENT");

        // ---- a file that cannot be used: the bridge does not start - never a half-read file ----
        foreach (var (what, read) in new (string, Func<string, string>)[]
        {
            ("no JSON", _ => "not json at all"),
            ("no object", _ => "[1, 2]"),
            ("a schema from the future", _ => """{ "schema": 2, "server": { "host": "future-host" } }"""),
            ("unreadable", _ => throw new UnauthorizedAccessException("denied")),
        })
        {
            var s = Load(NoEnv, read);
            Check($"Settings: a file that cannot be used ({what}) - the bridge stops with 78, and the line says why",
                s.Troubled && BridgeConfig.StopCode(s) == 78 && s.StartLine.StartsWith($"settings: the settings file {standard} cannot be used (")
                && s.StartLine.EndsWith(" - not starting: no part runs on settings it cannot use") && !s.StartLine.Contains("future-host"));
        }
        var inlineBad = Load(EnvOf(new() { ["OVERSTRIKE2_SETTINGS_CONTENT"] = "{ broken" }), NoFile);
        Check("Settings: settings in OVERSTRIKE2_SETTINGS_CONTENT that cannot be used - no start either, and the line names the variable, not its text",
            BridgeConfig.StopCode(inlineBad) == 78 && inlineBad.StartLine.StartsWith("settings: the environment variable OVERSTRIKE2_SETTINGS_CONTENT cannot be used (") && !inlineBad.StartLine.Contains("{ broken"));

        // ---- a file that sets nothing: the bridge's defaults ----
        var empty = BridgeValues.FromFile(CoreSettings.Parse("""{ "schema": 1 }""", Part.Bridge, NoEnv, NoFile));
        Check("Settings: a file that sets nothing gives the bridge's defaults, its data files in paths.bridgeData",
            empty.StateDir == "/data" && empty.RconHost == "cs2" && empty.RconPort == 27015 && empty.RconPassword == "" && empty.LogSecret == ""
            && empty.MapsFile == "/data/maps.json" && empty.BrokenFile == "/data/broken.json" && empty.StartMap == "ar_shoots" && empty.HttpPort == 8080
            && empty.ChangeDelay == 8 && empty.RestartCooldown == 300 && HudTab.Parse(empty.Tab) == HudTab.Default && HudCheck.Parse(empty.Check) == HudCheck.Mode.Warn
            && empty.OfficialMaps.SequenceEqual(["ar_baggage", "ar_pool_day", "ar_shoots", "ar_shoots_night"]) && empty.DiscoveryStock == 10);

        // ---- this run's own: the bridge itself took its settings' values ----
        Check("Settings: the bridge's own values are its settings'",
            BridgeConfig.Values == BridgeValues.FromFile(BridgeConfig.Settings) with { OfficialMaps = BridgeConfig.Values.OfficialMaps });
    }
}
