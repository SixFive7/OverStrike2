using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OverStrike2.Deploy;

/// <summary>A program started with its output and errors in a file and its input empty, as docshots.py's Popen(stdout=file, stdin=DEVNULL) starts
/// one: a file, not a pipe, so a grandchild that inherits the handle can never hold a wait open past its timeout. On Windows through CreateProcess
/// with exactly those two handles inherited and the command line Python's list2cmdline makes; elsewhere through /bin/sh.</summary>
sealed class Spawned : IDisposable
{
    readonly Process? process;          // elsewhere
    readonly IntPtr handle;             // Windows: the process handle
    public int Pid { get; }

    Spawned(Process p)
    {
        process = p;
        Pid = p.Id;
    }

    Spawned(IntPtr h, int pid)
    {
        handle = h;
        Pid = pid;
    }

    /// <summary>Start argv with its output and errors in outFile (created anew).</summary>
    public static Spawned Start(IReadOnlyList<string> argv, string outFile)
    {
        Console.Out.Flush();
        if (!OperatingSystem.IsWindows())
        {
            var quoted = string.Join(" ", argv.Select(a => "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, ArgumentList = { "-c", $"exec {quoted} > '{outFile.Replace("'", "'\\''", StringComparison.Ordinal)}' 2>&1 < /dev/null" } };
            return new Spawned(Process.Start(psi) ?? throw new InvalidOperationException($"{argv[0]} did not start"));
        }
        return StartWindows(argv, outFile);
    }

    /// <summary>Whether it ended within the time, and its exit code when it did.</summary>
    public bool Wait(double seconds, out int code)
    {
        code = -1;
        if (process is not null)
        {
            if (!process.WaitForExit(TimeSpan.FromSeconds(seconds)))
                return false;
            code = process.ExitCode;
            return true;
        }
        var ms = seconds >= int.MaxValue / 1000.0 ? uint.MaxValue - 1 : (uint)Math.Max(0, seconds * 1000);
        if (Native.WaitForSingleObject(handle, ms) != 0)
            return false;
        if (!Native.GetExitCodeProcess(handle, out var exit))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        code = unchecked((int)exit);
        return true;
    }

    /// <summary>The process and everything it started: taskkill /T /F, as docshots.py does on a timeout.</summary>
    public void KillTree()
    {
        if (process is not null)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }
        Proc.Capture(["taskkill", "/PID", Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "/T", "/F"]);
    }

    public void Dispose()
    {
        process?.Dispose();
        if (handle != IntPtr.Zero)
            Native.CloseHandle(handle);
    }

    // ------------------------------------------------------------------------------------------------------------- Windows

    const uint HandleFlagInherit = 1;
    const int StartfUseStdHandles = 0x100;
    const uint ExtendedStartupInfoPresent = 0x80000, CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x400;
    static readonly IntPtr ProcThreadAttributeHandleList = 0x20002;

    static Spawned StartWindows(IReadOnlyList<string> argv, string outFile)
    {
        using var output = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var nul = new FileStream("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var outHandle = output.SafeFileHandle;
        var inHandle = nul.SafeFileHandle;
        Inheritable(outHandle);
        Inheritable(inHandle);
        IntPtr list = IntPtr.Zero, handles = IntPtr.Zero;
        try
        {
            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handles, 0, outHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, inHandle.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, handles, IntPtr.Size * 2, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var si = new Native.StartupInfoEx();
            si.StartupInfo.cb = Marshal.SizeOf<Native.StartupInfoEx>();
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = inHandle.DangerousGetHandle();
            si.StartupInfo.hStdOutput = outHandle.DangerousGetHandle();
            si.StartupInfo.hStdError = outHandle.DangerousGetHandle();
            si.lpAttributeList = list;
            var line = new System.Text.StringBuilder(Py.List2CmdLine(argv));
            if (!Native.CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, true, ExtendedStartupInfoPresent | CreateNoWindow | CreateUnicodeEnvironment,
                                       IntPtr.Zero, null, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"{argv[0]} did not start");
            Native.CloseHandle(pi.hThread);
            return new Spawned(pi.hProcess, pi.dwProcessId);
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
            }
            if (handles != IntPtr.Zero)
                Marshal.FreeHGlobal(handles);
        }
    }

    static void Inheritable(SafeFileHandle h)
    {
        if (!Native.SetHandleInformation(h, HandleFlagInherit, HandleFlagInherit))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? app, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
                                                 uint flags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation info);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
