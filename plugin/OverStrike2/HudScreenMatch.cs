using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The Game page's game-side half: the two restart buttons' second click, and the match length for the match that is on (../../bridge/MatchLength.cs says what it does;
/// the bridge's Restarts.cs and Program.SetMatchLength decide). Restart match itself needs nothing here: the bridge counts it down and sends mp_restartgame, and the game's next
/// round start reads mp_roundtime and starts the ladder, and catch-up's count, over.</summary>
sealed partial class HudScreen
{
    /// <summary>CCSGameRules.GamePhase while the match-end screen is up (GAMEPHASE_MATCH_ENDED).</summary>
    const int MatchEndedPhase = 5;

    readonly Dictionary<int, DateTime> rematchArmed = new(); // slot -> Restart match's second click counts until then

    void MatchLoad() => plugin.AddCommand(MatchLength.Command, "Over Strike 2: the match length, for the match that is on too: os_matchlength <minutes> (the bridge's !time)", OnMatchLengthCommand);

    /// <summary>A click on one of the restart buttons: its first click arms it (the bridge counts the same window), its second lets go - unless the state says the bridge will refuse it,
    /// when there is nothing to arm and the player's note says why.</summary>
    void ArmGameButtons(CCSPlayerController player, string buttonId, DateTime now)
    {
        if (buttonId is not (HudMenu.Restart or HudMenu.Rematch) || HudMenu.RestartRefused(buttonId, state, vote)) return;
        var armed = buttonId == HudMenu.Restart ? restartArmed : rematchArmed;
        if (armed.TryGetValue(player.Slot, out var until) && now < until) armed.Remove(player.Slot); else armed[player.Slot] = now.AddSeconds(HudBrowser.ConfirmSeconds);
    }

    static int ArmedSeconds(Dictionary<int, DateTime> armed, int slot, DateTime now) => armed.TryGetValue(slot, out var until) && now < until ? (int)Math.Ceiling((until - now).TotalSeconds) : 0;

    /// <summary>The Game page for this player: their buttons' two-click state.</summary>
    (List<(string Id, string Text)>, List<(string Id, string Class, bool On)>) GamePage(CCSPlayerController p, HudState shown, DateTime now) =>
        HudMenu.Game(shown, ArmedSeconds(restartArmed, p.Slot, now), ArmedSeconds(rematchArmed, p.Slot, now));

    void OnMatchLengthCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        if (!int.TryParse(command.GetArg(1), out var minutes) || minutes <= 0) { command.ReplyToCommand($"{MatchLength.Command}: <minutes>"); return; }
        var reply = SetRoundTime(minutes);
        log.LogInformation("Over Strike 2: {Reply}", reply);
        command.ReplyToCommand(reply);
    }

    /// <summary>The round that is on gets the new length, counted from its start; a length it is past, or nearly, ends it MatchLength.LeastSeconds from now. Only in Arms Race, whose one
    /// round is the match, and only while a match is on: warmup and the match-end screen have no round to set, and the next match's round start reads mp_roundtime, which the
    /// bridge has set by now.</summary>
    string SetRoundTime(int minutes)
    {
        if (!mode.ArmsRace) return MatchLength.Reply(MatchLength.Result.NotHere, $"{mode.Name} keeps its own, nothing set");
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
