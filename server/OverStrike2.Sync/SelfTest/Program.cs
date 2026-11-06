// The Workshop sync's self-test: every check the Python sync's tests held, under the same names but for those that named a Python file,
// and the checks of what the port brings new - the settings file, the SteamCMD driver, the test tree, the SMTP client. Every check prints
// PASS, FAIL or SKIP; the exit code is the number of failures (0: all passed).
//   dotnet run --project server/OverStrike2.Sync/SelfTest
// The checks on process groups, /proc, links and real processes need Linux and say SKIP elsewhere. The run that counts is the one in the
// game image:
//   dotnet publish server/OverStrike2.Sync/SelfTest -c Release -r linux-x64 -o <dir>, then run <dir>/OverStrike2.Sync.SelfTest inside the
//   image (it needs nothing of the game but its bash, setsid and SteamCMD's folder).
using OverStrike2.Sync.SelfTest;

try
{
    Part1.Run();
    Part2.Run();
    Part3.Run();
    NewChecks.Run();
    Lifetimes.Run();
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
