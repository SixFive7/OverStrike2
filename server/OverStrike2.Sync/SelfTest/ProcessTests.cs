// The sync's self-test: the processes: the test engine, SteamCMD, the test tree.
using System.Diagnostics;
using OverStrike2.Core;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The processes, with real ones where Linux has them: the test engine's stop and start, SteamCMD's driver, the test tree, and a
/// real test load's first steps.</summary>
static class ProcessTests
{
    // ---- the test engine's stop, with real processes: a wrapper that dies at the SIGTERM over an engine that does not
    internal static void Engines()
    {
        if (!OperatingSystem.IsLinux())
        {
            foreach (var name in new[] { "engine stop: the group is up before the stop", "engine stop: a frozen engine goes with its wrapper, by SIGKILL on the group",
                                         "engine stop under a SIGTERM: the signals still go, and no frozen engine stays", "engine stop under a SIGTERM: ... and the stop goes on up",
                                         "engine start: nothing answering is a refusal (not run)", "engine start: an engine still running from the test tree is killed first",
                                         "engine start: its own attempt is gone again" })
                Linux(name, () => false);
            return;
        }
        var stopRoot = Dir("engine-stop");
        var real = new SyncClock(SystemClock.Instance, Paris);
        var te = new TestEngine(Cfg(), stopRoot, stopRoot, real, Log.Line) { QuitWait = 0, StopWait = 1 };
        var wrapper = Native.StartSession(["bash", "-c", "bash -c 'trap \"\" TERM; exec sleep 300' & wait"], stopRoot, null, false);
        te.Adopt(wrapper);
        Thread.Sleep(1000);
        var stopPgid = wrapper.Id;
        Check("engine stop: the group is up before the stop", Native.GroupAlive(stopPgid));
        te.Stop();
        Check("engine stop: a frozen engine goes with its wrapper, by SIGKILL on the group", !Native.GroupAlive(stopPgid) && !te.HasProcess);
        var going = new TestEngine(Cfg(), stopRoot, stopRoot, new SyncClock(new EndingClock(RunTests.T0, RunTests.T0), Paris), Log.Line);   // every wait of this clock throws at once
        var frozen = Native.StartSession(["bash", "-c", "bash -c 'trap \"\" TERM; exec sleep 300' & wait"], stopRoot, null, false);
        going.Adopt(frozen);
        Thread.Sleep(1000);
        var frozenPgid = frozen.Id;
        var wentUp = false;
        try
        {
            going.Stop();
        }
        catch (StopRequested)
        {
            wentUp = true;
        }
        for (var i = 0; i < 30 && Native.GroupAlive(frozenPgid); i++)
            Thread.Sleep(100);
        Check("engine stop under a SIGTERM: the signals still go, and no frozen engine stays", !Native.GroupAlive(frozenPgid) && !going.HasProcess);
        Check("engine stop under a SIGTERM: ... and the stop goes on up", wentUp);
        var treeBin = Path.Combine(stopRoot, "farm", "game", "bin");
        Directory.CreateDirectory(treeBin);
        File.Copy("/bin/sleep", Path.Combine(treeBin, "cs2"));
        var stale = Process.Start(Path.Combine(treeBin, "cs2"), "300");
        File.WriteAllText(Path.Combine(stopRoot, "farm", "game", "cs2.sh"), "#!/bin/bash\nsleep 300\n");
        File.SetUnixFileMode(Path.Combine(stopRoot, "farm", "game", "cs2.sh"), (UnixFileMode)0b111_101_101);
        Thread.Sleep(300);
        var (okStart, whyStart) = te.Start(2);
        Check($"engine start: nothing answering is a refusal ({whyStart})", !okStart && whyStart.Contains("did not answer"));
        Check("engine start: an engine still running from the test tree is killed first", stale.WaitForExit(5000) && stale.ExitCode == 137);
        Check("engine start: its own attempt is gone again", !te.HasProcess);
    }

    // ---- a test load's mode, and the real test load's first steps
    internal static void Definitions()
    {
        Check("test load: its mode, game_type and game_mode from the tags, Arms Race without one; a map's list mode the same way",
              TestLoad.ModeOf(["Wingman"]) == ("wingman", 0, 2) && TestLoad.ModeOf([]) == ("armsrace", 1, 0) && Status.ListMode(O("{\"tags\": [\"Deathmatch\"]}")) == "deathmatch");
        DownEngine.Sent.Clear();
        var got = TestLoad.Load(new DownEngine(), "4242", ["de_x"], ["Wingman"], 20, () => false, C(new FakeClock(HandOverTests.KT)));
        Check($"test load (the real one): the map asked for in its mode, a crash reported - no exception ({Node.StrOr(got["verdict"], "None")})",
              Node.Str(got["verdict"]) == "crash" && DownEngine.Sent.SequenceEqual(["game_type 0; game_mode 2; host_workshop_map 4242"]));
    }

