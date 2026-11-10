using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>What only the restart watcher or other players can send the bridge, sent by hand, for the screenshots no harness verb reaches
/// (tools/client/probe/bridgepost.py, ported): a restart announced, an update's notice, fake voters. A TEST COPY AND THE CANDIDATE BRIDGE, OR
/// THE RELEASE REHEARSAL'S OWN STACK, ONLY: production's bridge never hears any of it. Before every send all of this must hold: the harness holds
/// the server lock; the game server's container is a test copy and running; the bridge's container is the candidate and running, with its /data
/// on the -candidate copy - or the rehearsal's bridge and running (rehearse up: the rehearsal's label, on the game server too), with its /data on
/// the rehearsal folder's bridge/ copy (Rehearse.Folder: rehearse.dir, else "rehearsal" beside production's bridge data folder), never
/// production's data folder, inside it or around it; production's game server and production's bridge are both aside, exited. Only the routes
/// below, and no verb takes a route or a free text body from the command line. The calls go from inside the game container to the bridge's
/// container name; the secret travels in the exec's environment and is never printed.
///
///     bridgepost restart &lt;kind&gt; &lt;when&gt; &lt;seconds&gt;    /restart-notice: os2-restart 1 &lt;kind&gt; &lt;when&gt; &lt;seconds&gt;
///     bridgepost restart-cancel &lt;kind&gt;              /restart-notice: os2-restart 1 cancel &lt;kind&gt;
///     bridgepost updated &lt;kind&gt; &lt;words...&gt;          /updated: os2-updated 1 &lt;kind&gt; &lt;words&gt;
///     bridgepost quit-notice                        /quitting: os2-quit 1 daily
///     bridgepost fake-on [n]                        /log: n of the fake players (1-4: Test A to Test D) connect and enter the game
///     bridgepost fake-off                           /log: all of them leave, and HUDtest
///     bridgepost fake-crash                         /log: the first three time out (marks the map, rolls the dice)
///     bridgepost client-off &lt;account&gt;               /log: the bridge takes that real player for gone
///     bridgepost client-on &lt;account&gt;                /log: a chat line under the test name
///     bridgepost act &lt;fake 0-3&gt; &lt;word&gt;              /act: that fake player's click: next, random, previous, restart, rematch, time10, time60,
///                                                   cancel-restart, cancel-rematch, cancel-previous (a click on that button's countdown: its Cancel)
///     bridgepost vote &lt;fake 0-3&gt; &lt;card&gt;             /act: that fake player's click on the open vote's card
///     bridgepost add &lt;fake 0-3&gt; &lt;rank|id&gt;           /act: that fake player's Play on a map's card in the map browser (!map &lt;rank|id&gt;)
///     bridgepost cancel-pick &lt;fake 0-3&gt; &lt;key&gt;       /act: that fake player's click on a map's card whose confirmed pick counts down (!cancel pick &lt;key&gt;)
///     bridgepost names-check                        the vote's lines of the bridge's state (who voted, who has not, who started it): refuses
///                                                   when a private name is among them - the client's own Steam name or client.private_names -
///                                                   printing none
///     bridgepost check                              the checks above, nothing sent</summary>
sealed class BridgePost(TestCopy tc)
{
    public static readonly (string Name, string Sid)[] Fakes =
        [("Test A", "[U:1:42424251]"), ("Test B", "[U:1:42424252]"), ("Test C", "[U:1:42424253]"), ("Test D", "[U:1:42424254]")];   // test names only: the pictures are public
    const string ClientName = "Alice";   // docshots' test name for the real client, in what the bridge says about them
    public static readonly Dictionary<string, string> Acts = new()
    {
        ["next"] = "!next", ["random"] = "!next random", ["previous"] = "!previous", ["restart"] = "!restart", ["rematch"] = "!rematch",
        ["time10"] = "!time 10", ["time60"] = "!time 60",
        ["cancel-restart"] = "!cancel restart", ["cancel-rematch"] = "!cancel rematch", ["cancel-previous"] = "!cancel previous",
    };
    static readonly (string Name, string Sid) HudTest = ("HUDtest", "[U:1:42424243]");
    static readonly string[] Kinds = ["game-update", "our-update", "daily", "downloads"];
    static readonly string[] When = ["match-end", "now"];
    public const int RoundTries = 5;
    const double RoundWait = 2.0;
    /// <summary>Inside the game container: the URL from the exec's environment, the body from its argument; only the answer comes back.</summary>
    public const string Post = "import os,sys,urllib.request as u;r=u.Request(os.environ['OS2_BRIDGE_URL'],data=sys.argv[1].encode(),method='POST');"
                               + "a=u.urlopen(r,timeout=8);print(a.status,a.read(400).decode('utf-8','replace'))";
    /// <summary>The vote's line of the state, and nothing else of it: the rest has players' names in it.</summary>
    public const string GetVote = "import os,urllib.request as u;a=u.urlopen(os.environ['OS2_BRIDGE_URL'],timeout=8);"
                                  + "print('\\n'.join(l for l in a.read().decode('utf-8','replace').splitlines() if l.startswith('mv\\t')))";
    /// <summary>The vote's lines of the state, every one (mv, mvw who has not voted, mvs who started it, ...): the names a ballot shows. Never
    /// printed by this class.</summary>
    public const string GetVoteLines = "import os,urllib.request as u;a=u.urlopen(os.environ['OS2_BRIDGE_URL'],timeout=8);"
                                       + "print('\\n'.join(l for l in a.read().decode('utf-8','replace').splitlines() if l.startswith('mv')))";

