using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace OverStrike2.Deploy;

/// <summary>The release rehearsal's compose file, offline (rehearse.py had no tests): a made-up stack shaped like a real one - its anchors in an
/// x- block, the settings as one variable, the mode cfgs as single files, the program folder and the launcher - made a rehearsal, and read
/// back; and clean against an in-memory Docker. The other verbs that act on the server are proven at the next rehearsal.</summary>
static class RehearseTests
{
    const string Old = "theOldBridgeValue", New = "theNewBridgeValue";

    static readonly string Compose = """
x-os2:
  rcon: &rcon RconOfThisTest
  settings: &settings |
    {
      "schema": 1,
      "server": { "rconPassword": "env:CS2_RCONPW" },
      "bridge": { "url": "http://bridge:8080", "secret": "theOldBridgeValue" },
      "hud": { "addon": "3823993566" },
      "restarts": { "daily": "07:00" },
      "sync": { "mail": { "smtpHost": "mail.example.com" } }
    }
configs:
  old:
    content: x
networks:
  default:
    name: game
services:
  cs2:
    container_name: cs2
    image: docker.io/joedwards32/cs2
    volumes:
      - /srv/game:/home/steam/cs2-dedicated
      - /srv/os2/gamemode_armsrace_server.cfg:/home/steam/cs2-dedicated/game/csgo/cfg/gamemode_armsrace_server.cfg:ro
      - /srv/os2/overstrike2/pre.sh:/home/steam/cs2-dedicated/pre.sh:ro
      - /srv/os2/overstrike2:/home/steam/overstrike2:ro
      - /srv/os2/plugins:/home/steam/cs2-plugins:ro
      - /srv/os2/bridge:/data:ro
    environment:
      OVERSTRIKE2_SETTINGS_CONTENT: *settings
      CS2_RCONPW: *rcon
      CS2_MAXPLAYERS: 10
      CS2_LOG_HTTP_URL: http://bridge:8080/log/theOldBridgeValue
  bridge:
    container_name: bridge
    image: localhost/overstrike2-bridge
    read_only: true
    volumes:
      - /srv/os2/bridge:/data
    environment:
      OVERSTRIKE2_SETTINGS_CONTENT: *settings
      CS2_RCONPW: *rcon
    labels:
      example.rules: |
        egress:
          internet: deny
  sync:
    container_name: sync
    image: docker.io/joedwards32/cs2
    entrypoint:
      - /bin/bash
      - -c
      - cp /home/steam/overstrike2/os2-sync /tmp/os2-sync && exec /tmp/os2-sync serve
    init: true
    volumes:
      - /srv/game:/home/steam/cs2-dedicated:ro
      - /srv/os2/overstrike2:/home/steam/overstrike2:ro
      - /srv/os2/bridge:/data
      - /srv/os2/gamemode_armsrace_server.cfg:/home/steam/sync-cfg/gamemode_armsrace_server.cfg:ro
    environment:
      OVERSTRIKE2_SETTINGS_CONTENT: *settings

""";

    static readonly Dictionary<string, string> Replace = new()
    {
        ["/srv/os2/bridge"] = "/srv/os2/rehearsal/bridge", ["/srv/os2/plugins"] = "/srv/os2/rehearsal/plugins", ["/srv/os2/overstrike2"] = "/srv/os2/rehearsal/overstrike2",
        ["/srv/os2/overstrike2/pre.sh"] = "/srv/os2/rehearsal/overstrike2/pre.sh", ["/srv/os2/gamemode_armsrace_server.cfg"] = "/srv/os2/rehearsal/cfg/gamemode_armsrace_server.cfg",
    };

    static YamlMappingNode Load(string text)
    {
        var s = new YamlStream();
        s.Load(new StringReader(text));
        return (YamlMappingNode)s.Documents[0].RootNode;
    }

