namespace OverStrike2.Sync;

/// <summary>File modes the sync writes with: its own files 0644, the bridge's 0666.</summary>
public static class FileModes
{
    public const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    public const UnixFileMode Shared = Private | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
}
