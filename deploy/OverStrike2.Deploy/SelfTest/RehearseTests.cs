using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace OverStrike2.Deploy;

/// <summary>The release rehearsal's compose file, offline (rehearse.py had no tests): a made-up stack shaped like a real one - its anchors in an
/// x- block, the settings as one variable, the mode cfgs as single files, the program folder and the launcher - made a rehearsal, and read
/// back. The verbs that act on the server are proven at the next rehearsal.</summary>
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
    }
}
