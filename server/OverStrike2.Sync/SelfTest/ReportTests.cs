// The sync's self-test: the run's report and the problem mail.
using System.Net.Sockets;
using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using OverStrike2.Core;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The run's report text and its backlog, the problem mail - what counts, its words, once a day - and the SMTP client.</summary>
static class ReportTests
{
    public static JsonObject R() => O("""
        {"day": "2026-11-01", "manual": false, "api_stop": "Steam's Web API answered HTTP 429 to QueryFiles - every API step stops for the rest of the day",
         "discovery": {"skipped": "stopped"}, "check": {"asked": 10, "calls": 1}, "downloads": {"ok": 0}, "tests": {"rows": []},
         "published": {}, "limits": {"api_run": 3, "api_run_cap": 500, "api_24h": 3, "api_day_cap": 10000}}
        """);

    static JsonObject With(JsonObject r, params (string Key, JsonNode? Value)[] fields)
    {
        var c = (JsonObject)r.DeepClone();
        foreach (var (k, v) in fields)
            c[k] = v?.DeepClone();
        return c;
    }

    // ---- the mail, and the backlog: strictly most subscribed first, whatever the mode; its size and estimate in the mail
    internal static void MailAndBacklog()
    {
        var r = R();
        var (subject, body) = RunReport.Summary(r, Cfg());
        Check("mail: a stop is in the subject and the body", subject.Contains("STOPPED: HTTP 429") && body.Contains("STOPPED: Steam's Web API answered HTTP 429"));
        Check("mail: the limits are reported", body.Contains("Web API calls this run 3 of 500, last 24 h 3 of 10000"));
        var items = O("""
            {"1": {"tags": ["Deathmatch"], "subs": 100, "size": 10485760}, "2": {"tags": ["Deathmatch"], "subs": 90, "size": 10485760},
             "3": {"tags": ["Wingman"], "subs": 5, "size": 10485760}, "4": {"tags": ["Armsrace", "Deathmatch"], "subs": 50, "size": 10485760},
             "5": {"tags": ["Wingman", "Deathmatch"], "subs": 95, "size": 10485760}, "6": {"tags": ["Armsrace"], "subs": 1, "size": 10485760}}
            """);
        var all = items.Select(kv => kv.Key).ToList();
        Check("backlog: strictly by subscribers across the modes", Verdicts.OrderBacklog(items, all).SequenceEqual(["1", "5", "2", "4", "3", "6"]));
        var bs = Verdicts.BacklogStats(items, all);
        Check("backlog: counted per mode", Node.Int(bs["items"]) == 6 && Node.Eq(bs["by_mode"], O("{\"armsrace\": 2, \"deathmatch\": 4, \"wingman\": 2}")));
        var big = new JsonObject();
        for (var i = 0; i < 5255; i++)
            big[i.ToString()] = O("{\"tags\": [\"Wingman\"], \"subs\": 1, \"size\": 104857600}");
        var bigIds = big.Select(kv => kv.Key).ToList();
        var both = Verdicts.BacklogStats(big, bigIds);
        Check("backlog: no time windows, about 22 h a day at 31 s a map, 2,554 a day - no daily cap: 3 days for the backlog of 2026-11-01",
              Node.Float(both["hours"]) == 22 && Node.Int(both["per_day"]) == 2554 && Node.Int(both["days"]) == 3);
        Check("backlog: a pace measured counts", Node.Int(Verdicts.BacklogStats(big, bigIds, 60.0)["days"]) == 4);
        var r2 = With(r, ("api_stop", null), ("backlog", bs), ("backlog_until", " (until about 2026-11-04)"));
        var (_, body2) = RunReport.Summary(r2, Cfg());
        Check("mail: the backlog's size, per mode, and its estimate", body2.Contains("Backlog: 6 item(s) waiting") && body2.Contains("Deathmatch 4, Wingman 2, Arms Race 2")
              && body2.Contains("until about 2026-11-04") && body2.Contains("strictly by subscribers"));
        var rows = Node.Strings(Enumerable.Range(0, 60).Select(i => $"PASS     map{i} ({i}) - wingman, loads as x in 1s").Append("CRASHER  bad (1) - crashes the server"));
        var r3 = With(r, ("api_stop", null), ("tests", new JsonObject { ["rows"] = rows }),
                      ("published", new JsonObject
                      {
                          ["modes"] = new JsonObject { ["wingman"] = new JsonArray(Enumerable.Range(0, 60).Select(i => (JsonNode?)new JsonArray(i.ToString(), "m")).ToArray()) },
                          ["files"] = Node.Strings(["maps-wingman.json"]),
                      }));
        var (_, body3) = RunReport.Summary(r3, Cfg());
        Check("mail: a long run gives counts, what did not pass, a taste of what did", body3.Contains("60 PASS, 1 CRASHER.") && body3.Contains("CRASHER  bad (1)")
              && body3.Contains("and 50 more passes") && !body3.Contains("map30 "));
        Check("mail: a long mode list is counted", body3.Contains("maps-wingman.json  60 new maps"));
        var split = Enumerable.Range(0, 41).Select(i => $"PASS{(char)(0x1C + i % 4)}map{i}").ToList();
        var (_, body4) = RunReport.Summary(With(r, ("api_stop", null), ("tests", new JsonObject { ["rows"] = Node.Strings(split) })), Cfg());
        Check("mail: a row's first word ends where core's SearchText.Split ends it - at U+001C to U+001F too, as search.json's words do",
              body4.Contains("   41 PASS."));
        Check("mail: a run says when it started", RunReport.Summary(With(r, ("started_hm", "03:30")), Cfg()).Subject.Contains("(the run of 03:30)"));
        // the publish steps of a run add up
        var mp = RunReport.MergePublished(O("""{"listed": [["1", 1, "a", 5]], "modes": {"wingman": [["2", "b"]]}, "files": ["maps.json"], "reload": "x"}"""),
                                     O("""{"listed": [["3", 2, "c", 5]], "modes": {"wingman": [["4", "d"]], "deathmatch": [["5", "e"]]}, "files": ["maps.json", "search.json"], "reload": "y"}"""))!;
        Check("publish: a run's batches add up", Node.Arr(mp["listed"]).Select(x => Node.Str(x![0])).SequenceEqual(["1", "3"])
              && Node.Arr(mp["modes"]!["wingman"]).Select(x => Node.Str(x![0])).SequenceEqual(["2", "4"]) && Node.StrList(mp["files"]) is ["maps.json", "search.json"]
              && Node.Str(mp["reload"]) == "y" && Node.Eq(mp["modes"]!["deathmatch"], A("[[\"5\", \"e\"]]")));
        Check("service: a backlog waits while anything is queued or staged without a verdict",
              Service.BacklogWaits(O("{\"items\": {\"1\": {\"status\": \"queued\"}}}")) && Service.BacklogWaits(O("{\"items\": {\"1\": {\"status\": \"tested\"}}}"))
              && !Service.BacklogWaits(O("{\"items\": {\"1\": {\"status\": \"tested\", \"verdict\": \"pass\"}, \"2\": {\"status\": \"published\"}}}")));
        // nobody on: a map change shows 0 for a moment, so once somebody was seen it takes the quiet minutes
        var real = Hooks.HumansOn;
        var seen = new Queue<int>([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Hooks.HumansOn = (_, _) => seen.Dequeue();
        var fc = new FakeClock(1793500000.0);
        var waits = new List<double>();
        var nq = W.Run(Cfg(), fc, true, n =>
        {
            waits.Add(n);
            fc.T += n;
        });
        Check("wait: somebody on now means waiting, and 0 humans alone is not enough for the next 10 minutes", nq.WaitForNobody("x") && waits.Count == 10);
        seen = new Queue<int>([0]);
        waits.Clear();
        var nq2 = W.Run(Cfg(), fc, true, n =>
        {
            waits.Add(n);
            fc.T += n;
        });
        Check("wait: nobody seen this run: at once", nq2.WaitForNobody("x") && waits.Count == 0);
        Hooks.HumansOn = real;
    }

    public static readonly JsonObject Quiet = O("""
        {"day": "2026-11-03", "started_hm": "12:00", "ended_hm": "15:00",
         "downloads": {"stop": "the run's deadline (time is kept for this batch's tests)", "stop_kind": "routine"},
         "tests": {"stop": "somebody was on the server until the deadline", "stop_kind": "routine"}, "serve": {"stop": "the run's deadline", "stop_kind": "routine"},
         "errors": []}
        """);

    public static JsonObject QuietWith(params (string Key, JsonNode? Value)[] fields)
    {
        var c = (JsonObject)Quiet.DeepClone();
        foreach (var (k, v) in fields)
            c[k] = v?.DeepClone();
        return c;
    }

    // ---- the issues-only mail: what counts, what does not, the words, once a day per problem
    internal static void ProblemMail(string root)
    {
        Check("issues: a run that ended at its deadline, or waited for players, is no problem", Issues.Of(Quiet).Count == 0);
        // the downloading steps' stops come from SteamSession itself, so a stop reworded keeps its problem: the mail goes by their kinds
        Stop? Failing(params string[] reasons)
        {
            var session = new SteamSession(new ScriptedSteam(true), new DownloadBudget(new SyncState(Path.Combine(root, "issues-session.json")), "2026-11-01"),
                                           (_, _, _) => true, _ => { });
            return reasons.Select(r => session.Failed("1", r)).LastOrDefault(s => s is not null);
        }
        var noLogin = new SteamSession(new ScriptedSteam(false, "no answer"), new DownloadBudget(new SyncState(Path.Combine(root, "issues-session.json")), "2026-11-01"),
                                       (_, _, _) => true, _ => { }).Ready(0);
        var stops = new (string Step, Stop? Stop, string Key)[]
        {
            ("downloads", Failing("Rate Limit Exceeded"), "steamcmd-pushback"),
            ("serve", Failing("Access Denied", "Access Denied"), "steamcmd-pushback"),
            ("downloads", Failing("Failure", "Failure", "Failure"), "downloads-in-a-row"),
            ("downloads", noLogin, "steamcmd"),
            ("serve", Failing(SteamCmdWords.NotRunning), "steamcmd"),
            ("serve", new Stop(Stop.HandOversWaiting, "205.0 GB of hand-overs wait for the server already"), "handovers-waiting"),
            ("tests", new Stop(Stop.TestEngine, "test engine did not answer in 90 s"), "test-engine"),
            ("tests", new Stop(Stop.TestTree, "the test tree could not be built: disk full"), "tests-stopped"),
        };
        foreach (var (step, stop, key) in stops)
        {
            var part = new JsonObject();
            stop?.Into(part);
            var got = Issues.Of(QuietWith((step, part)));
            Check($"issues: {key} counts, by its stop's kind {stop?.Kind ?? "none"} ({(got.Count > 0 ? Text.Cut(got[0].What, 70) : "nothing")})",
                  stop is not null && got.Count == 1 && got[0].Key == key && got[0].Did.Length > 0);
        }
        var cases = new (string Extra, string Key)[]
        {
            ("""{"api_stop": "Steam's Web API answered HTTP 429 to QueryFiles - every API step stops for the rest of the day"}""", "steam-webapi"),
            ("""{"discovery": {"skipped": "failed: HTTP 500", "failed": true}}""", "discovery-failed"),
            ("""{"published": {"verdicts": "the report failed (HTTP Error 502) - the next check reports again", "verdicts_failed": true}}""", "verdicts-not-reported"),
            ("""{"check": {"suspicious": true}}""", "check-not-believed"),
            ("""{"serve": {"handover_failed": [["305", "its folder is empty"]]}}""", "handover-failed"),
            ("""{"disk": {"low": "80.0 GB free on the game's volume (the mark is 100.0 GB)"}}""", "disk-low"),
            ("""{"watcher_keep": {"computed": "the next morning", "problems": ["ratings.json is 80 h old (at most 72 h)"]}}""", "deletion-refused:"),
            ("""{"errors": ["the bridge's data cannot be read (x) - nothing done in this run"]}""", "error:"),
        };
        foreach (var (extra, key) in cases)
        {
            var got = Issues.Of(QuietWith(O(extra).Select(kv => (kv.Key, kv.Value)).ToArray()));
            Check($"issues: {key} counts ({(got.Count > 0 ? Text.Cut(got[0].What, 70) : "nothing")})", got.Count == 1 && got[0].Key.StartsWith(key, StringComparison.Ordinal) && got[0].Did.Length > 0);
        }
        var sentMails = new List<(string Subject, string Body)>();
        var realSend = Hooks.SendMail;
        Hooks.SendMail = (_, subject, body) =>
        {
            sentMails.Add((subject, body));
            return null;
        };
        var icfg = Cfg();
        icfg.StateDir = Path.Combine(root, "issues-state");
        Directory.CreateDirectory(icfg.StateDir);
        var withKey = """{ "schema": 1, "sync": { "mail": { "dashboardUrl": "https://dash.example/" } } }""";
        Check("issues: the dashboard's address is sync.mail.dashboardUrl, the one the mails point to",
              SyncConfig.From(Settings.Parse(withKey, Part.Sync, _ => null, _ => throw new FileNotFoundException()), _ => null).DashboardUrl == "https://dash.example/");
        icfg.DashboardUrl = "https://dash.example/";
        var ic = new FakeClock(RunTests.T0);
        var probs = Issues.Of(QuietWith(("api_stop", "Steam's Web API answered HTTP 429 to QueryFiles"), ("report_path", "/x/r.json")));
        var out1 = Issues.Mail(icfg, C(ic), probs, QuietWith(("report_path", "/x/r.json")), Hooks.SendMail, Log.Line);
        Check("issues: a problem is mailed - what went wrong, what the sync did, the dashboard, the report",
              out1.StartsWith("sent", StringComparison.Ordinal) && sentMails.Count == 1 && sentMails[0].Body.Contains("What went wrong: Steam's Web API pushed back")
              && sentMails[0].Body.Contains("What the sync did: Every Web API step stopped") && sentMails[0].Body.Contains("The dashboard: https://dash.example/")
              && sentMails[0].Body.Contains("/x/r.txt") && sentMails[0].Subject.Contains("pushed back"));
        ic.T += 3600;
        var out2 = Issues.Mail(icfg, C(ic), probs, Quiet, Hooks.SendMail, Log.Line);
        Check("issues: the same problem an hour later is not mailed again", out2.StartsWith("NOT sent: every problem", StringComparison.Ordinal) && sentMails.Count == 1);
        var probs2 = probs.Concat(Issues.Of(QuietWith(("disk", O("{\"low\": \"50 GB free\"}"))))).ToList();
        var out3 = Issues.Mail(icfg, C(ic), probs2, Quiet, Hooks.SendMail, Log.Line);
        Check("issues: a new problem is mailed, the ongoing one named as still going on", out3.StartsWith("sent", StringComparison.Ordinal) && sentMails.Count == 2
              && sentMails[1].Body.Contains("disk is running low") && sentMails[1].Body.Contains("Still going on, mailed already in the last day: Steam's Web API"));
        ic.T += 21 * 3600;
        Issues.Mail(icfg, C(ic), probs, Quiet, Hooks.SendMail, Log.Line);
        Check("issues: a problem that persists is mailed again once a day", sentMails.Count == 3);
        Check("issues: no problem, no mail", Issues.Mail(icfg, C(ic), [], Quiet, Hooks.SendMail, Log.Line).StartsWith("NOT sent: no problem", StringComparison.Ordinal) && sentMails.Count == 3);
        var crash = Issues.MailCrash(icfg, C(ic), new TestFailure("boom"), false, Hooks.SendMail, Log.Line);
        Check("issues: a run that crashed is mailed from the service", crash.StartsWith("sent", StringComparison.Ordinal) && sentMails[^1].Body.Contains("The run failed: TestFailure('boom')"));
        Check("issues: ...once a day too", Issues.MailCrash(icfg, C(ic), new TestFailure("boom 2"), false, Hooks.SendMail, Log.Line).StartsWith("NOT sent: every problem", StringComparison.Ordinal));
        Hooks.SendMail = realSend;
    }

    // ---- the problem mail's SMTP client, against a stand-in relay on localhost
    internal static void SmtpClient()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var lines = new List<string>();
        var server = new Thread(() =>
        {
            try
            {
                using var c = listener.AcceptTcpClient();
                var s = c.GetStream();
                var r = new StreamReader(s, Encoding.UTF8);
                void Say(string t) => s.Write(Encoding.UTF8.GetBytes(t + "\r\n"));
                Say("220 relay.test ESMTP");
                var data = false;
                while (r.ReadLine() is { } line)
                {
                    lines.Add(line);
                    if (data)
                    {
                        if (line == ".")
                        {
                            data = false;
                            Say("250 queued");
                        }
                        continue;
                    }
                    if (line.StartsWith("EHLO", StringComparison.Ordinal))
                        Say("250-relay.test\r\n250 8BITMIME");
                    else if (line == "DATA")
                    {
                        data = true;
                        Say("354 go ahead");
                    }
                    else if (line == "QUIT")
                    {
                        Say("221 bye");
                        break;
                    }
                    else
                        Say("250 ok");
                }
            }
            catch (Exception e) when (e is IOException or SocketException)
            {
            }
        }) { IsBackground = true };
        server.Start();
        var cfg = Cfg();
        (cfg.SmtpHost, cfg.SmtpPort, cfg.MailFrom, cfg.MailTo) = ("127.0.0.1", port, "The Sync <sync@example.com>", ["a@example.com", "b@example.com"]);
        var why = Smtp.Send(cfg, "a subject é", "line one\n.a line that starts with a dot\nlast");
        server.Join(5000);
        listener.Stop();
        Check($"mail: sent through an SMTP relay - the sender's address, every recipient, the body dot-stuffed ({why ?? "sent"})",
              why is null && lines.Contains("MAIL FROM:<sync@example.com>") && lines.Contains("RCPT TO:<a@example.com>") && lines.Contains("RCPT TO:<b@example.com>")
              && lines.Contains("Subject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("a subject é")) + "?=") && lines.Contains("..a line that starts with a dot") && lines.Contains("last") && !lines.Contains("STARTTLS"));
        Check("mail: no relay configured, no mail", Smtp.Send(Cfg(), "s", "b") == "no SMTP host, sender or recipient configured");
        Check("mail: a plain subject stays as it is, and no line break makes a header of its own",
              Smtp.HeaderText("Over Strike 2: a problem") == "Over Strike 2: a problem" && Smtp.HeaderText("a\r\nBcc: x") == "a  Bcc: x");
        var longName = string.Concat(Enumerable.Repeat("Der gr\u00fcne Server \U0001F600 ", 12));
        var folded = Smtp.HeaderText(longName).Split("\r\n ");
        var words = folded.Select(w => w[10..^2]).Select(Convert.FromBase64String).ToList();
        Check($"mail: a long subject beyond ASCII is encoded words that fit their lines, each whole characters, and read back the same ({folded.Length} words)",
              folded.Length > 1 && folded.All(w => w.StartsWith("=?utf-8?B?", StringComparison.Ordinal) && w.EndsWith("?=", StringComparison.Ordinal) && w.Length + "Subject: ".Length <= 76)
              && words.All(b => !Encoding.UTF8.GetString(b).Contains('\uFFFD')) && string.Concat(words.Select(Encoding.UTF8.GetString)) == longName);
    }
}
