// The sync's self-test: the service: its clock, its command line, a SIGTERM.
using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The service: a run's deadline and when one may start, the command line, a SIGTERM.</summary>
static class ServiceTests
{
    // ---- a run lasts sync.runHours, cut short before the restarts; the service starts one whenever nobody plays and work waits
    internal static void Clock()
    {
        var fcd = new FakeClock(RunTests.T0 + 3 * 86400);
        var c = C(fcd);
        var day0 = c.AtTime(new TimeOfDay(0, 0));
        string Hm(double ts) => c.Hm(ts);
        fcd.T = day0 + 12 * 3600;
        Check("run: at noon a run lasts 3 h", Hm(W.Run(Cfg(), fcd).Deadline) == "15:00");
        fcd.T = day0 + 5 * 3600;
        Check("run: at 05:00 it ends at 06:45, before the 07:00 restart", Hm(W.Run(Cfg(), fcd).Deadline) == "06:45");
        fcd.T = day0 + 2 * 3600;
        Check("run: at 02:00 it runs its 3 h", Hm(W.Run(Cfg(), fcd).Deadline) == "05:00");
        var cfgs = Cfg();
        Check("service: no start in the daily restart's stretch (06:45-07:10)", Service.StartBlocked(cfgs, c, day0 + 6.9 * 3600) is not null && Service.StartBlocked(cfgs, c, day0 + 7.2 * 3600) is null);
        Check("service: a run may start in the night, at 03:10 and at 03:30",
              Service.StartBlocked(cfgs, c, day0 + (3 * 60 + 10) * 60) is null && Service.StartBlocked(cfgs, c, day0 + 3.5 * 3600) is null);
        var at0630 = Service.StartBlocked(cfgs, c, day0 + 6.5 * 3600);
        Check($"service: no run with under 20 minutes before the daily restart's cut: at 06:30 it would end at 06:45 ({at0630 ?? "None"}) - at 06:20 one starts",
              (at0630 ?? "").Contains("daily restart") && Service.StartBlocked(cfgs, c, day0 + (6 * 60 + 20) * 60) is null);
        fcd.T = day0 + 12 * 3600;
        Check("service: work waits while maps are queued, unless Steam stopped today's downloads; or when the API steps are due",
              Service.WorkWaits(cfgs, c, new JsonObject { ["items"] = O("{\"1\": {\"status\": \"queued\"}}"), ["last_api"] = fcd.T })
              && !Service.WorkWaits(cfgs, c, new JsonObject { ["items"] = O("{\"1\": {\"status\": \"queued\"}}"), ["last_api"] = fcd.T, ["downloads"] = new JsonObject { [c.Day()] = O("{\"stop\": \"x\"}") } })
              && Service.WorkWaits(cfgs, c, new JsonObject { ["items"] = new JsonObject(), ["last_api"] = fcd.T - 5 * 3600 })
              && !Service.WorkWaits(cfgs, c, new JsonObject { ["items"] = new JsonObject(), ["last_api"] = fcd.T }));
    }

    static (int Code, string Said) Quiet(Func<int> run)
    {
        var (o, e) = (Console.Out, Console.Error);
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(sw);
        try
        {
            return (run(), sw.ToString());
        }
        finally
        {
            Console.SetOut(o);
            Console.SetError(e);
        }
    }