    static YamlNode? At(YamlNode? node, params string[] keys)
    {
        foreach (var k in keys)
            node = node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(k), out var v) ? v : null;
        return node;
    }

    static string? Scalar(YamlNode? node, params string[] keys) => (At(node, keys) as YamlScalarNode)?.Value;

    static List<string> Volumes(YamlNode root, string service) => ((At(root, "services", service, "volumes") as YamlSequenceNode)?.Children ?? []).OfType<YamlScalarNode>().Select(v => v.Value ?? "").ToList();

    public static void Run()
    {
        var check = SelfTest.Check;
        var labels = new Dictionary<string, string> { ["monitor.mute"] = "true" };
        var (compose, settings) = Rehearse.ComposeText(Compose, Replace, New, "game", "localhost/overstrike2-bridge:rel", labels, "05:30", 27216, false, "/unused");
        var root = Load(compose);
        check("the old secret is gone, the test secret is in the log push address and the settings",
              !compose.Contains(Old) && Scalar(root, "services", "cs2", "environment", "CS2_LOG_HTTP_URL") == "http://bridge:8080/log/" + New && settings.Contains(New));
        check("every service's volumes: production's bridge data, plugins, program folder, launcher and cfgs replaced by the rehearsal's, the game's tree kept, the options kept",
              Volumes(root, "cs2").SequenceEqual(["/srv/game:/home/steam/cs2-dedicated", "/srv/os2/rehearsal/cfg/gamemode_armsrace_server.cfg:/home/steam/cs2-dedicated/game/csgo/cfg/gamemode_armsrace_server.cfg:ro",
                                                  "/srv/os2/rehearsal/overstrike2/pre.sh:/home/steam/cs2-dedicated/pre.sh:ro", "/srv/os2/rehearsal/overstrike2:/home/steam/overstrike2:ro",
                                                  "/srv/os2/rehearsal/plugins:/home/steam/cs2-plugins:ro", "/srv/os2/rehearsal/bridge:/data:ro"])
              && Volumes(root, "bridge").SequenceEqual(["/srv/os2/rehearsal/bridge:/data"])
              && Volumes(root, "sync").SequenceEqual(["/srv/game:/home/steam/cs2-dedicated:ro", "/srv/os2/rehearsal/overstrike2:/home/steam/overstrike2:ro", "/srv/os2/rehearsal/bridge:/data",
                                                      "/srv/os2/rehearsal/cfg/gamemode_armsrace_server.cfg:/home/steam/sync-cfg/gamemode_armsrace_server.cfg:ro"]));
        var s = Json.Parse(Scalar(root, "services", "sync", "environment", Check.Content) ?? "{}");
        check("the settings: maps.delete dryrun, the daily restart given, no HUD item page visits",
              Json.Str(s, "maps", "delete") == "dryrun" && Json.Str(s, "restarts", "daily") == "05:30" && Json.AsBool(Json.At(s, "hud", "itemPage")) == false);
        check("production's maps folder left alone: maps.layout off, so the start's organize returns before it moves, links, takes in or deletes a map and before any acf write",
              Json.Str(s, "maps", "layout") == "off");
        check("the candidate watcher's background downloads off by default: nothing fetched into production's Workshop store and acf",
              Json.At(s, "maps", "backgroundDownloads") is JsonValue off && off.TryGetValue<bool>(out var on) && !on);
        var (_, keptText) = Rehearse.ComposeText(Compose, Replace, New, "game", "localhost/overstrike2-bridge:rel", labels, "05:30", 27216, false, "/unused", downloads: true);
        var kept = Json.Parse(keptText);
        check("--downloads: maps.backgroundDownloads as production has it (absent there: absent here, core's default, on), every other override as without it",
              !((JsonObject)Json.At(kept, "maps")!).ContainsKey("backgroundDownloads") && Json.Str(kept, "maps", "layout") == "off" && Json.Str(kept, "maps", "delete") == "dryrun"
              && Json.Str(kept, "paths", "handover") == Json.Str(s, "paths", "handover"));
        var prodValues = new[] { "false", "true" }.Select(v =>
        {
            var named = Compose.Replace("\"schema\": 1,", $"\"schema\": 1, \"maps\": {{ \"backgroundDownloads\": {v} }},", StringComparison.Ordinal);
            var (_, withOwn) = Rehearse.ComposeText(named, Replace, New, "game", "localhost/overstrike2-bridge:rel", labels, "off", 27216, false, "/unused", downloads: true);
            var (_, withoutOpt) = Rehearse.ComposeText(named, Replace, New, "game", "localhost/overstrike2-bridge:rel", labels, "off", 27216, false, "/unused");
            return (Kept: Json.Dumps(Json.At(Json.Parse(withOwn), "maps", "backgroundDownloads")), Default: Json.Dumps(Json.At(Json.Parse(withoutOpt), "maps", "backgroundDownloads")));
        }).ToList();
        check("--downloads keeps production's own value, false or true; without it the rehearsal's is false either way",
              prodValues[0] == ("false", "false") && prodValues[1] == ("true", "false"));
        check("nothing points at production's hand-over folder: paths.handover is the rehearsal's own, the game server's view of its sync's work copy",
              Json.Str(s, "paths", "handover") == "/home/steam/cs2-dedicated/workshop-sync/rehearsal/handover" && Json.Str(s, "paths", "handover") != Rehearse.HandoverAt
              && Rehearse.RehearsalHandover("/srv/game/workshop-sync/handover/") == "/srv/game/workshop-sync/rehearsal/handover"
              && Json.Str(s, "paths", "handover")![..^"/rehearsal/handover".Length] + "/rehearsal" == "/home/steam/cs2-dedicated/workshop-sync/rehearsal");
        var withPaths = Compose.Replace("\"schema\": 1,", "\"schema\": 1, \"paths\": { \"handover\": \"/srv/game/workshop-sync/handover\" }, \"maps\": { \"layout\": \"folders\" },", StringComparison.Ordinal);
        var (_, settingsWithPaths) = Rehearse.ComposeText(withPaths, Replace, New, "game", "localhost/overstrike2-bridge:rel", labels, "off", 27216, false, "/unused");
        var sp = Json.Parse(settingsWithPaths);
        check("a production that names its own hand-over folder and layout folders: the rehearsal's hand-over beside it, the layout off all the same",
              Json.Str(sp, "paths", "handover") == "/srv/game/workshop-sync/rehearsal/handover" && Json.Str(sp, "maps", "layout") == "off");
        var stageFlagOff = Rehearse.StageScript("rehearsal", true, false, Rehearse.SyncCopies);
        var stageFlagKept = Rehearse.StageScript("rehearsal", true, true, Rehearse.SyncCopies);
        check("stage: the maintenance flag prep set goes from the bridge's copy, so the candidate starts as production would without it",
              stageFlagOff.Contains("cp -a /b/. $R/bridge/; rm -f $R/bridge/maintenance; ", StringComparison.Ordinal));
        check("stage --keep-flag: the flag stays in the copy, the copy made all the same - the candidate starts under it",
              !stageFlagKept.Contains("maintenance", StringComparison.Ordinal) && stageFlagKept.Contains("cp -a /b/. $R/bridge/", StringComparison.Ordinal)
              && stageFlagKept.Replace(" ", "", StringComparison.Ordinal) == stageFlagOff.Replace("rm -f $R/bridge/maintenance; ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal));
        check("stage: the sync's work copy gets an empty hand-over folder of its own, and production's hand-overs are never copied into it",
              Rehearse.SyncCopies.Contains("mkdir -p /w/rehearsal/handover;", StringComparison.Ordinal) && Rehearse.SyncCopies.Contains("rehearsal|handover|farm) ;;", StringComparison.Ordinal)
              && stageFlagOff.EndsWith(Rehearse.SyncCopies + "echo bridge copy: $(find $R/bridge -type f | wc -l) files; ls -la $R/overstrike2 | cut -c1-90", StringComparison.Ordinal));
        check("the sync isolated by settings: its state and work on copies, its own test port, no hand-over, nothing published, no mail",
              Json.Str(s, "paths", "syncState") == "/home/steam/sync-state/rehearsal" && Json.Str(s, "paths", "syncWork") == "/home/steam/workshop-sync/rehearsal"
              && Json.AsLong(Json.At(s, "sync", "testPort")) == 27216 && Json.AsBool(Json.At(s, "sync", "handover")) == false && Json.AsBool(Json.At(s, "sync", "publish")) == false
              && Json.Str(s, "sync", "mail", "smtpHost") == "");
        check("the settings: every other value as it was, the same text in every service",
              Json.Str(s, "bridge", "url") == "http://bridge:8080" && Json.Str(s, "hud", "addon") == "3823993566" && Json.Str(s, "server", "rconPassword") == "env:CS2_RCONPW"
              && Scalar(root, "services", "cs2", "environment", Check.Content) == Scalar(root, "services", "bridge", "environment", Check.Content));
        check("the sync's own command is as it was: no option of a run that is gone",
              ((At(root, "services", "sync", "entrypoint") as YamlSequenceNode)?.Children.OfType<YamlScalarNode>().Last().Value ?? "").EndsWith("exec /tmp/os2-sync serve", StringComparison.Ordinal));
        check("the stack joins production's network as an external one; no x- block and no inline configs left",
              Scalar(root, "networks", "default", "name") == "game" && Scalar(root, "networks", "default", "external") == "true"
              && !root.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value is "configs" || (k.Value ?? "").StartsWith("x-", StringComparison.Ordinal)));
        check("every service carries the test label and the config's test labels; the bridge's own labels kept",
              new[] { "cs2", "bridge", "sync" }.All(svc => Scalar(root, "services", svc, "labels", TestCopy.TestLabel) == "the release rehearsal" && Scalar(root, "services", svc, "labels", "monitor.mute") == "true")
              && (Scalar(root, "services", "bridge", "labels", "example.rules") ?? "").Contains("internet: deny"));
        check("the bridge on the tag asked for, its root read-only; the RCON password still the same in the game server and the bridge, a number still a number",
              Scalar(root, "services", "bridge", "image") == "localhost/overstrike2-bridge:rel" && Scalar(root, "services", "bridge", "read_only") == "true"
              && Scalar(root, "services", "cs2", "environment", "CS2_RCONPW") == "RconOfThisTest" && Scalar(root, "services", "bridge", "environment", "CS2_RCONPW") == "RconOfThisTest"
              && (At(root, "services", "cs2", "environment", "CS2_MAXPLAYERS") as YamlScalarNode)?.Style is YamlDotNet.Core.ScalarStyle.Plain or YamlDotNet.Core.ScalarStyle.Any);
        var (viaFile, _) = Rehearse.ComposeText(Compose, Replace, New, "game", "localhost/overstrike2-bridge:rel", labels, "off", 27216, true, "/srv/os2/rehearsal/settings/overstrike2.jsonc");
        var fileRoot = Load(viaFile);
        check("--via file: the variable gone, the settings file mounted where the parts look for it",
              Scalar(fileRoot, "services", "cs2", "environment", Check.Content) is null
              && Volumes(fileRoot, "bridge").Contains("/srv/os2/rehearsal/settings/overstrike2.jsonc:/etc/overstrike2/overstrike2.jsonc:ro"));
        var noUrl = Compose.Replace("      CS2_LOG_HTTP_URL: http://bridge:8080/log/theOldBridgeValue\n", "", StringComparison.Ordinal);
        var refusal = SelfTest.Refusal(() => Rehearse.ComposeText(noUrl, Replace, New, "game", "x:y", labels, "off", 27216, false, "/unused"));
        check("a compose file whose game server pushes its log nowhere is refused: no test secret could take the bridge's place", refusal is not null && refusal.Contains("CS2_LOG_HTTP_URL"));
        Clean();
    }

    static JsonObject Container(string image, bool test, params string[] binds) => new()
    {
        ["Id"] = image + "-id", ["Image"] = image, ["State"] = new JsonObject { ["Status"] = "running" },
        ["Config"] = new JsonObject { ["Labels"] = test ? new JsonObject { [TestCopy.TestLabel] = "the release rehearsal" } : new JsonObject() },
        ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray(binds.Select(b => (JsonNode?)b).ToArray()) },
    };

    /// <summary>clean against an in-memory Docker: restore deletes the inspects prep saved, so after it the folders come from production's own
    /// containers; before it from the saved inspects; never while the rehearsal's stack is up, and never from a container that is not there.</summary>
    static void Clean()
    {
        var check = SelfTest.Check;
        var (tmp, config, oldState) = SimConfig.Make("rehearse-sim-");
        try
        {
            File.WriteAllText(config, File.ReadAllText(config).Replace("game_container = \"cs2\"\n", "game_container = \"cs2\"\nsync_container = \"sync\"\n", StringComparison.Ordinal));
            var containers = new Dictionary<string, JsonObject>();
            var helpers = new List<JsonObject>();
            Reply Req(string path, string method, JsonNode? body, byte[]? raw, string ctype, double timeout)
            {
                var parts = path.Split('?')[0].Split('/');
                if (method == "GET" && parts[1] == "containers" && parts[^1] == "json")
                    return containers.TryGetValue(parts[2], out var c) ? Reply.Of(200, c) : throw new DockerError("HTTP Error 404: Not Found");
                if (method == "POST" && path.StartsWith("/containers/create", StringComparison.Ordinal))
                {
                    helpers.Add((JsonObject)body!.DeepClone());
                    return Reply.Of(201, new JsonObject { ["Id"] = "helper-id" });
                }
                if (method == "POST" && parts[^1] == "start")
                    return Reply.Of(204, "");
                if (method == "POST" && parts[^1] == "wait")
                    return Reply.Of(200, new JsonObject { ["StatusCode"] = 0 });
                if (method == "GET" && parts[^1] == "logs")
                    return new Reply(200, [1, 0, 0, 0, 0, 0, 0, 8, .. Encoding.ASCII.GetBytes("cleaned\n")]);
                if (method == "DELETE")
                    return Reply.Of(204, "");
                throw new InvalidOperationException($"unexpected call {method} {path}");
            }
            var tc = new TestCopy(new Harness(config) { Req = Req, NeedLock = () => { }, Rcon = _ => "", Sleep = _ => { } });
            var r = new Rehearse(tc);
            Directory.CreateDirectory(tc.State);   // prep made it, and restore leaves it
            JsonObject Game(bool test = false) => Container("sha256:gameimg", test, "/srv/game:/home/steam/cs2-dedicated", "/srv/os2/plugins:/home/steam/cs2-plugins:ro");
            JsonObject Bridge(bool test = false) => Container("sha256:bridgeimg", test, "/srv/os2/bridge:/data");
            JsonObject Sync(bool test = false) => Container("sha256:gameimg", test, "/srv/game:/home/steam/cs2-dedicated:ro", "/srv/os2/sync-state:/home/steam/sync-state",
                                                            "/srv/os2/workshop-sync:/home/steam/workshop-sync");
            const string Script = "rm -rf -- '/p/rehearsal' /g/update-watch/rehearsal-backup; rm -rf /s/rehearsal /w/rehearsal; echo cleaned";
            string[] binds = ["/srv/os2:/p", "/srv/game:/g", "/srv/os2/sync-state:/s", "/srv/os2/workshop-sync:/w"];
            bool Helped(JsonObject? body) => body is not null && Json.Str(body, "Image") == "sha256:gameimg"
                                             && Json.Arr(body, "HostConfig", "Binds").Select(Json.PyStr).SequenceEqual(binds)
                                             && Json.Arr(body, "Cmd").Select(Json.PyStr).SequenceEqual([Script]) && Json.Str(body, "HostConfig", "NetworkMode") == "none";

            // after restore: production is itself again and nothing is saved any more
            (containers["cs2"], containers["bridge"], containers["sync"]) = (Game(), Bridge(), Sync());
            var refused = SelfTest.Refusal(r.Clean);
            check("clean after restore: the rehearsal folder, the records' backup and the sync's two copies, found through production's own containers",
                  refused is null && helpers.Count == 1 && Helped(helpers[0]));

            // the stack still up: its containers carry the test label, and the folders are their mounts
            helpers.Clear();
            (containers["cs2"], containers["bridge"], containers["sync"]) = (Game(true), Bridge(true), Sync(true));
            refused = SelfTest.Refusal(r.Clean);
            check("clean while the rehearsal's stack is up is refused, and nothing runs", refused is not null && refused.Contains("down first") && helpers.Count == 0);

            // before restore: production aside, the stack down - the saved inspects say where things are
            containers.Clear();
            File.WriteAllText(tc.ProdSaved, Game().ToJsonString());
            File.WriteAllText(tc.BridgeSaved, Bridge().ToJsonString());
            File.WriteAllText(Path.Combine(tc.State, "sync-inspect.json"), Sync().ToJsonString());
            refused = SelfTest.Refusal(r.Clean);
            check("clean before restore: the same folders, from the inspects prep saved", refused is null && helpers.Count == 1 && Helped(helpers[0]));

            // nothing saved and nothing there: no guess
            helpers.Clear();
            foreach (var f in new[] { tc.ProdSaved, tc.BridgeSaved, Path.Combine(tc.State, "sync-inspect.json") })
                File.Delete(f);
            refused = SelfTest.Refusal(r.Clean);
            check("clean with no saved inspect and no production container is refused, and nothing runs",
                  refused is not null && refused.Contains("was not saved and cs2 does not exist") && helpers.Count == 0);
        }
        finally
        {
            SimConfig.Done(tmp, oldState);
        }
    }
}
