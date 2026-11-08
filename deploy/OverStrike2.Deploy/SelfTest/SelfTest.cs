using System.Diagnostics;

namespace OverStrike2.Deploy;

/// <summary>--selftest: every offline check of the deploy tooling - the Python tests' every case, ported (deploy/test_deploy.py,
/// test_release.py - gone with release.py in 193 -, test_check.py; tools/client's test_testcopy.py, test_testcopy_bridge.py, test_bridgepost.py, test_publish.py) - with no
/// server, no Docker and no private config: stand-ins answer for them, in scratch folders under the repository's .work/.
///
///     os2-deploy --selftest              every suite; ends SELFTEST PASSED (exit 0) or SELFTEST FAILED (exit 1)
///     os2-deploy --selftest check ...    the suites named</summary>
static partial class SelfTest
{
    static readonly TextWriter Real = Console.Out;
    static string suite = "";
    static int checks, failures, skips;
    static readonly List<(string Suite, int Checks, int Failures, int Skips)> Results = [];

    /// <summary>One check: PASS or FAIL and its name, on the real output whatever a test has captured.</summary>
    public static void Check(string name, bool ok)
    {
        checks++;
        if (!ok)
            failures++;
        Real.WriteLine((ok ? "PASS " : "FAIL ") + name);
        Real.Flush();
    }

    /// <summary>Checks that cannot run here: said, counted apart, never a pass.</summary>
    public static void Skip(string name, int count = 1)
    {
        skips += count;
        Real.WriteLine("SKIP " + name);
        Real.Flush();
    }

    /// <summary>A body's result (or the exit code it ended with) and every word it printed.</summary>
    public static (T? Result, int? Exit, string Output) Capture<T>(Func<T> body)
    {
        var was = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            return (body(), null, sw.ToString());
        }
        catch (Exit e)
        {
            return (default, e.Code, sw.ToString());
        }
        finally
        {
            Console.SetOut(was);
        }
    }

    /// <summary>The words a body ended with (an Exit's text, or its code), what it printed kept out of the way; null when it ended normally.</summary>
    public static string? Refusal(Action body)
    {
        var was = Console.Out;
        Console.SetOut(new StringWriter());
        try
        {
            body();
            return null;
        }
        catch (Exit e)
        {
            return e.Message;
        }
        finally
        {
            Console.SetOut(was);
        }
    }

    /// <summary>A scratch folder under the repository's .work/ (gitignored), removed by the suite at its end.</summary>
    public static string Scratch(string prefix)
    {
        var work = Repo.P(".work");
        Directory.CreateDirectory(work);
        var dir = Path.Combine(work, prefix + Path.GetRandomFileName().Replace(".", "", StringComparison.Ordinal));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Remove(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static readonly (string Name, Action Run)[] Suites =
    [
        ("deploy", DeployTests.Run),
        ("release", ReleaseTests.Run),
        ("check", CheckTests.Run),
        ("testcopy", TestCopyTests.Run),
        ("testcopy-bridge", TestCopyBridgeTests.Run),
        ("testcopy-plugin", TestCopyPluginTests.Run),
        ("bridgepost", BridgePostTests.Run),
        ("base", BaseTests.Run),
        ("thumbs", ThumbsTests.Run),
        ("rehearse", RehearseTests.Run),
        ("publish", PublishTests.Run),
        ("ops", OpsTests.Run),
    ];

    public static int Command(string[] args)
    {
        var wanted = args.Where(a => !a.StartsWith('-')).ToList();
        var unknown = wanted.Where(w => !Suites.Any(s => s.Name == w)).ToList();
        if (unknown.Count > 0)
            throw new Exit(2, $"no such suite: {string.Join(", ", unknown)} (there are {string.Join(", ", Suites.Select(s => s.Name))})");
        var clock = Out.Clock;
        var watch = Stopwatch.StartNew();
        foreach (var (name, run) in Suites)
        {
            if (wanted.Count > 0 && !wanted.Contains(name))
                continue;
            suite = name;
            checks = failures = skips = 0;
            Real.WriteLine($"== {name}");
            try
            {
                run();
            }
            catch (Exception e)
            {
                Check($"{name}: ran to its end ({e.GetType().Name}: {e.Message})", false);
            }
            finally
            {
                Out.Clock = clock;
                Console.SetOut(Real);
            }
            Results.Add((name, checks, failures, skips));
            Real.WriteLine($"{name}: {checks - failures} of {checks} checks passed" + (skips > 0 ? $", {skips} skipped" : ""));
        }
        Real.WriteLine();
        foreach (var (name, n, f, s) in Results)
            Real.WriteLine($"  {name,-16} {n,4} checks, {f} failed" + (s > 0 ? $", {s} skipped" : ""));
        var total = Results.Sum(r => r.Checks);
        var failed = Results.Sum(r => r.Failures);
        Real.WriteLine($"{total} checks, {failed} failed, in {watch.Elapsed.TotalSeconds:0} s");
        Real.WriteLine(failed == 0 ? "SELFTEST PASSED" : "SELFTEST FAILED");
        return failed == 0 ? 0 : 1;
    }
}
