// The core library's self-test. Every check prints PASS or FAIL; the exit code is the number of failures (0: all passed).
//   dotnet run --project core/SelfTest              every check
//   dotnet run --project core/SelfTest -- --write   regenerate docs/settings.schema.json and docs/settings.md's key tables first
using OverStrike2.Core;
using OverStrike2.Core.SelfTest;

var root = T.Root();
if (args.Contains("--write"))
{
    File.WriteAllText(Path.Combine(root, "docs", "settings.schema.json"), SettingsDocs.JsonSchema());
    var md = Path.Combine(root, "docs", "settings.md");
    File.WriteAllText(md, T.WithKeys(File.ReadAllText(md), SettingsDocs.KeysMarkdown()));
    Console.WriteLine("wrote docs/settings.schema.json and the key tables of docs/settings.md");
}
try
{
    SettingsTests.Run(root);
    KeyValuesTests.Run();
    NetTests.Run();
    TimeTests.Run();
    KeepSetTests.Run();
    FilesTests.Run();
    SearchTextTests.Run();
    BridgeWireTests.Run();
}
finally
{
    T.Clean();
}
Console.WriteLine($"\n{T.Fails} failure(s) of {T.Checks} checks");
return T.Fails;

namespace OverStrike2.Core.SelfTest
{
    static class T
    {
        public static int Fails, Checks;

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

        /// <summary>The repository's root: the folder above this program's that holds core/ and docs/.</summary>
        public static string Root()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                    if (File.Exists(Path.Combine(d.FullName, "core", "OverStrike2.Core.csproj")))
                        return d.FullName;
            throw new DirectoryNotFoundException("no core/OverStrike2.Core.csproj above " + Directory.GetCurrentDirectory());
        }

        public const string Begin = "<!-- keys:begin (made from core/Settings.cs: dotnet run --project core/SelfTest -- --write) -->";
        public const string End = "<!-- keys:end -->";

        /// <summary>docs/settings.md with the block between the markers replaced by `keys`.</summary>
        public static string WithKeys(string md, string keys)
        {
            var i = md.IndexOf(Begin, StringComparison.Ordinal);
            var j = md.IndexOf(End, StringComparison.Ordinal);
            if (i < 0 || j < i)
                throw new InvalidOperationException("docs/settings.md has no keys markers");
            return md[..(i + Begin.Length)] + "\n" + keys + "\n" + md[j..];
        }

        /// <summary>The block between the markers.</summary>
        public static string KeysBlock(string md)
        {
            var i = md.IndexOf(Begin, StringComparison.Ordinal);
            var j = md.IndexOf(End, StringComparison.Ordinal);
            return i < 0 || j < i ? "" : md[(i + Begin.Length)..j].Trim('\n');
        }

        static readonly string TempDir = Path.Combine(Path.GetTempPath(), "os2-core-selftest-" + Environment.ProcessId);

        public static string Temp(string name)
        {
            Directory.CreateDirectory(TempDir);
            return Path.Combine(TempDir, name);
        }

        /// <summary>The test's own temporary folder, gone at the end.</summary>
        public static void Clean()
        {
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>A clock the tests move by hand: Sleep advances it.</summary>
    sealed class FakeClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset Now { get; set; } = start;
        public List<TimeSpan> Sleeps { get; } = [];

        public void Sleep(TimeSpan span)
        {
            Sleeps.Add(span);
            if (span > TimeSpan.Zero)
                Now += span;
        }
    }
}
