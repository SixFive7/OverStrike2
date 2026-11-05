// --selftest, the settings' part: where the bridge's settings come from (SettingsSource.cs, BridgeConfig.cs) - the settings file when
// there is one that can be used, else the environment as before - and the line that says which, with no value in it.
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

        // ---- no file: the environment, as before ----
        var none = SettingsSource.Load(Part.Bridge, NoEnv, NoFile);
        Check("Settings: no settings file - the environment, and the line says so",
            !none.FromFile && !none.Troubled && none.Path == standard && none.Line == $"settings: the environment (no settings file at {standard})");
        var elsewhere = SettingsSource.Load(Part.Bridge, EnvOf(new() { ["OVERSTRIKE2_SETTINGS"] = "/x/test.jsonc" }), NoFile);
        Check("Settings: OVERSTRIKE2_SETTINGS names the file read", !elsewhere.FromFile && elsewhere.Path == "/x/test.jsonc" && elsewhere.Line.EndsWith("at /x/test.jsonc)"));

        // ---- a file: every value the bridge reads comes from it; its secrets from the environment by reference ----
        const string text = """
            // the bridge's keys, none at its default
            {
              "schema": 1,
              "server": { "host": "game-host", "port": 27016, "rconPassword": "env:TEST_RCON", "startMap": "ar_pool_day" },
              "bridge": { "port": 8081, "secret": "env:TEST_SECRET", "officialMaps": ["ar_baggage", "ar_shoots"],
                          "randomOnStart": false, "discoveryStock": 7, "changeDelaySeconds": 3, "restartCooldownSeconds": 60, "guideOnJoin": false },
              "hud": { "tab": "off", "check": "enforce" },
              "paths": { "bridgeData": "/srv/bridge-data/" },
            }
            """;
        var vars = new Dictionary<string, string> { ["TEST_RCON"] = "rconvalue71", ["TEST_SECRET"] = "secretvalue72", ["RCON_HOST"] = "env-host", ["HUD_CHECK"] = "off" };
        var file = SettingsSource.Load(Part.Bridge, EnvOf(vars), p => p == standard ? text : NoFile(p));
        var v = BridgeValues.FromFile(file.File);
        Check("Settings: a file that can be used - its values, the environment's variables only where it refers to them",
            file.FromFile && !file.Troubled && v.RconHost == "game-host" && v.RconPort == 27016 && v.RconPassword == "rconvalue71" && v.StartMap == "ar_pool_day"
            && v.HttpPort == 8081 && v.LogSecret == "secretvalue72" && v.OfficialMaps.SequenceEqual(["ar_baggage", "ar_shoots"])
            && !v.RandomOnStart && v.DiscoveryStock == 7 && v.ChangeDelay == 3 && v.RestartCooldown == 60 && !v.GuideOnJoin
            && HudTab.Parse(v.Tab) == HudTab.Mode.Off && HudCheck.Parse(v.Check) == HudCheck.Mode.Enforce
            && v.StateDir == "/srv/bridge-data/" && v.MapsFile == "/srv/bridge-data/maps.json" && v.BrokenFile == "/srv/bridge-data/broken.json");
        Check("Settings: the line names the file and nothing it holds", file.Line == $"settings: the settings file {standard}"
            && new[] { "rconvalue71", "secretvalue72", "game-host", "27016", "ar_pool_day", "8081", "bridge-data" }.All(x => !file.Line.Contains(x)));

        // ---- a file with problems: still the file; the line names the keys, never what they hold ----
        const string wrong = """{ "schema": 1, "server": { "port": "port-text-81" }, "bridge": { "port": "port-text-82" }, "unknownkey": 1 }""";
        var bad = SettingsSource.Load(Part.Bridge, NoEnv, _ => wrong);
        var bv = BridgeValues.FromFile(bad.File);
        Check("Settings: a wrong value keeps its default and the file is still used; the line names the keys only - no value, no reason",
            bad.FromFile && bad.Troubled && bv.RconPort == 27015 && bv.HttpPort == 8080
            && bad.Line == $"settings: the settings file {standard} - 2 problems (server.port, bridge.port) - 1 warning (unknownkey)"
            && !bad.Line.Contains("port-text-81") && !bad.Line.Contains("port-text-82"));
        var unset = SettingsSource.Load(Part.Bridge, NoEnv, _ => """{ "schema": 1, "bridge": { "secret": "env:TEST_UNSET" } }""");
        Check("Settings: a secret whose variable is not set is empty - a warning naming the key, so the bridge refuses to start",
            unset.FromFile && BridgeValues.FromFile(unset.File).LogSecret == "" && unset.Line.EndsWith(" - 1 warning (bridge.secret)"));

        // ---- the same text in an environment variable, where a stack has no file to mount ----
        var inline = SettingsSource.Load(Part.Bridge, EnvOf(new() { ["OVERSTRIKE2_SETTINGS_CONTENT"] = """{ "schema": 1, "server": { "host": "inline-host" } }""" }), NoFile);
        Check("Settings: the settings as text in OVERSTRIKE2_SETTINGS_CONTENT count as the file, and the line names the variable, never the text",
            inline.FromFile && BridgeValues.FromFile(inline.File).RconHost == "inline-host" && inline.Line == "settings: the environment variable OVERSTRIKE2_SETTINGS_CONTENT");

        // ---- a file that cannot be used: the bridge does not start - never the environment, never a half-read file ----
        foreach (var (what, read) in new (string, Func<string, string>)[]
        {
            ("no JSON", _ => "not json at all"),
            ("no object", _ => "[1, 2]"),
            ("a schema from the future", _ => """{ "schema": 2, "server": { "host": "future-host" } }"""),
            ("unreadable", _ => throw new UnauthorizedAccessException("denied")),
        })
        {
            var s = SettingsSource.Load(Part.Bridge, NoEnv, read);
            Check($"Settings: a file that cannot be used ({what}) - the bridge stops with 78, and the line says why",
                !s.FromFile && s.Troubled && s.Refusal != null && BridgeConfig.StopCode(s) == 78 && s.Line.StartsWith($"settings: the settings file {standard} cannot be used (")
                && s.Line.EndsWith(" - not starting: no part runs on settings it cannot use") && !s.Line.Contains("future-host"));
        }
        var inlineBad = SettingsSource.Load(Part.Bridge, EnvOf(new() { ["OVERSTRIKE2_SETTINGS_CONTENT"] = "{ broken" }), NoFile);
        Check("Settings: settings in OVERSTRIKE2_SETTINGS_CONTENT that cannot be used - no start either, and the line names the variable, not its text",
            BridgeConfig.StopCode(inlineBad) == 78 && inlineBad.Line.StartsWith("settings: the environment variable OVERSTRIKE2_SETTINGS_CONTENT cannot be used (") && !inlineBad.Line.Contains("{ broken"));
        Check("Settings: no file at all, or one that can be used, is no refusal", BridgeConfig.StopCode(none) == null && BridgeConfig.StopCode(file) == null && BridgeConfig.StopCode(bad) == null);

        // ---- the environment: as the bridge has always read it ----
        var env = BridgeValues.FromEnvironment(NoEnv);
        Check("Settings: the environment's defaults are the old ones",
            env.StateDir == "/data" && env.RconHost == "cs2" && env.RconPort == 27015 && env.RconPassword == "" && env.LogSecret == "" && env.MapsFile == "maps.json"
            && env.BrokenFile == "" && env.StartMap == "ar_shoots" && env.HttpPort == 8080 && env.ChangeDelay == 8 && env.RestartCooldown == 300
            && env.RandomOnStart && env.GuideOnJoin && HudTab.Parse(env.Tab) == HudTab.Default && HudCheck.Parse(env.Check) == HudCheck.Mode.Warn
            && env.OfficialMaps.SequenceEqual(["ar_baggage", "ar_pool_day", "ar_shoots", "ar_shoots_night"]) && env.DiscoveryStock == 10);
        var set = BridgeValues.FromEnvironment(EnvOf(new()
        {
            ["STATE_DIR"] = "/d", ["RCON_HOST"] = "h", ["RCON_PORT"] = "1", ["RCON_PASSWORD"] = "p", ["LOG_SECRET"] = "s", ["MAPS_FILE"] = "/d/m.json",
            ["BROKEN_FILE"] = "/d/b.json", ["START_MAP"] = "ar_baggage", ["HTTP_PORT"] = "2", ["CHANGE_DELAY_SECONDS"] = "3", ["RESTART_COOLDOWN_SECONDS"] = "4",
            ["RANDOM_ON_START"] = "0", ["GUIDE_ON_JOIN"] = "no", ["HUD_TAB"] = "off", ["HUD_CHECK"] = "dryrun",
            ["OFFICIAL_MAPS"] = "ar_shoots", ["DISCOVERY_STOCK"] = "99",
        }));
        Check("Settings: the environment's variables, each read as before (DISCOVERY_STOCK clamped to 40, dryrun read as warn)",
            set.StateDir == "/d" && set.RconHost == "h" && set.RconPort == 1 && set.RconPassword == "p" && set.LogSecret == "s" && set.MapsFile == "/d/m.json"
            && set.BrokenFile == "/d/b.json" && set.StartMap == "ar_baggage" && set.HttpPort == 2 && set.ChangeDelay == 3 && set.RestartCooldown == 4
            && !set.RandomOnStart && !set.GuideOnJoin && HudTab.Parse(set.Tab) == HudTab.Mode.Off && HudCheck.Parse(set.Check) == HudCheck.Mode.Warn
            && set.OfficialMaps.SequenceEqual(["ar_shoots"]) && set.DiscoveryStock == 40);

        // ---- the switch changes nothing by itself: a file that sets nothing gives the environment's defaults ----
        var empty = BridgeValues.FromFile(CoreSettings.Parse("""{ "schema": 1 }""", Part.Bridge, NoEnv, NoFile));
        Check("Settings: every default is the same in the file as in the environment - but the data files, which the file puts in paths.bridgeData",
            empty with { MapsFile = env.MapsFile, BrokenFile = env.BrokenFile, OfficialMaps = env.OfficialMaps } == env
            && empty.OfficialMaps.SequenceEqual(env.OfficialMaps)
            && empty.MapsFile == "/data/maps.json" && empty.BrokenFile == "/data/broken.json" && HudTab.Parse(empty.Tab) == HudTab.Parse(env.Tab));

        // ---- this run's own: the bridge itself took the one the source says ----
        Check("Settings: the bridge's own values come from the source its line names",
            BridgeConfig.Values == (BridgeConfig.Source.FromFile ? BridgeValues.FromFile(BridgeConfig.Source.File) : BridgeValues.FromEnvironment(Environment.GetEnvironmentVariable))
            with { OfficialMaps = BridgeConfig.Values.OfficialMaps });
    }
}