    // ---- the command line
    internal static void Cli(string root)
    {
        Check("cli: an argument it does not know exits at once (2), so does a command", Quiet(() => Program.Main(["serve", "--nonsense"])).Code == 2
              && Quiet(() => Program.Main(["nonsense"])).Code == 2 && Quiet(() => Program.Main(["once", "{not json"])).Code == 2);
        string[][] gone = [["serve", "--dry-run"], ["once", "--dry-run"], ["serve", "--state", root], ["once", "--work", root], ["serve", "--test-port", "27400"], ["once", "--no-handover"]];
        Check("cli: --dry-run, --state, --work, --test-port and --no-handover are gone: each exits at once (2), so an old command line never starts a run in their place",
              gone.All(a => Quiet(() => Program.Main(a)).Code == 2));
        var (code, said) = Quiet(() => Program.Main(["version"]));
        Check("cli: version", code == 0 && said.StartsWith("os2-sync ", StringComparison.Ordinal));
        var file = Path.Combine(root, "cli.jsonc");
        File.WriteAllText(file, "{ \"schema\": 7 }");
        (code, said) = Quiet(() => Program.Main(["once", "--settings", file]));
        Check("cli: a settings file this program cannot use: nothing done, and it says so", code == Settings.RefusedExit && said.Contains("cannot be used"));
        foreach (var (what, text, why) in new[]
        {
            ("missing", "{ \"schema\": 1 }", "hud.addon: missing"),
            ("empty", "{ \"schema\": 1, \"hud\": { \"addon\": \"\" } }", "hud.addon: empty"),
            ("not digits", "{ \"schema\": 1, \"hud\": { \"addon\": \"abc\" } }", "hud.addon: not digits"),
        })
        {
            File.WriteAllText(file, text);
            foreach (var command in new[] { "serve", "once" })
            {
                (code, said) = Quiet(() => Program.Main([command, "--settings", file]));
                Check($"cli: {command} with hud.addon {what} refuses as os2-server does: one ALERT line naming the key and the fix, exit {Settings.RefusedExit}",
                      code == Settings.RefusedExit && said.Trim().Split('\n').Length == 1 && said.Contains("ALERT") && said.Contains(why) && said.Contains("required: the Workshop id") && said.Contains("fix the file"));
            }
        }
        (code, said) = Quiet(() => Program.Main(["once", "--settings", Path.Combine(root, "no-such-file.jsonc")]));
        Check("cli: no settings file at all: the same", code == Settings.RefusedExit && said.Contains("no settings file at") && said.Contains("hud.addon: missing"));
        File.WriteAllText(file, "{ \"schema\": 1 }");
        (code, said) = Quiet(() => Program.Main(["doctor", "--settings", file]));
        Check("cli: doctor still works with no hud.addon, and says so", code == 1 && said.Contains("NOT USABLE: hud.addon: missing") && said.Contains("Web API key:"));
        (code, said) = Quiet(() => Program.Main(["version", "--settings", file]));
        Check("cli: version too", code == 0 && said.StartsWith("os2-sync ", StringComparison.Ordinal) && said.Contains("settings: NOT USABLE - hud.addon: missing"));
        File.WriteAllText(file, "// a comment\n{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" }, \"sync\": { \"webApiKey\": \"\" }, \"paths\": { \"syncState\": \"/cli-state\" }, }");
        (code, said) = Quiet(() => Program.Main(["doctor", "--settings", file]));
        Check("cli: doctor says what it sees, the key without its value", code == 0 && said.Contains("Web API key: absent") && said.Contains("path state: /cli-state ") && said.Contains("the HUD item: 3823993566"));
        (code, said) = Quiet(() => Program.Main(["version", "--settings", file]));
        Check("cli: version with a good file: its one line", code == 0 && said.StartsWith("os2-sync ", StringComparison.Ordinal) && !said.Contains("NOT USABLE"));
        var (savedContent, savedPath) = (Environment.GetEnvironmentVariable(Settings.ContentVariable), Environment.GetEnvironmentVariable(Settings.PathVariable));
        Environment.SetEnvironmentVariable(Settings.PathVariable, null);
        Environment.SetEnvironmentVariable(Settings.ContentVariable, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" }, \"sync\": { \"webApiKey\": \"\" } }");
        (code, said) = Quiet(() => Program.Main(["doctor"]));
        Check($"cli: {Settings.ContentVariable} is read like the file, and doctor names it", code == 0 && said.Contains($"settings: {Settings.ContentSource}") && said.Contains("the HUD item: 3823993566"));
        (code, said) = Quiet(() => Program.Main(["doctor", "--settings", file]));
        Check("cli: --settings wins over it, for a test", code == 0 && said.Contains($"settings: {file}"));
        Environment.SetEnvironmentVariable(Settings.ContentVariable, "{ \"schema\": 1 }");
        (code, said) = Quiet(() => Program.Main(["once"]));
        Check("cli: a value with no hud.addon refuses the same way, and the ALERT names the variable", code == Settings.RefusedExit && said.Contains($"{Settings.ContentSource}: hud.addon: missing"));
        Environment.SetEnvironmentVariable(Settings.ContentVariable, savedContent);
        Environment.SetEnvironmentVariable(Settings.PathVariable, savedPath);
    }

    // ---- a SIGTERM: the next wait ends the run with its clean-up, never as an error
    internal static void Stopping()
    {
        var stoppable = new StoppableClock();
        stoppable.Stop();
        var threw = false;
        try
        {
            new SyncClock(stoppable, Paris).Sleep(60);
        }
        catch (StopRequested)
        {
            threw = true;
        }
        Check("service: a SIGTERM ends the next wait at once, as StopRequested (the run's clean-up runs, nothing is mailed as a crash)", threw);
    }
}
