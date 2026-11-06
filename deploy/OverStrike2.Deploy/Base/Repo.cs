using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OverStrike2.Deploy;

/// <summary>This checkout: its root, git, its committed bytes, and careful copies of them (deploy/os2.py's git, clean_tree, sha256 and
/// copy_verified).</summary>
static class Repo
{
    static string? root;

    /// <summary>The repository's root: the folder up from this program (or the current folder) that holds this project.</summary>
    public static string Root
    {
        get => root ??= Find(AppContext.BaseDirectory) ?? Find(Directory.GetCurrentDirectory())
            ?? throw new Exit(2, "this program is not inside an Over Strike 2 checkout (no deploy/OverStrike2.Deploy above it or the current folder)");
        set => root = value;
    }

    static string? Find(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "deploy", "OverStrike2.Deploy", "OverStrike2.Deploy.csproj")))
                return d.FullName;
        return null;
    }

    public static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    // ------------------------------------------------------------------------------------------------------------------- git

    /// <summary>git -C root args: its stdout, trimmed (whatever its exit code, as os2.git).</summary>
    public static Func<string[], string> Git = args => Proc.Capture(["git", "-C", Root, .. args]).Out.Trim();

    /// <summary>The deploy ships committed bytes only: refuse when any of these paths has uncommitted changes. Returns HEAD, 12 digits.</summary>
    public static Func<IReadOnlyList<string>, string> CleanTree = paths =>
    {
        var dirty = Git(["status", "--porcelain", "--", .. paths]);
        if (dirty.Length > 0)
            throw Out.Fail("uncommitted changes in what would ship - commit them first:\n" + dirty);
        return Git(["rev-parse", "--short=12", "HEAD"]);
    };

    /// <summary>git show commit:rel as bytes; null when it has no such file.</summary>
    public static byte[]? Show(string commit, string rel)
    {
        var r = Proc.CaptureBytes(["git", "-C", Root, "show", $"{commit}:{rel}"]);
        return r.Code == 0 ? r.Out : null;
    }

    /// <summary>data.py's committed_before: the commit (of the last `depth` that touched rel) whose copy of rel is exactly these bytes, or
    /// null - is a server copy one this repository ever held?</summary>
    public static Func<string, byte[], int, string?> CommittedBefore = (rel, data, depth) =>
    {
        foreach (var c in Git(["log", "--format=%H", "-n", depth.ToString(System.Globalization.CultureInfo.InvariantCulture), "--", rel])
                     .Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Show(c, rel);
            if (bytes is not null && bytes.AsSpan().SequenceEqual(data))
                return c;
        }
        return null;
    };

    // ---------------------------------------------------------------------------------------------------------- careful copies

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length)
            return false;
        using var sa = fa.OpenRead();
        using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var ba = new byte[1 << 20];
        var bb = new byte[1 << 20];
        while (true)
        {
            var na = sa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            var nb = sb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb)))
                return false;
            if (na == 0)
                return true;
        }
    }

    /// <summary>Copy when the destination lacks the file or holds other bytes; compare after. "same", "copied" or "would copy".
    /// atomic: the bytes go to &lt;dst&gt;.new and are renamed over dst once verified, so a reader sees the old file or the new one, never half
    /// of one, and a program running from dst keeps the copy it started with. Without it the file is written in place: a container that
    /// mounts that one file stays on it, and sees the new bytes.</summary>
    public static string CopyVerified(string src, string dst, bool dry = false, bool atomic = false)
    {
        if (File.Exists(dst) && SameBytes(src, dst))
            return "same";
        if (dry)
            return "would copy";
        var folder = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        var to = atomic ? dst + ".new" : dst;
        using (var input = File.OpenRead(src))
        using (var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
        if (!SameBytes(src, to))
            throw Out.Fail($"{to} is not byte-identical to {src} after the copy");
        if (atomic)
            File.Move(to, dst, overwrite: true);
        return "copied";
    }

    /// <summary>The note of the server lock (server.lock_file), when it is held: whoever holds it is working on the server.</summary>
    public static string? LockNote(IConfig cfg)
    {
        var path = cfg.Str("server.lock_file");
        return path.Length > 0 && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null;
    }

    /// <summary>The lock note as one line, as every command shows it.</summary>
    public static string Oneline(string note) => Py.Slice(note.Replace("\n", " | ", StringComparison.Ordinal), 0, 300);
}

