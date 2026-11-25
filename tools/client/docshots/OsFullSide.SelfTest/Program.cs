// OsFullSide's self-test, offline (no game server): the plugin's type as CounterStrikeSharp loads it, its console command's registration, the rule osf_full
// writes by, and that the number it writes is the one Over Strike 2's SpawnRoom raises when a join is refused. Exit 0 when every check passes.
using System.Reflection;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using OsFullSide;

var failed = 0;
var checks = 0;
void Check(string what, bool ok)
{
    checks++;
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {what}");
}

string Repo(params string[] parts)
{
    var dir = AppContext.BaseDirectory;
    while (dir != null && !File.Exists(Path.Combine(dir, "AGENTS.md"))) dir = Path.GetDirectoryName(dir);
    return Path.Combine([dir ?? throw new InvalidOperationException("no repository above " + AppContext.BaseDirectory), .. parts]);
}

// ---- the plugin as CounterStrikeSharp loads it: a BasePlugin with a name, and its command by attribute, the server console's alone
var type = typeof(OsFullSide.OsFullSide);
// BasePlugin's constructor registers with the game server's native side, which is not here: its name is read off an instance made without it
var plugin = (BasePlugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
var method = type.GetMethods().SingleOrDefault(m => m.GetCustomAttribute<ConsoleCommandAttribute>() != null);
var command = method?.GetCustomAttribute<ConsoleCommandAttribute>();
var helper = method?.GetCustomAttribute<CommandHelperAttribute>();
var parms = method?.GetParameters().Select(p => p.ParameterType).ToArray();
Check($"the plugin loads as CounterStrikeSharp's loader takes it: a BasePlugin with a public constructor of no arguments, named {plugin.ModuleName} {plugin.ModuleVersion}",
    type.IsSubclassOf(typeof(BasePlugin)) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null && plugin.ModuleName == "OsFullSide" && plugin.ModuleVersion == "1.0.0");
Check($"its one console command is {command?.Command}, from the server console and RCON alone ({helper?.WhoCanExcecute}), with no argument or one",
    command?.Command == OsFullSide.OsFullSide.Command && OsFullSide.OsFullSide.Command == "osf_full" && helper is { WhoCanExcecute: CommandUsage.SERVER_ONLY, MinArgs: 0 }
    && parms is [var a0, var a1] && a0 == typeof(CCSPlayerController) && a1 == typeof(CommandInfo));
Check("...and only one plugin and one command in the assembly, so loading it adds nothing else",
    type.Assembly.GetTypes().Count(t => t.IsSubclassOf(typeof(BasePlugin))) == 1
    && type.Assembly.GetTypes().SelectMany(t => t.GetMethods()).Count(m => m.GetCustomAttribute<ConsoleCommandAttribute>() != null) == 1);

// ---- built against what the game server runs: the CounterStrikeSharp os2-server installs
var install = File.ReadAllText(Repo("server", "OverStrike2.Server", "Install.cs"));
var csproj = File.ReadAllText(Repo("tools", "client", "docshots", "OsFullSide", "OsFullSide.csproj"));
var pinned = Regex.Match(install, "public const string CssVersion = \"([0-9.]+)\";").Groups[1].Value;
Check($"built against the CounterStrikeSharp.API the server runs ({pinned}, server/OverStrike2.Server/Install.cs), never loaded from the plugins folder",
    pinned != "" && csproj.Contains($"Include=\"CounterStrikeSharp.API\" Version=\"{pinned}\"", StringComparison.Ordinal)
    && csproj.Contains("<ExcludeAssets>runtime</ExcludeAssets>", StringComparison.Ordinal));

// ---- the rule: a side full of its humans refuses the next human join; SpawnRoom's room (one more than it holds) lets it through
var ct = FullSideRule.SideOf("ct");
var t = FullSideRule.SideOf("T");
Check("ct and t name the sides (3 and 2 too), as the game's teams and their spawn point classes; anything else names none",
    ct is { Team: CsTeam.CounterTerrorist, Class: "info_player_counterterrorist", Name: "CT" } && t is { Team: CsTeam.Terrorist, Class: "info_player_terrorist", Name: "T" }
    && FullSideRule.SideOf("3") == ct && FullSideRule.SideOf("2") == t && FullSideRule.SideOf("spec") == null && FullSideRule.SideOf("") == null);
Check("osf_full puts a side's count at its humans: none on it - the lab's one client on the other side - is a count of 0, two is 2",
    FullSideRule.FullCount(0) == 0 && FullSideRule.FullCount(2) == 2 && FullSideRule.FullCount(-1) == 0);
Check("...so the next human join to it is refused, as on a side full of humans: 0 on a count of 0, 2 on a count of 2 (Wingman's two a side)",
    FullSideRule.Refused(0, 0, FullSideRule.FullCount(0)) && FullSideRule.Refused(2, 0, FullSideRule.FullCount(2)));
Check("...and once SpawnRoom has raised the count to its spawn points on, one more than the side holds or more, the next try goes through",
    !FullSideRule.Refused(0, 0, 1) && !FullSideRule.Refused(2, 0, 3) && !FullSideRule.Refused(0, 0, 2));
Check("...a bot on the side leaves instead of the join being refused (bot_auto_vacate, on by default): osf_full then says to kick the side's bots first",
    !FullSideRule.Refused(0, 1, FullSideRule.FullCount(0)) && FullSideRule.Refused(0, 1, 0, botLeaves: false)
    && FullSideRule.Next(0, 1, 0).Contains("kick the side's bots first", StringComparison.Ordinal)
    && FullSideRule.Next(0, 0, 0).Contains("refused", StringComparison.Ordinal) && FullSideRule.Next(0, 0, 1).Contains("goes through", StringComparison.Ordinal));

// ---- the refusal goes through the real handler: osf_full writes the very number SpawnRoom raises, and SpawnRoom answers jointeam_failed with its room
var mine = File.ReadAllText(Repo("tools", "client", "docshots", "OsFullSide", "OsFullSide.cs"));
var room = File.ReadAllText(Repo("plugin", "OverStrike2", "SpawnRoom.cs"));
const string field = "ref var count = ref (side.Team == CsTeam.Terrorist ? ref rules.SpawnPointCount_Terrorist : ref rules.SpawnPointCount_CT);";
Check("osf_full writes the game's spawn point count of the side - CCSGameRules.SpawnPointCount_Terrorist or _CT, the field SpawnRoom reads and raises - and nothing else",
    mine.Contains(field, StringComparison.Ordinal) && mine.Contains("count = FullSideRule.FullCount(humans);", StringComparison.Ordinal)
    && room.Contains("ref var count = ref (team == CsTeam.Terrorist ? ref rules.SpawnPointCount_Terrorist : ref rules.SpawnPointCount_CT);", StringComparison.Ordinal)
    && room.Contains("if (count < on) { count = on; raised = true; }", StringComparison.Ordinal)
    && Regex.Matches(mine, @"^\s*count = ", RegexOptions.Multiline).Count == 1 && !mine.Contains("AcceptInput", StringComparison.Ordinal) && !mine.Contains("CreateEntityByName", StringComparison.Ordinal));
Check("...and the refusal is answered by SpawnRoom's own handler: jointeam_failed, then its room on the next frame, logged as \"spawn room (a join was refused)\" - in Wingman",
    room.Contains("plugin.RegisterEventHandler<EventJointeamFailed>((_, _) => { Server.NextFrame(() => Make(\"a join was refused\")); return HookResult.Continue; });", StringComparison.Ordinal)
    && room.Contains("\"Over Strike 2 spawn room ({Why}): {Side} holds {OnTeam}", StringComparison.Ordinal)
    && room.Contains("if (!always && modeId() != Modes.WingmanId) return;", StringComparison.Ordinal));
Check("...which OsFullSide only watches: its own handler of the event lets it go on, and logs the sides then and two frames on, after SpawnRoom's room",
    mine.Contains("RegisterEventHandler<EventJointeamFailed>", StringComparison.Ordinal) && mine.Contains("return HookResult.Continue;", StringComparison.Ordinal)
    && mine.Contains("Server.NextFrame(() => Server.NextFrame(", StringComparison.Ordinal) && !mine.Contains("HookResult.Handled", StringComparison.Ordinal)
    && !mine.Contains("HookResult.Stop", StringComparison.Ordinal));

Console.WriteLine(failed == 0 ? $"OsFullSide self-test: {checks} checks passed." : $"OsFullSide self-test: {failed} of {checks} checks FAILED.");
return failed == 0 ? 0 : 1;
