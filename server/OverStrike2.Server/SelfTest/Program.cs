// The server program's self-test: the restart watcher's decisions, its scenarios (the real Watch loop on a fake clock with a fake engine
// and a fake bridge), the maps folder, the hand-over and the deletion, job 9 and the download line, and the plugin layer's install.
// Every check prints PASS, FAIL or SKIP; the exit code is the number of failures (0: all passed).
//   dotnet run --project server/OverStrike2.Server/SelfTest
// The checks on links, /proc and a folder's fsync need Linux and say SKIP elsewhere. The run that counts is the one in the game image:
//   dotnet publish server/OverStrike2.Server/SelfTest -c Release -r linux-x64 -o <dir>, then run <dir>/OverStrike2.Server.SelfTest
//   inside the image (it needs nothing of the game).
using OverStrike2.Server.SelfTest;

var root = T.Root();
try
{
    PureTests.Run();
    ScenarioTests.Run();
    WorkshopTests.Run();
    OrganizeTests.Run();
    RestartTests.Run();
    InstallTests.Run(root);
}
finally
{
    T.Clean();
}
Console.WriteLine($"\n{T.Fails} failure(s) of {T.Checks} checks{(T.Skips > 0 ? $", {T.Skips} skipped (Linux only)" : "")}");
return T.Fails;

namespace OverStrike2.Server.SelfTest
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

        /// <summary>The repository's root: the folder above this program's that holds core/ and server/.</summary>
        public static string Root()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                    if (File.Exists(Path.Combine(d.FullName, "core", "OverStrike2.Core.csproj")))
                        return d.FullName;
            return "";   // in the game image, run from a copy: the checks that read the repository say so
        }

        static readonly string Base = Path.Combine(Path.GetTempPath(), "os2-server-selftest-" + Environment.ProcessId);

        /// <summary>A fresh folder of the test's own.</summary>
        public static string Dir(string name)
        {
            var d = Path.Combine(Base, name);
            if (Directory.Exists(d))
                Directory.Delete(d, true);
            Directory.CreateDirectory(d);
            return d;
        }

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

        public static string Show<TItem>(IEnumerable<TItem> items) => "[" + string.Join(", ", items) + "]";
    }
}
