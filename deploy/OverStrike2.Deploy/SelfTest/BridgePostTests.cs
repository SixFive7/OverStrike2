using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>tools/client/probe/test_bridgepost.py, ported: it sends only with a test copy, the candidate on its copy of the data and
/// production aside; the secret never reaches the output; the bodies are the bridge's own format; and the two programs it runs inside the
/// game container do what they say, against a local server (run with this PC's Python).</summary>
static class BridgePostTests
{
    const string Secret = "s3cr3t-log-word";
    const string Client = "42424242";   // a made-up account for the real client, in what the bridge is told about it

    static Dictionary<string, JsonObject> World()
    {
        JsonObject C(string status = "running", JsonObject? labels = null, string[]? env = null, string[]? binds = null) => new()
        {
            ["Id"] = new string('x', 64), ["State"] = new JsonObject { ["Status"] = status },
            ["Config"] = new JsonObject { ["Labels"] = labels ?? new JsonObject(), ["Env"] = new JsonArray((env ?? []).Select(e => (JsonNode?)e).ToArray()) },
            ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray((binds ?? []).Select(b => (JsonNode?)b).ToArray()) }, ["Mounts"] = new JsonArray(),
        };
        return new()
        {
            ["cs2"] = C(labels: new JsonObject { [TestCopy.TestLabel] = "copy" }),
            ["bridge"] = C(labels: new JsonObject { [TestCopy.TestLabel] = TestCopy.BridgeTag }, env: [$"LOG_SECRET={Secret}", "HTTP_PORT=8081"], binds: ["/data/bridge-candidate:/data:rw"]),
            ["cs2-prod-aside"] = C("exited"), ["bridge-prod-aside"] = C("exited"),
        };
    }

    static string TupleRepr(string[] items) => "(" + string.Join(", ", items.Select(Py.ReprStr)) + (items.Length == 1 ? ",)" : ")");

    public static void Run()
    {
        var check = SelfTest.Check;
        var (tmp, config, oldState) = SimConfig.Make("bp-sim-");
        try
        {
            var execs = new List<JsonObject>();
            var lockHeld = true;
            var mv = "mv\t1793900000\topen\t55\t2\t0\tmid\t\t1";
            var answer = "ok: taken at {url}";
            var containers = World();
            Reply Req(string path, string method, JsonNode? body, byte[]? raw, string ctype, double timeout)
            {
                if (method == "GET" && path.StartsWith("/containers/", StringComparison.Ordinal))
                {
                    if (!containers.TryGetValue(path.Split('/')[2], out var d))
                        throw new DockerError("HTTP Error 404: Not Found");
                    return Reply.Of(200, d);
                }
                if (method == "POST" && path == "/containers/cs2/exec")
                {
                    execs.Add((JsonObject)body!.DeepClone());
                    return Reply.Of(201, new JsonObject { ["Id"] = "e1" });
                }
                if (method == "POST" && path.StartsWith("/exec/", StringComparison.Ordinal))
                {
                    var url = Json.Arr(execs[^1], "Env").Select(Json.PyStr).First(e => e.StartsWith("OS2_BRIDGE_URL=", StringComparison.Ordinal)).Split('=', 2)[1];
                    if (url.Contains("/hud/"))
                        return Reply.Of(200, mv + "\n");    // GET_VOTE passes on the vote's line alone (held below against a real server)
                    if (url.Contains("/log/"))
                        return Reply.Of(200, "200 \n");
                    if (url.Contains("/act/"))
                        return Reply.Of(200, "200 gg-hud\tHUD holds Secret Name\n");
                    return Reply.Of(200, "200 " + answer.Replace("{url}", url, StringComparison.Ordinal));   // a bridge that echoes its URL: the scrub must catch it
                }
                throw new InvalidOperationException($"unexpected call {method} {path}");
            }
            var h = new Harness(config)
            {
                Req = Req,
                Rcon = _ => "",
                NeedLock = () =>
                {
                    if (!lockHeld)
                        throw Out.Stop("the server lock is not held");
                },
                Sleep = _ => { },
            };
            var bp = new BridgePost(new TestCopy(h));

            // (exit code - a refusal's words as a string, a plain exit as its number - or null, what it printed)
            (object? Code, string Out) RunBp(params string[] argv)
            {
                object? code = null;
                var (_, exit, output) = SelfTest.Capture(() =>
                {
                    try
                    {
                        bp.Main(argv);
                    }
                    catch (Exit e) when (e.Text is not null)
                    {
                        code = e.Text;
                    }
                    return 0;
                });
                return (code ?? exit, output);
            }
            void Refused(string why, params string[] argv)
            {
                var before = execs.Count;
                var (code, _) = RunBp(argv.Length > 0 ? argv : ["restart", "our-update", "match-end", "0"]);
                check($"{why}: refused and nothing sent (exit {Py.Repr(code)})", code is string && execs.Count == before);
            }
            static string Cmd3(JsonObject e) => Json.PyStr(Json.Arr(e, "Cmd")[3]);
            static string Env0(JsonObject e) => Json.PyStr(Json.Arr(e, "Env")[0]);

            // every send, the OK path first
            var (code, output) = RunBp("check");
            check("check: all hold, nothing sent", code is null && output.Contains("all checks hold") && execs.Count == 0);

            (code, output) = RunBp("restart", "our-update", "match-end", "0");
            var e = execs[^1];
            check("restart: sent", code is null);
            check($"restart: the bridge's own body: {Py.Repr(Json.Arr(e, "Cmd").Skip(3).Select(Json.PyStr).ToList())}",
                  Json.PyStr(Json.Arr(e, "Cmd")[0]) == "python3" && Cmd3(e) == "gg-restart\t1\tour-update\tmatch-end\t0");
            check("restart: the candidate's name, port, route and secret, in the exec's environment",
                  Json.Arr(e, "Env").Select(Json.PyStr).SequenceEqual([$"OS2_BRIDGE_URL=http://bridge:8081/restart-notice/{Secret}"]));
            check("the secret is not on the exec's command line", !string.Join(" ", Json.Arr(e, "Cmd").Select(Json.PyStr)).Contains(Secret));
            check("the secret never reaches the output, even when the answer holds it", !output.Contains(Secret) && output.Contains("<secret>"));
            foreach (var bad in new[] { "refused: the kind is daily", "busy: the bridge did not get to it within 5 s", "error: boom" })
            {
                answer = bad;
                (code, output) = RunBp("restart", "our-update", "match-end", "0");
                check($"an answer that is not ok fails the call and shows: {bad}", code is 1 && output.Contains(bad));
            }
            answer = "ok: taken at {url}";

            RunBp("restart-cancel", "daily");
            check("restart-cancel: its body", Cmd3(execs[^1]) == "gg-restart\t1\tcancel\tdaily");
            RunBp("updated", "game-update", "CS2", "1.41.9.6");
            check("updated: its body and route", Cmd3(execs[^1]) == "gg-updated\t1\tgame-update\tCS2 1.41.9.6" && Env0(execs[^1]).Contains("/updated/"));

            (code, output) = RunBp("fake-on", "2");
            var lines = Cmd3(execs[^1]).Split('\n');
            var player = new Regex(@"^\d\d/\d\d/\d{4} - \d\d:\d\d:\d\d\.000 - ""(.+?)<(\d+)><(\[U:1:\d+\])><([^>]*)>"" (.+)$");   // bridge/Program.cs's PlayerRx behind the stamp
            check($"fake-on 2: four log lines the bridge's PlayerRx reads: {Py.Repr(lines.Take(1).ToList())}",
                  code is null && Env0(execs[^1]).Contains("/log/") && lines.Length == 4 && lines.All(l => player.IsMatch(l)));
            check("fake-on: connected, then entered, under test names",
                  player.Match(lines[0]).Groups[5].Value.StartsWith("connected, address", StringComparison.Ordinal) && player.Match(lines[^1]).Groups[5].Value == "entered the game" && lines[0].Contains("Test A"));
            (code, output) = RunBp("fake-off");
            var gone = Cmd3(execs[^1]).Split('\n');
            check("fake-off: every fake leaves for real, and HUDtest (a reason with SHUTDOWN in it would only be a reload)",
                  code is null && gone.Length == 5 && gone.All(l => l.Contains("disconnected (reason") && !l.Contains("SHUTDOWN")) && gone[^1].Contains("HUDtest"));

            (code, output) = RunBp("act", "0", "next");
            check("act: the fake's Start a vote", code is null && Cmd3(execs[^1]) == "[U:1:42424251]\tTest A\t!next" && Env0(execs[^1]).Contains("/act/"));
            check("an /act answer, the live state with its names, is not printed", !output.Contains("Secret Name"));
            RunBp("act", "3", "time10");
            check("act: a word becomes the bridge's own command", Cmd3(execs[^1]) == "[U:1:42424254]\tTest D\t!time 10");
            RunBp("fake-crash");
            var crash = Cmd3(execs[^1]).Split('\n');
            check("fake-crash: three players time out (BrokenWhy.cs: the third marks the map)", crash.Length == 3 && crash.All(l => l.Contains("NETWORK_DISCONNECT_TIMEDOUT")));
            RunBp("quit-notice");
            check("quit-notice: its body and route", Cmd3(execs[^1]) == "gg-quit\t1\tdaily" && Env0(execs[^1]).Contains("/quitting/"));
            RunBp("client-off", Client);
            var off = Cmd3(execs[^1]);
            var mOff = player.Match(off);
            check($"client-off: a real departure, under the test name: {off}",
                  mOff.Success && mOff.Groups[1].Value == "Alice" && mOff.Groups[3].Value == $"[U:1:{Client}]" && off.Contains("disconnected (reason") && !off.Contains("SHUTDOWN"));
            RunBp("client-on", Client);
            var on = Cmd3(execs[^1]);
            var mOn = player.Match(on);
            check($"client-on: a chat line, under the test name: {on}", mOn.Success && mOn.Groups[1].Value == "Alice" && mOn.Groups[5].Value == "say \"back\"");

            var before = execs.Count;
            (code, output) = RunBp("vote", "1", "4");
            check($"vote: the round from the bridge's state, the click in /act's format: {Py.Repr(Json.Arr(execs[^1], "Cmd").Skip(3).Select(Json.PyStr).ToList())}",
                  code is null && execs.Count == before + 2 && Env0(execs[before]).Contains("/hud/") && !Env0(execs[before]).Contains('?')
                  && Cmd3(execs[before + 1]) == "[U:1:42424252]\tTest B\t!mv 1793900000 4");
            mv = "mv\t1793900000\tdone\t0\t2\t2\tmid\t\t1";
            before = execs.Count;
            (code, _) = RunBp("vote", "1", "4");
            check("vote: refused when no vote comes up: only the state was read",
                  code is string s1 && s1.Contains("no vote is open") && execs.Count == before + BridgePost.RoundTries && execs.Skip(before).All(x => Env0(x).Contains("/hud/")));
            mv = "";
            (code, _) = RunBp("vote", "1", "4");
            check("vote: refused when the state has no vote line at all", code is string s2 && s2.Contains("no vote is open"));
            mv = "mv\t1793900000\topen\t55\t2\t0\tmid\t\t1";

            // a candidate with a settings text instead of the environment
            containers["bridge"]["Config"]!["Env"] = new JsonArray("OVERSTRIKE2_SETTINGS_CONTENT={\"schema\":1,\"bridge\":{\"secret\":\"env:THE_WORD\",\"port\":9000}}", $"THE_WORD={Secret}");
            RunBp("restart-cancel", "daily");
            check("the settings text's port and secret, by its env: reference",
                  Json.Arr(execs[^1], "Env").Select(Json.PyStr).SequenceEqual([$"OS2_BRIDGE_URL=http://bridge:9000/restart-notice/{Secret}"]));
            containers["bridge"]["Config"]!["Env"] = new JsonArray("OVERSTRIKE2_SETTINGS_CONTENT={\"schema\":1,\"bridge\":{\"secret\":\"file:/run/x\"}}");
            Refused("a secret the inspect cannot give");

            // every way it must refuse
            containers = World();
            lockHeld = false;
            before = execs.Count;
            (code, _) = RunBp("restart", "our-update", "match-end", "0");
            check("no server lock: nothing sent", code is not null && execs.Count == before);
            lockHeld = true;

            foreach (var (why, change) in new (string, Action<Dictionary<string, JsonObject>>)[]
                     {
                         ("cs2 is production", w => w["cs2"]["Config"]!["Labels"] = new JsonObject()),
                         ("no cs2 at all", w => w.Remove("cs2")),
                         ("the copy is stopped", w => w["cs2"]["State"]!["Status"] = "exited"),
                         ("the bridge is production's", w => w["bridge"]["Config"]!["Labels"] = new JsonObject()),
                         ("the bridge is some other test thing", w => w["bridge"]["Config"]!["Labels"] = new JsonObject { [TestCopy.TestLabel] = "something else" }),
                         ("no bridge at all", w => w.Remove("bridge")),
                         ("the candidate is stopped", w => w["bridge"]["State"]!["Status"] = "exited"),
                         ("the candidate's /data is production's folder", w => w["bridge"]["HostConfig"]!["Binds"] = new JsonArray("/data/bridge:/data:rw")),
                         ("production's game server is not aside", w => w.Remove("cs2-prod-aside")),
                         ("production's bridge is not aside", w => w.Remove("bridge-prod-aside")),
                         ("production's bridge is aside but running", w => w["bridge-prod-aside"]["State"]!["Status"] = "running"),
                         ("production's game server is aside but running", w => w["cs2-prod-aside"]["State"]!["Status"] = "running"),
                     })
            {
                containers = World();
                change(containers);
                Refused(why);
            }
            containers = World();

            // bad arguments: no free route, no free text
            foreach (var args in new[]
                     {
                         new[] { "restart", "nuke", "now", "5" }, ["restart", "daily", "soon", "5"], ["restart", "daily", "now", "x"], ["updated", "daily", "a;b"], ["fake-on", "9"],
                         ["fake-on", "x"], ["act", "0"], ["act", "7", "next"], ["act", "0", "quit"], ["act", "0", "!rcon x"], ["client-off"], ["client-on", "x"],
                         ["vote", "7", "1"], ["vote", "x", "1"], ["vote", "1", "x"], ["raw", "quitting", "x"],
                     })
            {
                before = execs.Count;
                (code, _) = RunBp(args);
                check($"bad arguments {TupleRepr(args)}: nothing sent", code is not null && execs.Count == before);
            }
            check("/quitting is reached by quit-notice alone", execs.Count(x => Env0(x).Contains("/quitting/")) == 1);

            // the two programs that run inside the game container, for real, against a local server standing in for the bridge
            InnerPrograms(check);
        }
        finally
        {
            SimConfig.Done(tmp, oldState);
        }
    }

    /// <summary>bridgepost's POST and GET_VOTE run by this PC's Python against a local HTTP server that stands in for the bridge.</summary>
    static void InnerPrograms(Action<string, bool> check)
    {
        var got = new List<(string Path, string Body)>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!serving.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(serving.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                using (client)
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
                        head.Append((char)one[0]);
                    var request = head.ToString().Split("\r\n");
                    var (method, path) = (request[0].Split(' ')[0], request[0].Split(' ')[1]);
                    var length = request.Select(l => Regex.Match(l, @"^Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success) is { } m ? int.Parse(m.Groups[1].Value) : 0;
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body);
                    string text;
                    if (method == "POST")
                    {
                        lock (got)
                            got.Add((path, Encoding.UTF8.GetString(body)));
                        text = path.Contains("notice") ? "ok: heard" : "";
                    }
                    else
                        text = "gg-hud\tname\nmvo\t1\tHUDtest\nmv\t77\topen\t50\n" + new string('x', 200000) + "\n";
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(bytes);
                }
            }
        });
        var b = $"http://127.0.0.1:{port}";
        (int Code, string Out, string Err)? Inner(string path, params string[] cmd)
        {
            var env = Proc.Environment();
            env["OS2_BRIDGE_URL"] = b + path;
            try
            {
                var r = Proc.Capture(["python", "-I", "-c", .. cmd], env: env, timeoutSeconds: 30);
                return (r.Code, r.Out, r.Err);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
        try
        {
            var post = Inner("/restart-notice/x", BridgePost.Post, "gg-restart\t1\tcancel\tdaily");
            if (post is null)
            {
                SelfTest.Skip("the inner programs: no Python on this PC to run them with (3 checks)", 3);
                return;
            }
            var (code, output, err) = post.Value;
            check($"POST runs and passes the answer on: {Py.Repr(output)} {Py.Repr(Py.Slice(err, -200))}",
                  code == 0 && Py.Strip(output) == "200 ok: heard" && got.Count > 0 && got[^1] == ("/restart-notice/x", "gg-restart\t1\tcancel\tdaily"));
            (code, output, err) = Inner("/log/x", BridgePost.Post, "line one\nline two")!.Value;
            check($"POST with an empty answer, and a body of two lines: {Py.Repr(output)} {Py.Repr(Py.Slice(err, -200))}",
                  code == 0 && Py.Strip(output) == "200" && got[^1].Body == "line one\nline two");
            (code, output, err) = Inner("/hud/x", BridgePost.GetVote)!.Value;
            check($"GET_VOTE gives the vote's line alone, however long the state: {Py.Repr(Py.Slice(output, 0, 80))} {Py.Repr(Py.Slice(err, -200))}",
                  code == 0 && Py.Strip(output) == "mv\t77\topen\t50");
        }
        finally
        {
            serving.Cancel();
            listener.Stop();
            try
            {
                server.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }
    }
}