    readonly Harness h = tc.H;

    static Exit Refuse(string why) => Out.Stop($"REFUSED: {why} - nothing sent");

    /// <summary>A container's test label (cs2.test-config), or null: production's containers have none.</summary>
    static string? Label(JsonNode? d) => d is null ? null : Json.AsStr(InspectOf.Labels(d)[TestCopy.TestLabel]);

    /// <summary>Where the rehearsal's bridge has its /data, for production's bridge data folder: the rehearsal folder's bridge/ copy, by the
    /// rehearsal's own rule (Rehearse.Folder); null when that is no place for it - production's data folder, inside it or around it among them.</summary>
    string? RehearsalData(string? prodData) =>
        prodData is { Length: > 0 } p && p.StartsWith('/') && Rehearse.Folder(h.Cfg.Str("rehearse.dir"), p) is (var dir, null) ? dir + "/bridge" : null;

    /// <summary>Every check of the first paragraph; returns the bridge's inspect: the candidate's or the rehearsal's.</summary>
    JsonObject Gate()
    {
        h.NeedLock();
        var game = tc.Inspect(tc.Game);
        if (game is null || !TestCopy.IsTest(game))
            throw Refuse($"{tc.Game} is not a test copy");
        if (Json.Str(game, "State", "Status") != "running")
            throw Refuse("the test copy is not running");
        var b = tc.Inspect(tc.Bridge);
        var tag = Label(b);
        if (b is null || (tag != TestCopy.BridgeTag && tag != Rehearse.Purpose))
            throw Refuse($"{tc.Bridge} is neither the candidate nor the rehearsal's bridge");
        var rehearsal = tag == Rehearse.Purpose;
        if (rehearsal != (Label(game) == Rehearse.Purpose))   // one test: the rehearsal's stack, or a test copy and the candidate
            throw Refuse($"{tc.Game} and {tc.Bridge} are not of one test: the rehearsal's label is on one of them only");
        var who = rehearsal ? "the rehearsal's bridge" : "the candidate";
        if (Json.Str(b, "State", "Status") != "running")
            throw Refuse($"{who} is not running");
        var data = TestCopy.DataMount(b);
        if (!rehearsal && (string.IsNullOrEmpty(data) || !data.TrimEnd('/').EndsWith("-candidate", StringComparison.Ordinal)))
            throw Refuse("the candidate's /data is not its copy");
        if (rehearsal)
        {
            var aside = tc.Inspect(tc.BridgeAside);
            var own = RehearsalData(aside is null ? null : TestCopy.DataMount(aside));
            if (string.IsNullOrEmpty(data) || own is null || data.TrimEnd('/') != own)
                throw Refuse("the rehearsal's bridge does not have the rehearsal folder's bridge/ copy at /data");
        }
        foreach (var (name, what) in new[] { (tc.Aside, "production's game server"), (tc.BridgeAside, "production's bridge") })
        {
            var d = tc.Inspect(name);
            if (d is null || Json.Str(d, "State", "Status") != "exited")
                throw Refuse($"{what} is not aside and stopped");
        }
        return b;
    }

