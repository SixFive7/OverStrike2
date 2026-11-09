using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace OsDocName;

/// <summary>Throwaway test plugin for the documentation's screenshots (os2-deploy docshots name): loaded from /tmp on the INSECURE TEST COPY only, never copied to the plugins folder and gone with the copy.
/// The pictures are public, so the test client plays under a test name: the server's own record of that player's name, every quarter of a second, so that what the game shows - the scoreboard, chat -
/// and what Over Strike 2 reads and says says that name. It changes nothing on the client and nothing of the game's rules. Server console only:
///
///   osd_name &lt;account id&gt; &lt;name&gt;    that player goes by that name from now on, also after a reconnect or a map change
///   osd_name &lt;account id&gt;           no longer
///   osd_name                        who goes by what, and what the server has for them now</summary>
public class OsDocName : BasePlugin
{
    public override string ModuleName => "OsDocName";
    public override string ModuleVersion => "1.0.0";

    readonly Dictionary<ulong, string> names = new();

    public override void Load(bool hotReload) => AddTimer(0.25f, Apply, TimerFlags.REPEAT);

    void Apply()
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, IsBot: false, IsHLTV: false } || !names.TryGetValue(p.SteamID & 0xFFFFFFFF, out var name) || p.PlayerName == name) continue;
            p.PlayerName = name;
            Utilities.SetStateChanged(p, "CBasePlayerController", "m_iszPlayerName");
        }
    }

    [ConsoleCommand("osd_name", "OsDocName: osd_name <account id> [<name>]: that player goes by a test name")]
    [CommandHelper(minArgs: 0, usage: "[<account id> [<name>]]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnName(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount >= 2 && ulong.TryParse(command.GetArg(1), out var account))
        {
            if (command.ArgCount >= 3 && command.GetArg(2) != "") names[account] = command.GetArg(2);
            else names.Remove(account);
            Apply();
        }
        command.ReplyToCommand("osd_name: " + string.Join(", ", names.Select(n => $"{n.Key} goes by {n.Value}")) + "; on the server: "
            + string.Join(", ", Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }).Select(p => $"{p.SteamID & 0xFFFFFFFF} is {(names.ContainsKey(p.SteamID & 0xFFFFFFFF) ? p.PlayerName : "(another)")}")));
    }
}
