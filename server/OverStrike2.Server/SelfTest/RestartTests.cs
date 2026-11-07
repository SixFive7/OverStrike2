using static OverStrike2.Server.SelfTest.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>A CS2 update or a deployed new version of our own parts restarts the engine at the end of the current match, players on or
/// not, and at once with nobody on; the bridge is told when it becomes due (POST /restart-notice, "match-end"), at the match end (a
/// countdown, "now") and after the restart what changed (POST /updated). The daily restart still waits for an empty server, and tells the
/// bridge only when it goes.</summary>
static class RestartTests
{
    static DateTimeOffset At(int d, int h, int m, int s) => Scenario.At(2026, 10, d, h, m, s);
    static string R => Scenario.Root;
    static string ServerFrom => Path.Combine(R, "os2-server-src");
    static string SettingsFile => Path.Combine(R, "settings.jsonc");
    static string PluginDll => Path.Combine(R, "plugins", "OverStrike2", "OverStrike2.dll");

    /// <summary>Our parts on disk as a deploy leaves them.</summary>
    static void Parts(string plugin = "plugin v1")
    {
        Put(PluginDll, plugin);
        Put(Path.Combine(R, "plugins", "OverStrike2", "settings-of-its-own.json"), "{}");
        Put(ServerFrom, "os2-server v1");
        Put(Path.Combine(R, "pre.sh"), "#!/usr/bin/env bash\n");
        Put(Path.Combine(R, "game", "csgo", "cfg", "gamemode_armsrace_server.cfg"), "mp_maxrounds 1\n");
        Put(SettingsFile, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\", \"check\": \"off\" } }");
    }

    static Installed Now(long at = 0) =>
        new(Fingerprint.Compute(Scenario.Template.Paths, ServerFrom, SettingsFile, _ => null), at, ServerFrom);

    static List<string> Notices(Scenario s) => s.Bridge.Bodies("restart-notice");

    public static void Run()
    {
        Scenario.Paths(p => p with { BridgeData = Dir("bridge-empty") });   // no queues: job 9 stays out of these
        Fingerprints();
        Updates();
        NewVersions();
        Daily();
    }

    static void Fingerprints()
    {
        Parts();
        var a = Now().Parts;
        var b = Now().Parts;
        Check("version: the same parts give the same version, every time", a == b && a.Total.Length == 16);
        File.SetLastWriteTimeUtc(PluginDll, DateTime.UtcNow.AddHours(-5));
        Check("version: a file touched but not changed is the same version (its look on disk is not)",
              Now().Parts == a && Fingerprint.Signature(Scenario.Template.Paths, ServerFrom, SettingsFile) is var sig && sig.Length == 64);
        Put(PluginDll, "plugin v2");
        var c = Now().Parts;
        Check($"version: a changed plugin file is a new version, and says which part changed ({string.Join(", ", c.Changed(a))})",
              c.Total != a.Total && c.Changed(a).SequenceEqual(["plugin"]));
        Parts();
        Put(SettingsFile, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\", \"check\": \"enforce\", \"tab\": \"menu\" }, \"sync\": { \"tags\": [\"Armsrace\"] } }");
        Check("version: a setting only the bridge or the sync reads changes nothing here", Now().Parts == a);
        Put(SettingsFile, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993567\" } }");
        Check("version: a setting the engine side reads (hud.addon) is a new version", Now().Parts.Changed(a).SequenceEqual(["settings"]));
        Put(SettingsFile, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" }, \"plugin\": { \"enabled\": false } }");
        Check("version: plugin.enabled is one too (the install step reads it)", Now().Parts.Changed(a).SequenceEqual(["settings"]));
        Put(SettingsFile, "{ \"schema\": 9 }");
        Check("version: a settings file this program cannot use is no version at all: nothing is decided on it",
              Throws<InvalidDataException>(() => Now()));
        Put(SettingsFile, "{ \"schema\": 1, \"hud\": { \"check\": \"off\" } }");
        Check("version: nor is one that lost hud.addon: a running watcher restarts nothing for it", Throws<InvalidDataException>(() => Now()));
        Parts();
        File.Delete(Path.Combine(R, "game", "csgo", "cfg", "gamemode_armsrace_server.cfg"));
        Check("version: a part that is not there is 'none', the same way both times", Now().Parts.Cfgs == Fingerprint.None && Now().Parts == Now().Parts);
        var dir = Path.Combine(R, "update-watch-v");
        new Installed(a, 42, ServerFrom).Write(Path.Combine(dir, "installed.json"));
        Check("version: installed.json round-trips", Installed.Read(Path.Combine(dir, "installed.json")) is { At: 42 } i && i.Parts == a && i.ServerFrom == ServerFrom);
        Parts();
    }

