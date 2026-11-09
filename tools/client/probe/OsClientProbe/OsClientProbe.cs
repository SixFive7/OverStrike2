using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace OsClientProbe;

/// <summary>Test-only server half of the CS2 client test harness (tools/client/README.md). It is never one of the server's
/// plugins: os2-deploy probe copies the DLL to /tmp/osprobe in the game container, loads it with `css_plugins load` for one test, then unloads
/// it and deletes it. Every command is server console / RCON only and acts on the players named, nobody else.
///
///   osprobe_players                                   who is on: slot, name, account id, alive, team
///   osprobe_html64 &lt;account|*&gt; &lt;seconds&gt; &lt;base64url&gt;   a centre-screen HTML message (PrintToCenterHtml), re-sent every tick for
///                                                     that long the way CounterStrikeSharp's centre menu does - one send lasts
///                                                     only a few seconds. base64url keeps quotes and semicolons (an entity's ;
///                                                     would end the console command) out of the command line.
///   osprobe_htmlfile &lt;account|*&gt; &lt;seconds&gt; &lt;path&gt;   the same, the HTML read from a file under /tmp/osprobe/ (longer than a console line)
///   osprobe_stop                                      clears every message this plugin is showing
///   osprobe_clientcmd &lt;account&gt; &lt;command...&gt;          ExecuteClientCommand: the client runs it only if the command is marked
///                                                     FCVAR_CLIENT_CAN_EXECUTE (few are)</summary>
public class OsClientProbe : BasePlugin
{
    public override string ModuleName => "OsClientProbe";
    public override string ModuleVersion => "1.1.0";

    sealed record Shown(int Slot, string Html, DateTime Until);
    readonly List<Shown> shown = new();

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnTick>(OnTick);
        Logger.LogInformation("OsClientProbe {Version} loaded (test harness probe; unload it after the test)", ModuleVersion);
    }

    void OnTick()
    {
        if (shown.Count == 0) return;
        var now = DateTime.UtcNow;
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            var s = shown[i];
            var player = Utilities.GetPlayerFromSlot(s.Slot);
            if (player is not { IsValid: true }) { shown.RemoveAt(i); continue; }
            if (now >= s.Until) { player.PrintToCenterHtml(" "); shown.RemoveAt(i); continue; }
            player.PrintToCenterHtml(s.Html);
        }
    }

    static string AccountId(ulong steamId) => (steamId & 0xFFFFFFFF).ToString();

    static List<CCSPlayerController> Targets(string who) => Utilities.GetPlayers()
        .Where(p => p is { IsValid: true, IsBot: false } && (who == "*" || AccountId(p.SteamID) == who || p.SteamID.ToString() == who))
        .ToList();

    [ConsoleCommand("osprobe_players", "OsClientProbe: who is on")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnPlayers(CCSPlayerController? caller, CommandInfo command)
    {
        var players = Utilities.GetPlayers();
        foreach (var p in players)
            command.ReplyToCommand($"osprobe: slot {p.Slot} '{p.PlayerName}' account {AccountId(p.SteamID)} bot {p.IsBot} alive {p.PawnIsAlive} team {p.TeamNum}");
        command.ReplyToCommand($"osprobe: {players.Count} player(s), {shown.Count} message(s) showing");
    }

    [ConsoleCommand("osprobe_html64", "OsClientProbe: osprobe_html64 <account|*> <seconds> <base64url html>")]
    [CommandHelper(minArgs: 3, usage: "<account|*> <seconds> <base64url html>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnHtml(CCSPlayerController? caller, CommandInfo command)
    {
        string html;
        try
        {
            var b64 = command.GetArg(3).Replace('-', '+').Replace('_', '/');
            html = Encoding.UTF8.GetString(Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=')));
        }
        catch (FormatException) { command.ReplyToCommand("osprobe_html64: the third argument is not base64url"); return; }
        if (!int.TryParse(command.GetArg(2), out var seconds) || seconds < 1 || seconds > 600) { command.ReplyToCommand("osprobe_html64: seconds 1-600"); return; }
        var targets = Targets(command.GetArg(1));
        if (targets.Count == 0) { command.ReplyToCommand($"osprobe_html64: no human player {command.GetArg(1)} on the server"); return; }
        var until = DateTime.UtcNow.AddSeconds(seconds);
        foreach (var p in targets)
        {
            shown.RemoveAll(s => s.Slot == p.Slot);
            shown.Add(new Shown(p.Slot, html, until));
        }
        command.ReplyToCommand($"osprobe_html64: {html.Length} chars to {string.Join(", ", targets.Select(p => p.PlayerName))} for {seconds} s");
        Logger.LogInformation("osprobe_html64: {Length} chars to {Who} for {Seconds} s: {Html}", html.Length, command.GetArg(1), seconds, html);
    }

    /// <summary>The same from a file in the container (os2-deploy probe htmlfile uploads it): a message longer than a console line can carry.</summary>
    [ConsoleCommand("osprobe_htmlfile", "OsClientProbe: osprobe_htmlfile <account|*> <seconds> <path in the container>")]
    [CommandHelper(minArgs: 3, usage: "<account|*> <seconds> <path>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnHtmlFile(CCSPlayerController? caller, CommandInfo command)
    {
        var path = command.GetArg(3);
        if (!path.StartsWith("/tmp/osprobe/") || path.Contains("..") || !File.Exists(path)) { command.ReplyToCommand($"osprobe_htmlfile: no file {path} under /tmp/osprobe/"); return; }
        var html = File.ReadAllText(path, Encoding.UTF8);
        if (!int.TryParse(command.GetArg(2), out var seconds) || seconds < 1 || seconds > 600) { command.ReplyToCommand("osprobe_htmlfile: seconds 1-600"); return; }
        var targets = Targets(command.GetArg(1));
        if (targets.Count == 0) { command.ReplyToCommand($"osprobe_htmlfile: no human player {command.GetArg(1)} on the server"); return; }
        var until = DateTime.UtcNow.AddSeconds(seconds);
        foreach (var p in targets) { shown.RemoveAll(s => s.Slot == p.Slot); shown.Add(new Shown(p.Slot, html, until)); }
        command.ReplyToCommand($"osprobe_htmlfile: {html.Length} chars ({Encoding.UTF8.GetByteCount(html)} bytes) to {string.Join(", ", targets.Select(p => p.PlayerName))} for {seconds} s");
    }

    [ConsoleCommand("osprobe_stop", "OsClientProbe: clear every message this plugin shows")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnStop(CCSPlayerController? caller, CommandInfo command)
    {
        foreach (var s in shown) { var p = Utilities.GetPlayerFromSlot(s.Slot); if (p is { IsValid: true }) p.PrintToCenterHtml(" "); }
        command.ReplyToCommand($"osprobe_stop: cleared {shown.Count}");
        shown.Clear();
    }

    [ConsoleCommand("osprobe_clientcmd", "OsClientProbe: osprobe_clientcmd <account> <command...> (only FCVAR_CLIENT_CAN_EXECUTE commands run)")]
    [CommandHelper(minArgs: 2, usage: "<account> <command...>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnClientCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var targets = Targets(command.GetArg(1));
        if (targets.Count != 1) { command.ReplyToCommand($"osprobe_clientcmd: {targets.Count} players match {command.GetArg(1)}, need exactly 1"); return; }
        var rest = command.ArgString.Trim();
        rest = rest.Substring(rest.IndexOf(' ') + 1).Trim().Trim('"');
        targets[0].ExecuteClientCommand(rest);
        command.ReplyToCommand($"osprobe_clientcmd: sent '{rest}' to {targets[0].PlayerName}");
    }
}
