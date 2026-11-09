using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The Game page's game-side half: the two restart buttons' second click, and the match length for the match that is on (../../bridge/Shared/MatchLength.cs says what it does;
/// the bridge's Restarts.cs and Program.SetMatchLength decide). Restart match itself needs nothing here: the bridge counts it down and sends os_restart (GameSettingsRestart.cs), and the game's next
/// round start reads mp_roundtime and starts the ladder, and catch-up's count, over.</summary>
sealed partial class HudScreen
{
    /// <summary>CCSGameRules.GamePhase while the match-end screen is up (GAMEPHASE_MATCH_ENDED).</summary>
    const int MatchEndedPhase = 5;

    void MatchLoad() => plugin.AddCommand(MatchLength.Command, "Over Strike 2: the match length, for the match that is on too: os_matchlength <minutes> (the bridge's !time)", OnMatchLengthCommand);

    /// <summary>A click on one of the restart buttons: its first click arms it (the bridge counts the same window), its second lets go - unless the state says the bridge will refuse it,
    /// when there is nothing to arm and the player's note says why.</summary>
    void ArmGameButtons(CCSPlayerController player, string buttonId, DateTime now)
    {
        if (buttonId is not (HudMenu.Restart or HudMenu.Rematch) || HudMenu.RestartRefused(buttonId, state, vote)) return;
        var me = Player(player.Slot);
        var until = buttonId == HudMenu.Restart ? me.RestartArmed : me.RematchArmed;
        until = now < until ? default : now.AddSeconds(HudBrowser.ConfirmSeconds); // the second click lets go of it (HudFrame shows the Game page's two-click state)
        if (buttonId == HudMenu.Restart) me.RestartArmed = until; else me.RematchArmed = until;
    }

    void OnMatchLengthCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        if (!int.TryParse(command.GetArg(1), out var minutes) || minutes <= 0) { command.ReplyToCommand($"{MatchLength.Command}: <minutes>"); return; }
        var reply = SetRoundTime(minutes);
        log.LogInformation("Over Strike 2: {Reply}", reply);
        command.ReplyToCommand(reply);
    }

    /// <summary>The round that is on gets the new length, counted from its start; a length it is past, or nearly, ends it MatchLength.LeastSeconds from now. Only in Arms Race and
    /// Deathmatch, whose one round is the match (release 197), and only while a match is on: warmup and the match-end screen have no round to set, and the next match's round
    /// start reads mp_roundtime, which the bridge has set by now.</summary>
    string SetRoundTime(int minutes)
    {
        if (!MatchLength.Applies(mode.Id)) return MatchLength.Reply(MatchLength.Result.NotHere, $"{mode.Name} plays a match of rounds, nothing set");
        if (!worldReady) return MatchLength.Reply(MatchLength.Result.NoMatch, "no round has started on this map"); // no entity before the first round start (the class summary)
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (proxy is not { IsValid: true } || proxy.GameRules is not { } rules) return MatchLength.Reply(MatchLength.Result.NoMatch, "no game rules");
        if (rules.WarmupPeriod || rules.GamePhase == MatchEndedPhase) return MatchLength.Reply(MatchLength.Result.NoMatch, rules.WarmupPeriod ? "warmup" : "the match has ended");
        var played = Server.CurrentTime - rules.RoundStartTime;
        var seconds = MatchLength.RoundSeconds(minutes, played);
        var before = rules.RoundTime;
        rules.RoundTime = seconds;
        NetworkGameRules(proxy);
        return MatchLength.Reply(MatchLength.Of(minutes, played), $"the round's time goes from {before} s to {seconds} s, {played:0} s played, {minutes} minutes asked");
    }

    /// <summary>A field written here reaches the clients only when it is marked: the round timer on everybody's screen goes by it. The server's own round end reads the field as it is, so a
    /// failure to mark it costs the timer on screen, not the match's end.</summary>
    void NetworkGameRules(CCSGameRulesProxy proxy)
    {
        try { Utilities.SetStateChanged(proxy, "CCSGameRulesProxy", "m_pGameRules"); }
        catch (Exception e) { log.LogWarning("Over Strike 2: the round's time is set, but marking it for the network failed: {Message}", e.Message); }
    }
}
