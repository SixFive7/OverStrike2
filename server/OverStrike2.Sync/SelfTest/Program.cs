// The Workshop sync's self-test, a file a subject: the settings, the limits, discovery and the check, the scan, the verdicts, the publish,
// the report and the problem mail, the JSON and the words, whole runs, the service, the hand-over, the processes, and a map's life over
// days. Every check prints PASS, FAIL or SKIP; the exit code is the number of failures (0: all passed).
//   dotnet run --project server/OverStrike2.Sync/SelfTest
// The checks on process groups, /proc, links and real processes need Linux and say SKIP elsewhere. The run that counts is the one in the
// game image:
//   dotnet publish server/OverStrike2.Sync/SelfTest -c Release -r linux-x64 -o <dir>, then run <dir>/OverStrike2.Sync.SelfTest inside the
//   image (it needs nothing of the game but its bash, setsid and SteamCMD's folder).
// The checks run in the order below: a scenario may build on the folders under T.Root, W's paths and the stand-ins in W.Hooks the ones
// before it left.
using OverStrike2.Sync.SelfTest;
using static OverStrike2.Sync.SelfTest.W;

try
{
    var root = T.Root;
    Directory.CreateDirectory(root);
    (State, Work, Data, GameRoot) = (Path.Combine(root, "state"), Path.Combine(root, "work"), Path.Combine(root, "data"), Path.Combine(root, "game-none"));
    SettingsTests.Config();
    LimitTests.LedgerChecks(root);
    LimitTests.Door(root);
    CheckTests.DiscoveryChecks(root);
    CheckTests.CheckStep(root);
    LimitTests.Downloads(root);
    ScanTests.ScanChecks(root);
    VerdictTests.VerdictChecks();
    PublishTests.Plan(root);
    ReportTests.MailAndBacklog();
    PublishTests.MergeRule();
    PublishTests.Bridge(root);
    ProcessTests.Engines();
    RunTests.WholeRun(root);
    ReportTests.ProblemMail(root);
    RunTests.StatusFile();
    RunTests.Join();
    RunTests.Stops();
    RunTests.Stalls();
    RunTests.Gone();
    RunTests.WorkshopJson();
    RunTests.PictureOnly();
    ServiceTests.Clock();
    HandOverTests.Acf(root);
    var kdata = HandOverTests.KData();
    HandOverTests.KeepSetChecks(kdata);
    HandOverTests.Votes(root);
    HandOverTests.RunSteps(root, kdata);
    HandOverTests.HandOver();
    HandOverTests.Serve();
    HandOverTests.Staged();
    HandOverTests.Tidy();
    ProcessTests.Definitions();
    HandOverTests.Reshaped(root);
    var fresh = T.Dir("new");
    SettingsTests.SettingsFile(fresh);
    SettingsTests.StockRule();
    FormatTests.Formats();
    FormatTests.SearchTexts();
    LimitTests.DoorAndLedger(fresh);
    ServiceTests.Cli(fresh);
    ReportTests.SmtpClient();
    ProcessTests.SteamCmdDriver(fresh);
    ProcessTests.TreeBuild(fresh);
    HandOverTests.TakenIn(fresh);
    ServiceTests.Stopping();
    LifetimeTests.Run();
}
finally
{
    T.Clean();
}
Console.WriteLine($"\n{T.Fails} failure(s) of {T.Checks} checks{(T.Skips > 0 ? $", {T.Skips} skipped (Linux only)" : "")}");
return T.Fails;

namespace OverStrike2.Sync.SelfTest
{
    static class T
    {
        public static int Fails, Checks, Skips;

        public static void Check(string name, bool ok)
        {
            Checks++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
                Fails++;
        }

        public static void Check(string name, Func<bool> ok)
        {
            bool r;
            try
            {
                r = ok();
            }
            catch (Exception e)
            {
                Console.WriteLine($"     ({name}: {e.GetType().Name}: {e.Message})");
                r = false;
            }
            Check(name, r);
        }

        /// <summary>A check that needs Linux: SKIP elsewhere.</summary>
        public static void Linux(string name, Func<bool> ok)
        {
            if (OperatingSystem.IsLinux())
                Check(name, ok);
            else
            {
                Skips++;
                Console.WriteLine("SKIP " + name);
            }
        }

        static readonly string Base = Path.Combine(Path.GetTempPath(), "os2-sync-selftest-" + Environment.ProcessId);

        /// <summary>A fresh folder of the test's own.</summary>
        public static string Dir(string name)
        {
            var d = Path.Combine(Base, name);
            if (Directory.Exists(d))
                Directory.Delete(d, true);
            Directory.CreateDirectory(d);
            return d;
        }

        public static string Root => Base;

        public static void Clean()
        {
            try
            {
                if (Directory.Exists(Base))
                    Directory.Delete(Base, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        public static void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public static string Show<TItem>(IEnumerable<TItem> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
    }
}
