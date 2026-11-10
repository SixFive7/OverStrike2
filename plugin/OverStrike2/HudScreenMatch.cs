using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The Game page's game-side half: the two restart buttons' second click, and the match length for the match that is on (../../bridge/Shared/MatchLength.cs says what it does;
/// the bridge's Restarts.cs and Program.SetMatchLength decide): the one round's time in Arms Race and Deathmatch, Wingman's count of rounds (release 199). Restart match itself needs nothing here: the bridge counts it down and sends os_restart (GameSettingsRestart.cs), and the game's next
/// round start reads mp_roundtime and starts the ladder, and catch-up's count, over.</summary>
sealed partial class HudScreen
{
    /// <summary>CCSGameRules.GamePhase while the match-end screen is up (GAMEPHASE_MATCH_ENDED).</summary>
    const int MatchEndedPhase = 5;

    void MatchLoad() => plugin.AddCommand(MatchLength.Command, "Over Strike 2: the match length, for the match that is on too: os_matchlength <minutes, or Wingman's rounds> (the bridge's !time)", OnMatchLengthCommand);

    /// <summary>Wingman's match length (release 199): how many rounds its match has.</summary>
    const string MaxRounds = "mp_maxrounds";

    /// <summary>A click on one of the restart buttons that goes out to the bridge: its first click arms it (the bridge counts the same window), its second lets go - unless the state says
    /// the bridge will refuse it, when there is nothing to arm and the player's note says why. The second click of a double click goes nowhere (HudBrowser.DoubleClick) and changes
    /// nothing here either: the button stays armed, as the bridge's own confirmation does.</summary>
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
        if (!int.TryParse(command.GetArg(1), out var length) || length <= 0) { command.ReplyToCommand($"{MatchLength.Command}: <minutes, or Wingman's rounds>"); return; }
        var reply = MatchLength.InRounds(mode.Id) ? SetMaxRounds(length) : SetRoundTime(length);
        log.LogInformation("Over Strike 2: {Reply}", reply);
        command.ReplyToCommand(reply);
    }

    /// <summary>The round that is on gets the new length, counted from its start; a length it is past, or nearly, ends it MatchLength.LeastSeconds from now. Only in Arms Race and
    /// Deathmatch, whose one round is the match (release 197), and only while a match is on: the match-end screen has no round to set, and the next match's round start reads
    /// mp_roundtime, which the bridge has set by now. Neither mode warms up (their cfgs' mp_warmup_online_enabled 0): the match is live from the first team pick, so !time takes
    /// it at once - a warmup still answers NoMatch, should a cfg switch it back on.</summary>
    string SetRoundTime(int minutes)
    {
        if (!MatchLength.OneRound(mode.Id)) return MatchLength.Reply(MatchLength.Result.NotHere, $"{mode.Name} has no one round, nothing set");
        if (!worldReady) return MatchLength.Reply(MatchLength.Result.NoMatch, "no round has started on this map"); // no entity before the first round start (the class summary)
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (proxy is not { IsValid: true } || proxy.GameRules is not { } rules) return MatchLength.Reply(MatchLength.Result.NoMatch, "no game rules");
        if (rules.WarmupPeriod || rules.GamePhase == MatchEndedPhase) return MatchLength.Reply(MatchLength.Result.NoMatch, rules.WarmupPeriod ? "a warmup (the cfg switched it on)" : "the match has ended");
        var played = Server.CurrentTime - rules.RoundStartTime;
        var seconds = MatchLength.RoundSeconds(minutes, played);
        var before = rules.RoundTime;
        rules.RoundTime = seconds;
        NetworkGameRules(proxy);
        return MatchLength.Reply(MatchLength.Of(minutes, played), $"the round's time goes from {before} s to {seconds} s, {played:0} s played, {minutes} minutes asked");
    }

    /// <summary>Wingman's count of rounds for the match that is on (release 199). Its sides switch at half the count, so the match takes the new one only while neither
    /// count's halftime has come (MatchLength.ThisMatch); otherwise nothing is set here, and the bridge sets it when the next match starts. With no match on - Wingman's
    /// warmup (should its cfg switch it on again), the match-end screen, a map with no round started yet - it is set now, for the match that comes.</summary>
    string SetMaxRounds(int rounds)
    {
        if (ConVarKeeper.Find(MaxRounds) is not { } cv) return MatchLength.Reply(MatchLength.Result.NotHere, $"this build has no {MaxRounds}, nothing set");
        var current = int.TryParse(cv.StringValue, out var n) ? n : 0;
        string Take(MatchLength.Result result, string why)
        {
            cv.StringValue = rounds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return MatchLength.Reply(result, $"{MaxRounds} {current} -> {rounds}: {why}");
        }
        if (!worldReady) return Take(MatchLength.Result.NoMatch, "no round has started on this map");
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (proxy is not { IsValid: true } || proxy.GameRules is not { } rules) return Take(MatchLength.Result.NoMatch, "no game rules");
        if (rules.WarmupPeriod || rules.GamePhase == MatchEndedPhase) return Take(MatchLength.Result.NoMatch, rules.WarmupPeriod ? "warmup" : "the match has ended");
        var played = rules.TotalRoundsPlayed;
        if (MatchLength.ThisMatch(played, current, rounds)) return Take(MatchLength.Result.Set, $"{played} round(s) played, before the halftime of either count");
        return MatchLength.Reply(MatchLength.Result.Later, $"{played} round(s) played, a halftime of {current} or {rounds} has come: {MaxRounds} stays {current} for this match");
    }

    /// <summary>A field written here reaches the clients only when it is marked: the round timer on everybody's screen goes by it. The server's own round end reads the field as it is, so a
    /// failure to mark it costs the timer on screen, not the match's end.</summary>
    void NetworkGameRules(CCSGameRulesProxy proxy)
    {
        try { Utilities.SetStateChanged(proxy, "CCSGameRulesProxy", "m_pGameRules"); }
        catch (Exception e) { log.LogWarning("Over Strike 2: the round's time is set, but marking it for the network failed: {Message}", e.Message); }
    }
}
