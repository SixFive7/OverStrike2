using static OverStrike2.Core.SelfTest.T;

namespace OverStrike2.Core.SelfTest;

/// <summary>Files.WriteAtomic: a file written whole, also when something holds the target for a moment, as a scanner does on Windows.</summary>
static class FilesTests
{
    public static void Run()
    {
        var path = Temp("atomic.txt");
        Files.WriteAtomic(path, "one");
        Check("files: a file written whole, nothing left beside it",
              File.ReadAllText(path) == "one" && Directory.GetFiles(Path.GetDirectoryName(path)!, ".atomic*").Length == 0);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var letGo = Task.Run(async () =>
        {
            await Task.Delay(300);
            held.Dispose();
        });
        Check("files: a target something holds for a moment is replaced once it lets go", () =>
        {
            Files.WriteAtomic(path, "two");
            return File.ReadAllText(path) == "two";
        });
        letGo.Wait();
        var folder = Temp("a-folder");
        Directory.CreateDirectory(folder);
        try
        {
            Files.WriteAtomic(folder, "three");
            Check("files: a real failure still throws", false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Check("files: a real failure still throws", true);
        }
    }
}
