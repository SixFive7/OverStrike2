using System.Runtime.InteropServices;

namespace OverStrike2.Core;

/// <summary>The few libc calls the programs make: signals to a process or its group, a group's liveness, a volume's free space, whether this
/// user may write somewhere, a folder's fsync. Linux only: elsewhere - builds and self-tests - each does nothing and says so its own way.</summary>
public static partial class Posix
{
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int KillCall(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "statvfs", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatVfs(string path, byte* buf);

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Access(string path, int mode);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    public const int SigTerm = 15, SigKill = 9;
    const int Eperm = 1;

    /// <summary>kill(2); false when it failed (no such process, not ours) or off Linux.</summary>
    public static bool Signal(int pid, int sig) => OperatingSystem.IsLinux() && KillCall(pid, sig) == 0;

    /// <summary>killpg(2), as kill(-pgid).</summary>
    public static bool SignalGroup(int pgid, int sig) => OperatingSystem.IsLinux() && pgid > 0 && KillCall(-pgid, sig) == 0;

    /// <summary>Is anything left of this process group? Its leader's exit is not the group's: the engine's wrapper (cs2.sh) dies at a
    /// SIGTERM, a frozen engine under it does not, and it keeps the port: a stop that believed the wrapper would leave the next engine no
    /// port to answer on.</summary>
    public static bool GroupAlive(int pgid)
    {
        if (!OperatingSystem.IsLinux() || pgid <= 0)
            return false;
        if (KillCall(-pgid, 0) == 0)
            return true;
        return Marshal.GetLastPInvokeError() == Eperm;
    }

    /// <summary>Bytes free for an unprivileged user on the volume holding `path` (statvfs f_bavail * f_frsize), or null.</summary>
    public static unsafe long? DiskFree(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;
        var buf = stackalloc byte[256];                     // glibc x86_64 struct statvfs: 11 unsigned longs and 6 ints
        if (StatVfs(path, buf) != 0)
            return null;
        var frsize = *(ulong*)(buf + 8);
        var bavail = *(ulong*)(buf + 32);
        return (long)(frsize * bavail);
    }

    /// <summary>access(2) with W_OK: may this user write there? (Not on Linux: assumed.)</summary>
    public static bool MayWrite(string path) => !OperatingSystem.IsLinux() || Access(path, 2) == 0;

    /// <summary>fsync(2) on a folder, so a rename inside it survives a crash. Off Linux, and on a folder that will not open: nothing.</summary>
    public static void FsyncFolder(string folder)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var fd = Open(folder, 0);
        if (fd < 0)
            return;
        Fsync(fd);
        Close(fd);
    }
}