    /// <summary>The candidate's port and secret, from its own inspect: its settings text when it has one, else the environment. Never printed.</summary>
    static (string Secret, string Port) Route(JsonObject b)
    {
        var secret = InspectOf.EnvOrNull(b, "LOG_SECRET") is { Length: > 0 } s ? s : "";
        var port = InspectOf.EnvOrNull(b, "HTTP_PORT") is { Length: > 0 } p ? p : "8080";
        var text = InspectOf.EnvOrNull(b, TestCopy.Content);
        if (!string.IsNullOrEmpty(text))
        {
            JsonNode? settings;
            try
            {
                settings = Json.Jsonc(text);
            }
            catch (JsonSyntaxError)
            {
                throw Refuse("the candidate's settings text cannot be read");
            }
            var section = Json.At(settings, "bridge") is JsonObject o ? o : [];
            secret = Json.Truthy(section["secret"]) ? Json.PyStr(section["secret"]) : secret;
            port = Json.Truthy(section["port"]) ? Json.PyStr(section["port"]) : port;
            if (secret.StartsWith("env:", StringComparison.Ordinal))
                secret = InspectOf.EnvOrNull(b, secret[4..]) ?? "";
        }
        if (secret.Length == 0 || secret.StartsWith("file:", StringComparison.Ordinal))
            throw Refuse("the candidate's secret cannot be read from its inspect");
        return (secret, port);
    }

    /// <summary>One exec in the test copy, the URL in its environment; its output with the secret scrubbed, should it ever be in there.</summary>
    string ExecInCopy(string url, IReadOnlyList<string> cmd, string secret) =>
        Py.Strip(h.ExecIn(tc.Game, cmd, new() { ["OS2_BRIDGE_URL"] = url }).Replace(secret, "<secret>", StringComparison.Ordinal));

    /// <summary>answers: the route says ok or refused in words; else (/log, /act) the status is all there is, and what follows it is not printed.</summary>
    void Send(JsonObject b, string path, string body, bool answers = true)
    {
        var (secret, port) = Route(b);
        var answer = ExecInCopy($"http://{tc.Bridge}:{port}/{path}/{secret}", ["python3", "-c", Post, body], secret);
        var ok = answers ? answer.StartsWith("200 ok:", StringComparison.Ordinal) : Py.Slice(answer, 0, 3) == "200" && Py.Slice(answer, 3, 4) is "" or " " or "\n";
        var said = answers || !ok ? answer : "200";
        Out.Print(Out.Clock().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + $"/{path}: " + Py.Slice(string.Join(" | ", body.Split('\n')), 0, 160).Replace('\t', ' ')
                  + " -> " + Py.Slice(said, 0, 200));
        if (!ok)
            throw new Exit(1);
    }

    /// <summary>The round of the vote that is open, from the bridge's own state; refuses when none has come up in RoundTries looks.</summary>
    string OpenRound(JsonObject b)
    {
        var (secret, port) = Route(b);
        for (var i = 0; i < RoundTries; i++)
        {
            var state = ExecInCopy($"http://{tc.Bridge}:{port}/hud/{secret}", ["python3", "-c", GetVote], secret);
            var line = Py.SplitLines(state).FirstOrDefault(l => l.StartsWith("mv\t", StringComparison.Ordinal))?.Split('\t');
            if (line is { Length: >= 3 } && line[2] == "open")
                return line[1];
            if (i + 1 < RoundTries)
                h.Sleep(RoundWait);
        }
        throw Refuse("no vote is open");
    }

