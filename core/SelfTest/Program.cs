// The core library's self-test. Every check prints PASS or FAIL; the exit code is the number of failures (0: all passed).
//   dotnet run --project core/SelfTest              every check
//   dotnet run --project core/SelfTest -- --write   regenerate docs/settings.schema.json and docs/settings.md's key tables first
using OverStrike2.Core.SelfTest;
using OverStrike2.TestKit;

var root = T.Root();
if (root.Length == 0)
{
    Console.WriteLine("FAIL no core/OverStrike2.Core.csproj above " + Directory.GetCurrentDirectory() + ": run it from the repository");
    return 1;
}
if (args.Contains("--write"))
{
    File.WriteAllText(Path.Combine(root, "docs", "settings.schema.json"), SettingsDocs.JsonSchema());
    var md = Path.Combine(root, "docs", "settings.md");
    File.WriteAllText(md, SettingsDocs.WithKeys(File.ReadAllText(md), SettingsDocs.KeysMarkdown()));
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
    TailTests.Run();
}
finally
{
    T.Clean();
}
Console.WriteLine(T.Summary);
return T.Fails;