    public static bool Throws<TE>(Action a) where TE : Exception
    {
        try
        {
            a();
            return false;
        }
        catch (TE)
        {
            return true;
        }
    }

    static void Updates()
    {
        var order = new List<string>();
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, body) => { order.Add($"{what}: {body}"); return what == "quitting" ? "ok: going down" : "ok"; } };
        Scenario Recorded(DateTimeOffset start, (string, int?, string?)? verdict = null, Installed? installed = null)
        {
            var s = Scenario.Make(start, verdict, installed: installed);
            s.Eng.OnRcon = (c, r) =>
            {
                if (c == "quit")
                    order.Add("quit");
                return s.Eng.BaseRcon(c, r);
            };
            return s;
        }

        // U1. a CS2 update with players on: announced at once, counted down at the match end, then the quit
        var s = Recorded(At(4, 15, 0, 0), ("stale", 14189, null));
        (s.Eng.Humans, s.Eng.Hud) = (3, true);
        s.Step(120);
        Check($"U1: a CS2 update with players on: the bridge hears at once that the server restarts at the end of the match ({Show(Notices(s).Select(n => n.TrimEnd()))})",
              Notices(s).FirstOrDefault() == "gg-restart\t1\tgame-update\tmatch-end\t10\n" && !s.Has("quit"));
        Check("U1: ... and the bridge shows it: no line of the watcher's own on the HUD or in chat", s.Eng.Notices.Count == 0 && s.Eng.Chat.Count == 0);
        s.Eng.Write("L 10/04/2026 - 15:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");
        s.Step(5);
        Check($"U1: at the end of the match the bridge counts down {Watch.RestartCountdown} s, and the quit waits for it",
              Notices(s).Contains($"gg-restart\t1\tgame-update\tnow\t{Watch.RestartCountdown}\n") && !s.Has("quit") && s.W.CountdownUntil is not null);
        s.Step(8);
        Check($"U1: ... then the quit notice and the quit ({Show(order.Select(o => o.Split(':')[0].Replace("\t", " ")))})",
              order.Count >= 4 && order[^2] == "quitting: gg-quit\t1\tupdate\n" && order[^1] == "quit" && s.W.State.Pending?.Kind == "update");

        // U2. a CS2 update with nobody on: at once
        order.Clear();
        s = Recorded(At(4, 15, 0, 0), ("stale", 14189, null));
        s.Step(30);
        Check($"U2: a CS2 update with nobody on: at once - the bridge told, the quit notice, the quit ({Show(order.Select(o => o.Replace("\t", " ").TrimEnd()))})",
              order.SequenceEqual(["restart-notice: gg-restart\t1\tgame-update\tnow\t0\n", "quitting: gg-quit\t1\tupdate\n", "quit"]));

        // U3. the announcement again every 5 minutes while it waits (a bridge swapped in the meantime knows it too)
        s = Recorded(At(4, 15, 0, 0), ("stale", 14189, null));
        s.Eng.Humans = 3;
        s.Step(700);
        var matchEnd = Notices(s).Count(n => n.Contains("\tmatch-end\t"));
        Check($"U3: the match-end notice again every {Watch.AnnounceRefresh:0} s while the restart waits ({matchEnd} in 700 s)", matchEnd == 3);

        // U4. no longer due (Valve now calls the build current): the bridge hears that it is off
        s = Recorded(At(4, 15, 0, 0), ("stale", 14189, null));
        s.Eng.Humans = 3;
        s.Step(60);
        s.Valve.Answer = _ => ("current", null, null);
        s.W.VerdictAt = 0;
        s.Step(5);
        Check($"U4: a restart that is no longer due is taken back ({Show(Notices(s).Select(n => n.TrimEnd().Replace("\t", " ")))})",
              Notices(s)[^1] == "gg-restart\t1\tcancel\tgame-update\n" && s.W.Due is null && !s.Has("quit"));

        // U5. the bridge does not take the quit notice when the countdown is over: no quit, and it waits for the next match end again
        var takeQuit = false;
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, body) => { order.Add($"{what}: {body}"); return what == "quitting" ? (takeQuit ? "ok: going down" : null) : "ok"; } };
        s = Recorded(At(4, 15, 0, 0), ("stale", 14189, null));
        s.Eng.Humans = 3;
        s.Step(60);
        s.Eng.Write("L 10/04/2026 - 15:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");
        s.Step(40);
        Check("U5: the countdown is over but the bridge does not take the quit notice: no quit, and the restart waits for the next match end again",
              !s.Has("quit") && s.W.Due is { Kind: "update" } && s.W.CountdownUntil is null && Notices(s)[^1] == "gg-restart\t1\tgame-update\tmatch-end\t10\n");
        takeQuit = true;
        s.Step(70);
        s.Eng.Write("L 10/04/2026 - 15:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");
        s.Step(15);
        Check("U5: ... the next match end counts down again, and then it goes", s.Has("quit") && Notices(s).Count(n => n.Contains("\tnow\t")) == 2);
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, body) => { order.Add($"{what}: {body}"); return what == "quitting" ? "ok: going down" : "ok"; } };

        // U6. after the restart: once the engine answers, the bridge hears what changed (the CS2 build)
        var st = new WatchState { Restarts = [new Restart((long)Watch.Seconds(At(4, 15, 0, 0)) - 60, "update")],
                                  Pending = new Pending("update", (long)Watch.Seconds(At(4, 15, 0, 0)) - 60, "1.41.8.7", 14188, null, null) };
        s = Scenario.Make(At(4, 15, 0, 0));
        s.Carry(st);
        s.Step(5);
        Check($"U6: after an update restart, once the engine answers: the bridge hears the build it went from and to ({Show(s.Bridge.Bodies("updated").Select(b => b.TrimEnd().Replace("\t", " ")))})",
              s.Bridge.Bodies("updated").SequenceEqual(["gg-updated\t1\tgame-update\t1.41.8.7\t1.41.8.8\n"]) && s.W.State.UpdateMisses == 0);
        var failing = 0;
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, _) => what == "updated" && failing++ < 2 ? null : "ok" };
        s = Scenario.Make(At(4, 15, 0, 0));
        s.Carry(st);
        s.Step(150);
        Check($"U6: ... a bridge that does not take it hears it again a minute later ({s.Bridge.Bodies("updated").Count} tries)", s.Bridge.Bodies("updated").Count == 3);
        Scenario.NewBridge = () => null;
    }

    static void NewVersions()
    {
        var order = new List<string>();
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, body) => { order.Add($"{what}: {body}"); return what == "quitting" ? "ok: going down" : "ok"; } };
        Parts();
        var v1 = Now(1);

        // V1. a new version with players on: due once it is still, announced, counted down at the match end, then the quit
        var s = Scenario.Make(At(4, 15, 0, 0), installed: v1);
        (s.Eng.Humans, s.Eng.Hud) = (3, true);
        s.Step(20);
        Check("V1: the version on disk is the installed one: nothing due", s.W.Due is null && s.W.OnDisk?.Total == v1.Fingerprint);
        Put(PluginDll, "plugin v2");                         // a deploy writes the new plugin
        var v2 = Now(2);
        s.Step(100);
        Check($"V1: a new version on disk is not due while it may still be being written ({Show(s.Lines.Where(l => l.Contains("being written")).Take(1))})",
              s.W.Due is null && !s.Has("quit"));
        s.Step(70);
        Check($"V1: ... once it has been still for {Watch.VersionSettle:0} s, a restart is due for it",
              s.W.Due is { Kind: "version" } && s.W.VersionDue?.Total == v2.Fingerprint && Notices(s).FirstOrDefault() == "gg-restart\t1\tour-update\tmatch-end\t10\n");
        s.Eng.Write("L 10/04/2026 - 15:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");
        s.Step(15);
        Check($"V1: with players on: at the end of the match, counted down, then the quit ({Show(order.TakeLast(3).Select(o => o.Split(':')[0]))})",
              Notices(s).Contains($"gg-restart\t1\tour-update\tnow\t{Watch.RestartCountdown}\n") && order[^1] == "quitting: gg-quit\t1\tversion\n" && s.Has("quit"));
        Check("V1: ... recorded with the version the engine ran and the one on disk", s.W.State.Pending is { Kind: "version" } p && p.Installed == v1.Fingerprint && p.Source == v2.Fingerprint);
        var st = s.W.State.Copy();

        // V2. after it: install put the new version in place - the bridge hears what changed; nothing more is due
        s = Scenario.Make(s.Clock.Now.AddSeconds(40), installed: v2);
        s.Carry(st);
        s.Step(70);
        Check($"V2: after the version restart, once the engine answers: the bridge hears our parts' version it went from and to ({Show(s.Bridge.Bodies("updated").Select(b => b.TrimEnd().Replace("\t", " ")))})",
              s.Bridge.Bodies("updated").SequenceEqual([$"gg-updated\t1\tour-update\t{v1.Fingerprint}\t{v2.Fingerprint}\n"]) && s.Lines.Any(l => l.StartsWith("new version installed")));
        Check("V2: ... and no restart is due any more", s.W.Due is null && !s.Has("quit"));

        // V3. a version restart that did not install the new version: no restart for it again (no loop); a newer one re-arms
        s = Scenario.Make(s.Clock.Now.AddSeconds(40), installed: v1);
        s.Carry(st);
        s.Eng.Humans = 3;                                    // so a restart that becomes due waits for a match end, and can be seen due
        s.Step(400);
        Check($"V3: a version restart that left the old version in place is not repeated for that version ({s.W.State.VersionTrippedFor})",
              s.W.State.VersionTrippedFor == v2.Fingerprint && !s.Has("quit") && s.W.Due is null);
        Put(PluginDll, "plugin v3");
        s.Step(200);
        Check("V3: ... a newer one is due again", s.W.Due is { Kind: "version" });
        Parts();

        // V4. with nobody on: at once
        order.Clear();
        s = Scenario.Make(At(4, 15, 0, 0), installed: v1);
        s.Eng.OnRcon = (c, r) =>
        {
            if (c == "quit")
                order.Add("quit");
            return s.Eng.BaseRcon(c, r);
        };
        s.Step(20);
        Put(PluginDll, "plugin v2");
        s.Step(200);
        Check($"V4: a new version with nobody on: at once ({Show(order.Select(o => o.Replace("\t", " ").TrimEnd()))})",
              order.SequenceEqual(["restart-notice: gg-restart\t1\tour-update\tnow\t0\n", "quitting: gg-quit\t1\tversion\n", "quit"]));
        Parts();

        // V5. the version on disk goes back to the installed one before the restart: no longer due, and the bridge hears that
        s = Scenario.Make(At(4, 15, 0, 0), installed: v1);
        s.Eng.Humans = 3;
        s.Step(20);
        Put(PluginDll, "plugin v2");
        s.Step(200);
        var due = s.W.Due?.Kind;
        Parts();
        s.Step(70);
        Check($"V5: back to the installed version before the match ended: no longer due, the notice taken back (due was {due})",
              due == "version" && s.W.Due is null && Notices(s)[^1] == "gg-restart\t1\tcancel\tour-update\n");

        // V6. restarts.newVersions false: never
        s = Scenario.Make(At(4, 15, 0, 0), installed: v1, tune: c => c.NewVersions = false);
        s.Step(20);
        Put(PluginDll, "plugin v2");
        s.Step(300);
        Check("V6: restarts.newVersions false: a new version restarts nothing", s.W.Due is null && !s.Has("quit"));
        Parts();

        // V8. the next deploy begins while a version restart waits for the match end: no quit into a half-written deploy, no false alarm after it
        const string gameOver = "L 10/04/2026 - 15:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min";
        s = Scenario.Make(At(4, 15, 0, 0), installed: v1);
        (s.Eng.Humans, s.Eng.Hud) = (3, true);
        s.Step(20);
        Put(PluginDll, "plugin v2");
        s.Step(200);
        var dueBefore = s.W.Due?.Kind;
        Put(PluginDll, "plugin v3, half of it");             // the next deploy is writing
        s.Step(70);                                          // the watcher has had its look
        Check($"V8: a deploy begins while a version restart waits: it is no longer due until what is on disk is still, and the bridge hears that (due was {dueBefore})",
              dueBefore == "version" && s.W.VersionDue is null && s.W.Due is null && Notices(s)[^1] == "gg-restart\t1\tcancel\tour-update\n");
        s.Eng.Write(gameOver);
        s.Step(15);
        Check("V8: ... so a match end in that window quits nothing", !s.Has("quit") && s.W.CountdownUntil is null);
        Put(PluginDll, "plugin v3");                         // the deploy is done
        var v3 = Now(3);
        s.Step(200);
        Check("V8: ... once it is still the restart is due again, for what is on disk now", s.W.Due is { Kind: "version" } && s.W.VersionDue?.Total == v3.Fingerprint);
        s.Eng.Write(gameOver);
        s.Step(15);
        Check("V8: ... and the next match end takes it, recorded with that version, so the check after it raises no false alarm", s.Has("quit") && s.W.State.Pending is { Kind: "version" } p8 && p8.Source == v3.Fingerprint);
        Parts();

        // V9. the deploy begins right after the watcher's look, and the match ends before the next one: the moment of the quit looks again
        s = Scenario.Make(At(4, 15, 0, 0), installed: v1);
        (s.Eng.Humans, s.Eng.Hud) = (3, true);
        s.Step(20);
        Put(PluginDll, "plugin v2");
        s.Step(200);
        var seen = s.W.Due?.Kind;
        Put(PluginDll, "plugin v3, half of it");
        s.Eng.Write(gameOver);
        s.Step(15);
        Check($"V9: a match end right after a deploy began, before the watcher's minute comes round: no countdown, no quit (due was {seen})",
              seen == "version" && !s.Has("quit") && s.W.CountdownUntil is null);
        Parts();

        // V10. another kind of restart waits for a deploy too, and goes when it is still
        s = Scenario.Make(At(4, 15, 0, 0), ("stale", 14189, null), installed: v1);
        s.Eng.Humans = 3;
        s.Step(20);
        Put(PluginDll, "plugin v2");
        s.Eng.Humans = 0;
        s.Step(30);
        Check($"V10: a CS2 update restart with nobody on, a deploy being written: it waits ({Show(s.Lines.Where(l => l.Contains("being written")).Take(1))})",
              s.W.Due is { Kind: "update" } && !s.Has("quit"));
        s.Step(200);
        Check("V10: ... and goes once what is on disk is still", s.Has("quit"));
        Parts();

        // V11. parts that cannot be read are no deploy: no decision on a new version, and no other restart held back for them
        s = Scenario.Make(At(4, 15, 0, 0), ("stale", 14189, null), installed: v1);
        s.Eng.Humans = 3;
        s.Step(20);
        Put(SettingsFile, "{ \"schema\": 9 }");
        s.Eng.Humans = 0;
        s.Step(10);
        Check("V11: a settings file that turned unusable is no deploy: the CS2 update restart goes", s.Has("quit") && s.W.VersionDue is null);
        Parts();

        // V7. no record of what install put in place (an older pre.sh): no version is detected, and the start says so
        s = Scenario.Make(At(4, 15, 0, 0));
        s.W.Start();
        s.Step(20);
        Check("V7: no record of what install put in place: no new version detected, and the start says so",
              s.W.InstalledNow is null && s.Lines.Any(l => l.Contains("no record of what install put in place")));

        // V8. a version restart counts against the same caps as every planned restart
        s = Scenario.Make(At(4, 15, 0, 0), installed: v1);
        s.W.State.Restarts = [.. Enumerable.Range(0, 4).Select(i => new Restart((long)s.Clock.T - 1000 - i * 1000, "update"))];
        s.Step(20);
        Put(PluginDll, "plugin v2");
        s.Step(300);
        Check("V8: four planned restarts in 24 h already: a new version waits (the cap)", s.W.Due is { Kind: "version" } && !s.Has("quit"));
        Parts();
        Scenario.NewBridge = () => null;
    }

    static void Daily()
    {
        var order = new List<string>();
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, body) => { order.Add($"{what}: {body}"); return what == "quitting" ? "ok: going down" : "ok"; } };
        // D1. the daily restart with players on: nothing for the bridge or the players until the server is empty, then at once
        var s = Scenario.Make(At(4, 6, 59, 0));
        s.Eng.Humans = 3;
        s.Step(300);
        s.Eng.Write("L 10/04/2026 - 07:05:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");
        s.Step(60);
        Check("D1: the daily restart with players on: no notice to the bridge, no line, no quit at the match end", Notices(s).Count == 0 && s.Eng.Notices.Count == 0 && !s.Has("quit"));
        s.Eng.Humans = 0;
        s.Step(30);
        Check("D1: ... the moment the server is empty: the bridge told it goes now, then the quit",
              Notices(s).SequenceEqual(["gg-restart\t1\tdaily\tnow\t0\n"]) && s.Has("quit") && s.W.State.DoneDay == "2026-10-04");

        // D2. a planned restart of another kind inside the daily window counts as the day's: no second restart that morning
        s = Scenario.Make(At(4, 7, 10, 0), ("stale", 14189, null));
        s.Eng.Humans = 3;
        s.Step(20);
        s.Eng.Write("L 10/04/2026 - 07:05:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");
        s.Step(15);
        Check("D2: an update restart at a match end inside the daily window is the day's restart too",
              s.Has("quit") && s.W.State.Pending?.Kind == "update" && s.W.State.DoneDay == "2026-10-04");
        Scenario.NewBridge = () => null;
    }
}