    static string LogLines(IEnumerable<string> lines)
    {
        var stamp = Out.Clock().ToString("MM/dd/yyyy - HH:mm:ss.000 - ", CultureInfo.InvariantCulture);
        return string.Join("\n", lines.Select(l => stamp + l));
    }

    static (string Name, string Sid) Fake(string arg) =>
        Py.IsDigits(arg) && int.Parse(arg, CultureInfo.InvariantCulture) < Fakes.Length ? Fakes[int.Parse(arg, CultureInfo.InvariantCulture)]
        : throw Out.Stop($"a fake player is 0-{Fakes.Length - 1}, not {arg}");

    public const string Doc = "bridgepost restart <kind> <when> <seconds> | restart-cancel <kind> | updated <kind> <words...> | quit-notice | fake-on [n] | fake-off | "
                              + "fake-crash | client-off <account> | client-on <account> | act <fake 0-3> <word> | vote <fake 0-3> <card> | add <fake 0-3> <rank|id> | "
                              + "cancel-pick <fake 0-3> <key> | names-check | check";

    public void Main(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || argv[0] is "-h" or "--help")
        {
            Out.Print("usage: os2-deploy " + Doc);
            return;
        }
        var (verb, args) = (argv[0], argv.Skip(1).ToList());
        var b = Gate();
        switch (verb)
        {
            case "check":
                Out.Print("all checks hold: " + (Label(b) == Rehearse.Purpose ? "the rehearsal's stack, its bridge on the rehearsal folder's copy of the data"
                                                 : "a test copy, the candidate on its copy of the data") + ", production and its bridge aside");
                break;
            case "names-check":
                // The bridge knows a player by the name the game's log gave it at the connect; a test name the server goes by (OsDocName) reaches
                // the bridge only with that player's next chat line or click. So a ballot can name the client by its own Steam name: refuse before
                // a picture.
                var (secret, port) = Route(b);
                var voteLines = Py.SplitLines(ExecInCopy($"http://{tc.Bridge}:{port}/hud/{secret}", ["python3", "-c", GetVoteLines], secret));
                var privateNames = h.PrivateNames();
                if (privateNames.Count == 0)
                    throw Out.Stop("REFUSED: no private name is known to check against (Steam names no logged-in account, client.private_names is empty)");
                var hits = privateNames.Where(nm => voteLines.Any(l => l.Contains(nm, StringComparison.OrdinalIgnoreCase))).ToList();
                if (hits.Count > 0)
                    throw Out.Stop($"REFUSED: {hits.Count} private name(s) among the vote's names in the bridge's state - nothing may be shot");
                Out.Print($"no private name among the vote's names ({voteLines.Count} line(s) of the vote in the bridge's state)");
                break;
            case "restart":
                if (args.Count != 3 || !Kinds.Contains(args[0]) || !When.Contains(args[1]) || !Py.IsDigits(args[2]))
                    throw Out.Stop($"usage: restart <{string.Join('|', Kinds)}> <{string.Join('|', When)}> <seconds>");
                Send(b, "restart-notice", string.Join("\t", ["os2-restart", "1", .. args]));
                break;
            case "restart-cancel":
                if (args.Count != 1 || !Kinds.Contains(args[0]))
                    throw Out.Stop($"usage: restart-cancel <{string.Join('|', Kinds)}>");
                Send(b, "restart-notice", string.Join("\t", "os2-restart", "1", "cancel", args[0]));
                break;
            case "updated":
                if (args.Count < 2 || !Kinds.Contains(args[0]) || !Regex.IsMatch(string.Join(" ", args.Skip(1)), @"^[\w .-]{1,60}$"))
                    throw Out.Stop($"usage: updated <{string.Join('|', Kinds)}> <words>");
                Send(b, "updated", string.Join("\t", "os2-updated", "1", args[0], string.Join(" ", args.Skip(1))));
                break;
            case "fake-on":
                var n = args.Count > 0 && Py.IsDigits(args[0]) ? int.Parse(args[0], CultureInfo.InvariantCulture) : args.Count == 0 ? Fakes.Length : 0;
                if (n < 1 || n > Fakes.Length)
                    throw Out.Stop($"usage: fake-on [1-{Fakes.Length}]");
                var lines = Fakes.Take(n).Select((f, i) => $"\"{f.Name}<9{i}><{f.Sid}><>\" connected, address \"\"")
                                 .Concat(Fakes.Take(n).Select((f, i) => $"\"{f.Name}<9{i}><{f.Sid}><CT>\" entered the game"));
                Send(b, "log", LogLines(lines), answers: false);
                break;
            case "fake-off":
                Send(b, "log", LogLines(Fakes.Append(HudTest).Select((f, i) => $"\"{f.Name}<9{i}><{f.Sid}><CT>\" disconnected (reason \"NETWORK_DISCONNECT_DISCONNECT_BY_USER\")")), answers: false);
                break;
            case "fake-crash":
                Send(b, "log", LogLines(Fakes.Take(3).Select((f, i) => $"\"{f.Name}<9{i}><{f.Sid}><CT>\" disconnected (reason \"NETWORK_DISCONNECT_TIMEDOUT\")")), answers: false);
                break;
            case "quit-notice":
                Send(b, "quitting", "os2-quit\t1\tdaily");
                break;
            case "client-off":
            case "client-on":
                if (args.Count != 1 || !Py.IsDigits(args[0]))
                    throw Out.Stop($"usage: {verb} <account>");
                var who = $"\"{ClientName}<99><[U:1:{args[0]}]><CT>\"";
                Send(b, "log", LogLines([verb == "client-off" ? $"{who} disconnected (reason \"NETWORK_DISCONNECT_DISCONNECT_BY_USER\")" : $"{who} say \"back\""]), answers: false);
                break;
            case "act":
                if (args.Count != 2 || !Acts.ContainsKey(args[1]))
                    throw Out.Stop($"usage: act <fake 0-{Fakes.Length - 1}> <{string.Join('|', Acts.Keys)}>");
                var (name, sid) = Fake(args[0]);
                Send(b, "act", $"{sid}\t{name}\t{Acts[args[1]]}", answers: false);
                break;
            case "add":
                if (args.Count != 2 || !Py.IsDigits(args[1]) || args[1].Length > 12)
                    throw Out.Stop($"usage: add <fake 0-{Fakes.Length - 1}> <rank|id>");
                var (adder, adderSid) = Fake(args[0]);
                Send(b, "act", $"{adderSid}\t{adder}\t!map {args[1]}", answers: false);
                break;
            case "cancel-pick":
                if (args.Count != 2 || !Regex.IsMatch(args[1], "^[a-z0-9_]{1,32}$"))
                    throw Out.Stop($"usage: cancel-pick <fake 0-{Fakes.Length - 1}> <key: a Workshop id, or an official map's name>");
                var (canceller, cancellerSid) = Fake(args[0]);
                Send(b, "act", $"{cancellerSid}\t{canceller}\t!cancel pick {args[1]}", answers: false);
                break;
            case "vote":
                if (args.Count != 2 || !Py.IsDigits(args[1]))
                    throw Out.Stop($"usage: vote <fake 0-{Fakes.Length - 1}> <card>");
                var (voter, voterSid) = Fake(args[0]);
                Send(b, "act", $"{voterSid}\t{voter}\t!mv {OpenRound(b)} {args[1]}", answers: false);
                break;
            default:
                throw Out.Stop($"unknown verb {verb}: usage: os2-deploy {Doc}");
        }
    }

    public static int Command(string[] argv)
    {
        var (args, config) = Harness.TakeConfig(argv);
        if (args.Count == 0 || args[0] is "-h" or "--help")
        {
            Out.Print("usage: os2-deploy " + Doc);
            return 0;
        }
        new BridgePost(new TestCopy(new Harness(config))).Main(args);
        return 0;
    }
}