    // ---- SteamCMD's driver: a stand-in steamcmd.sh that answers as SteamCMD does, in a session of its own
    internal static void SteamCmdDriver(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            foreach (var name in new[] { "steamcmd: the anonymous login, a download, a failure", "steamcmd: an item it says nothing about is a stall, after its timeout",
                                         "steamcmd: the stop takes the whole process group, a child that ignores quit too" })
                Linux(name, () => false);
            return;
        }
        var dir = Path.Combine(root, "steamcmd");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "steamcmd.sh");
        File.WriteAllText(exe, """
            #!/bin/bash
            sleep 300 &
            echo "Redirecting stderr to 'stderr.txt'"
            echo "Connecting anonymously to Steam Public...OK"
            echo "Waiting for user info...OK"
            printf 'Steam>'
            while read -r cmd a b; do
              case "$cmd" in
                workshop_download_item)
                  case "$b" in
                    111) echo "Downloading item 111 ..."; echo "Success. Downloaded item 111 to \"/x/content/730/111\" (1234 bytes)";;
                    222) echo "ERROR! Download item 222 failed (Access Denied)";;
                    333) sleep 3;;
                  esac
                  printf 'Steam>';;
                quit) exit 0;;
              esac
            done

            """);
        File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        var clock = new SyncClock(SystemClock.Instance, Paris);
        var steam = new SteamCmdProcess(exe, Path.Combine(dir, "staging"), Path.Combine(dir, "steamcmd.log"), dir, clock) { LoginTimeout = 20, DownloadTimeout = 1 };
        var login = steam.Start();
        var good = steam.Download("111");
        var bad = steam.Download("222");
        Check($"steamcmd: the anonymous login, a download, a failure ({login.Why}; {good.Reason}; {bad.Reason})",
              login.Ok && good is (true, "OK", 1234) && bad is (false, "Access Denied", 0) && File.ReadAllText(Path.Combine(dir, "steamcmd.log")).Contains("Downloaded item 111"));
        var stall = steam.Download("333");
        Check($"steamcmd: an item it says nothing about is a stall, after its timeout ({stall.Reason})",
              !stall.Ok && stall.Reason == SteamCmdWords.Stall + " 1s" && stall.Reason.StartsWith(SteamCmdWords.Stall, StringComparison.Ordinal));
        var pid = typeof(SteamCmdProcess).GetField("proc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(steam) is Process p ? p.Id : -1;
        steam.Stop();
        Check("steamcmd: the stop takes the whole process group, a child that ignores quit too", pid > 0 && !Native.GroupAlive(pid));
    }

    // ---- the test tree: links into the game's install, the engine's own files copied, no log address, no plugins
    internal static void TreeBuild(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            Linux("test tree: links into the game, the engine's files copied, the cfgs without log addresses, gameinfo without the plugins", () => false);
            return;
        }
        var game = Path.Combine(root, "tree-game");
        var work = Path.Combine(root, "tree-work");
        var cfgDir = Path.Combine(root, "tree-cfg");
        Put(Path.Combine(game, "game", "cs2.sh"), "#!/bin/bash\n");
        Put(Path.Combine(game, "game", "other.txt"), "x");
        Put(Path.Combine(game, "game", "bin", "linuxsteamrt64", "cs2"), "engine");
        Put(Path.Combine(game, "game", "bin", "linuxsteamrt64", "libengine.so"), "lib");
        Put(Path.Combine(game, "game", "bin", "linuxsteamrt64", "x.log"), "log");
        Put(Path.Combine(game, "game", "bin", "linuxsteamrt64", "steamapps", "workshop", "appworkshop_730.acf"), "acf");
        Put(Path.Combine(game, "game", "bin", "other", "tool"), "t");
        Put(Path.Combine(game, "game", "csgo", "maps", "x.vpk"), "m");
        Put(Path.Combine(game, "game", "csgo", "addons", "metamod", "x"), "m");
        Put(Path.Combine(game, "game", "csgo", "console.log"), "c");
        Put(Path.Combine(game, "game", "csgo", "cfg", "server.cfg"), "hostname x\nlogaddress_add_http \"http://198.51.100.9/log\"\n");
        Put(Path.Combine(game, "game", "csgo", "cfg", "sub", "a.cfg"), "a 1\nLOGADDRESS_ADD x\nb 2");
        Put(Path.Combine(game, "game", "csgo", "gameinfo.gi"), "\t\t\tGame\tcsgo/addons/metamod\n\t\t\tGame\tcsgo\n");
        Put(Path.Combine(cfgDir, "gamemode_armsrace_server.cfg"), "mp_x 1\n");
        var line = TestTree.Build(game, work, cfgDir, new SyncClock(SystemClock.Instance, Paris));
        var farm = Path.Combine(work, "farm", "game");
        Check($"test tree: links into the game, the engine's files copied, the cfgs without log addresses, gameinfo without the plugins ({line})",
              !Handover.IsLink(Path.Combine(farm, "cs2.sh")) && File.Exists(Path.Combine(farm, "cs2.sh")) && Handover.IsLink(Path.Combine(farm, "other.txt"))
              && !Handover.IsLink(Path.Combine(farm, "bin", "linuxsteamrt64", "cs2")) && File.Exists(Path.Combine(farm, "bin", "linuxsteamrt64", "libengine.so"))
              && !File.Exists(Path.Combine(farm, "bin", "linuxsteamrt64", "x.log")) && Handover.IsLink(Path.Combine(farm, "bin", "linuxsteamrt64", "steamapps"))
              && new FileInfo(Path.Combine(farm, "bin", "linuxsteamrt64", "steamapps")).LinkTarget == Path.Combine(work, "staging", "steamapps")
              && Handover.IsLink(Path.Combine(farm, "bin", "other")) && Handover.IsLink(Path.Combine(farm, "csgo", "maps"))
              && !Directory.Exists(Path.Combine(farm, "csgo", "addons")) && !File.Exists(Path.Combine(farm, "csgo", "console.log"))
              && File.ReadAllText(Path.Combine(farm, "csgo", "cfg", "server.cfg")) == TestTree.TestServerCfg
              && File.ReadAllText(Path.Combine(farm, "csgo", "cfg", "sub", "a.cfg")) == "a 1\nb 2"
              && File.Exists(Path.Combine(farm, "csgo", "cfg", "gamemode_armsrace_server.cfg"))
              && File.ReadAllText(Path.Combine(farm, "csgo", "gameinfo.gi")) == "\t\t\tGame\tcsgo\n"
              && line.Contains("2 engine files copied") && line.Contains("search path removed") && line.Contains("from 2 cfg file(s)"));
    }
}
