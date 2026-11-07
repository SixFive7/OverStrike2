// The server program's self-test: the restart watcher's decisions, its scenarios (the real Watch loop on a fake clock with a fake engine
// and a fake bridge), the maps folder, the hand-over and the deletion, the background downloads and the download line, and the plugin layer's
// install.
// Every check prints PASS, FAIL or SKIP; the exit code is the number of failures (0: all passed).
//   dotnet run --project server/OverStrike2.Server/SelfTest
// The checks on links, /proc and a folder's fsync need Linux and say SKIP elsewhere. The run that counts is the one in the game image:
//   dotnet publish server/OverStrike2.Server/SelfTest -c Release -r linux-x64 -o <dir>, then run <dir>/OverStrike2.Server.SelfTest
//   inside the image (it needs nothing of the game).
using OverStrike2.Server.SelfTest;
using OverStrike2.TestKit;

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
Console.WriteLine(T.Summary);
return T.Fails;