/// <summary>A program run from here: its output captured, or handed this console; at below-normal priority for builds.</summary>
static class Proc
{
    public sealed record Result(int Code, string Out, string Err)
    {
        public string All => Out + Err;
    }

    public sealed record BytesResult(int Code, byte[] Out);

    static ProcessStartInfo Info(IReadOnlyList<string> argv, string? cwd, IDictionary<string, string?>? env, bool capture)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            CreateNoWindow = capture,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            RedirectStandardInput = false,
            WorkingDirectory = cwd ?? "",
        };
        if (capture)
        {
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
        }
        foreach (var a in argv.Skip(1))
            psi.ArgumentList.Add(a);
        if (env is not null)
        {
            psi.Environment.Clear();
            foreach (var (k, v) in env)
                if (v is not null)
                    psi.Environment[k] = v;
        }
        return psi;
    }

    static readonly Lock Priority = new();

    /// <summary>Start a process; below: at below-normal priority. On Windows a process inherits its creator's below-normal class, so this one
    /// takes it for the moment of the start and gives it back: the child and everything it starts stay below normal, this program does not.</summary>
    static Process Start(ProcessStartInfo psi, bool below)
    {
        if (!below || !OperatingSystem.IsWindows())
            return Process.Start(psi) ?? throw new InvalidOperationException($"{psi.FileName} did not start");
        lock (Priority)
        {
            var me = Process.GetCurrentProcess();
            var was = me.PriorityClass;
            me.PriorityClass = ProcessPriorityClass.BelowNormal;
            try
            {
                return Process.Start(psi) ?? throw new InvalidOperationException($"{psi.FileName} did not start");
            }
            finally
            {
                me.PriorityClass = was;
            }
        }
    }

    /// <summary>Run and capture stdout and stderr as text (UTF-8, errors replaced).</summary>
    public static Result Capture(IReadOnlyList<string> argv, string? cwd = null, IDictionary<string, string?>? env = null, bool below = false,
                                 double? timeoutSeconds = null)
    {
        using var p = Start(Info(argv, cwd, env, capture: true), below);
        var err = p.StandardError.ReadToEndAsync();
        var outText = p.StandardOutput.ReadToEndAsync();
        if (timeoutSeconds is double t)
        {
            if (!p.WaitForExit(TimeSpan.FromSeconds(t)))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                throw new TimeoutException($"{argv[0]} did not end within {t:0} s");
            }
        }
        p.WaitForExit();
        return new Result(p.ExitCode, outText.Result, err.Result);
    }

    /// <summary>Run and capture stdout as bytes (stderr dropped).</summary>
    public static BytesResult CaptureBytes(IReadOnlyList<string> argv, string? cwd = null)
    {
        var psi = Info(argv, cwd, null, capture: true);
        using var p = Start(psi, below: false);
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        _ = err.Result;
        return new BytesResult(p.ExitCode, ms.ToArray());
    }

    /// <summary>Run with this console: what it prints goes straight out. Its exit code.</summary>
    public static int Run(IReadOnlyList<string> argv, string? cwd = null, IDictionary<string, string?>? env = null, bool below = false)
    {
        Console.Out.Flush();
        using var p = Start(Info(argv, cwd, env, capture: false), below);
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>dotnet with these arguments at below-normal priority, no build server or node left behind: (exit code, its output).</summary>
    public static Func<string[], string, (int Code, string Output)> Dotnet = (args, cwd) =>
    {
        var r = Capture(["dotnet", .. args, "-nodeReuse:false", "-p:UseSharedCompilation=false"], cwd: cwd, below: true);
        return (r.Code, r.All);
    };

    /// <summary>A command line run by the system's shell in cwd, its output and errors into a file there - a file, not a pipe, so nothing the
    /// command leaves running can hold this call open. Its exit code.</summary>
    public static int RunToFile(string command, string cwd, string logName, bool below = false)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{command} > {logName} 2>&1\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", $"{command} > {logName} 2>&1" } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = cwd;
        Console.Out.Flush();
        using var p = Start(psi, below);
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>This environment as a mutable copy, to hand a child with a few variables changed.</summary>
    public static Dictionary<string, string?> Environment()
    {
        var env = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value;
        return env;
    }

    /// <summary>The command line that runs this program again: the program itself, or dotnet with its DLL.</summary>
    public static List<string> Self()
    {
        var exe = System.Environment.ProcessPath ?? "dotnet";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return [exe, typeof(Proc).Assembly.Location];
        return [exe];
    }
}
