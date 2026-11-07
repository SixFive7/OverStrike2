// What the self-tests of core and os2-server share, compiled into each as source: the checks and their counts, a scratch folder of the
// run's own, and a clock moved by hand. Every check prints PASS, FAIL or SKIP; the program's exit code is the number of failures.
using OverStrike2.Core;

namespace OverStrike2.TestKit;

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

    /// <summary>A check whose exception is a failure, with the exception's words, never the end of the run.</summary>
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

    /// <summary>A check that needs Linux (links, /proc, a folder's fsync): SKIP elsewhere. The run that counts is the one in the game image.</summary>
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

    /// <summary>The last line of the run: the failures, the checks and the skips.</summary>
    public static string Summary => $"\n{Fails} failure(s) of {Checks} checks{(Skips > 0 ? $", {Skips} skipped (Linux only)" : "")}";

    /// <summary>The repository's root: the folder above the program that holds core/; empty when there is none - in the game image the
    /// self-test runs from a copy, and the checks that read the repository say so.</summary>
    public static string Root()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "core", "OverStrike2.Core.csproj")))
                    return d.FullName;
        return "";
    }

    static readonly string Base = Path.Combine(Path.GetTempPath(), $"os2-selftest-{AppDomain.CurrentDomain.FriendlyName}-{Environment.ProcessId}");

    /// <summary>A fresh folder of the run's own.</summary>
    public static string Dir(string name)
    {
        var d = Path.Combine(Base, name);
        if (Directory.Exists(d))
            Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A path in the run's own folder, for one file.</summary>
    public static string Temp(string name)
    {
        Directory.CreateDirectory(Base);
        return Path.Combine(Base, name);
    }

    /// <summary>The run's own folder, gone at the end.</summary>
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

/// <summary>A clock the tests move by hand: Sleep advances it.</summary>
sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset Now { get; set; } = start;

    /// <summary>Now in unix seconds.</summary>
    public double T => (Now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

    public void Sleep(TimeSpan span)
    {
        if (span > TimeSpan.Zero)
            Now += span;
    }

    public void Sleep(double seconds) => Sleep(TimeSpan.FromSeconds(seconds));
}
