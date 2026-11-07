using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace OverStrike2;

/// <summary>The Bots page's clicks (FUN, Bots): what it shows every frame is HudFrame's (../../bridge/Shared/HudBots.cs), what a click on it comes to HudBots.Clicked's. A row's
/// choice is a fun change like the Fun page's (FunChange: the settings module makes it a frame later, and one message says who switched what); a preset is several, made in one
/// frame in Bots.Plan's order with one message for all of them (Messages.BotsPreset); Kick all bots and Default are the settings module's own (BotsReset; Messages.BotsOff). A
/// choice the mode that is on cannot make, and a preset whose bots do not fit, is refused with its reason in a bubble at that button, as the Fun page refuses a clash. A choice
/// that names its player (Copycats) is made for the player who clicked it (Bots.For).</summary>
sealed partial class HudScreen
{
    readonly FunClicks botClicks = new(); // the Bots page's refused clicks: that button's bubble and its row's line, as the Fun page's (HudFrame draws both)

    /// <summary>A bot setting changed as part of a preset: the settings module's change without the bots joining again (GameSettings.ChangeFun, rejoin false), and then once
    /// for the whole preset (BotsRejoin). Set at its load.</summary>
    public Func<Fun.Setting, Fun.Choice, string, string>? BotsSet { get; set; }

    /// <summary>The bots join again, to take what they take as they join (difficulty, name tag, weapon rules): the settings module's (GameSettings.RejoinBots). Set at its load.</summary>
    public Action? BotsRejoin { get; set; }

    /// <summary>Kick all bots or Default (Bots.KickWord, Bots.DefaultWord), in the name of who asked: the settings module's (GameSettings.ResetBots). Set at its load.</summary>
    public Func<string, string, string>? BotsReset { get; set; }

    /// <summary>A click in the window, if it is one on the Bots page.</summary>
    bool BotsClicked(CCSPlayerController player, string buttonId, DateTime now)
    {
        if (HudBots.Clicked(buttonId) is not { } click) return false;
        var (slot, account) = (player.Slot, Account(player));
        if (click.All is { } all)
        {
            if (all == Bots.KickWord && Bots.Quota(FunValue) == 0) BotsRefuse(slot, account, buttonId, null, M.Refused("No bot plays: nothing to kick"));
            else BotsAll(player, all);
        }
        else if (click.Preset is { } preset)
        {
            if (Bots.Unavailable(preset, mode) is { } why) BotsRefuse(slot, account, buttonId, null, M.Refused($"{M.A(preset.Title)}: {why}"));
            else BotsPreset(player, preset);
        }
        else if (click is { Setting: { } setting, Choice: { } choice } && Fun.Showing(setting, FunValue).Id != choice.Id) // a lit choice: nothing to do
        {
            if (Bots.Unavailable(setting, choice, mode) is { } why) BotsRefuse(slot, account, buttonId, setting, M.Refused($"{M.A(setting.Title)} cannot be {M.V(choice.Label)} here: {why}"));
            else FunChange(player, setting, Bots.For(choice, slot), line => BotsRefuse(slot, account, buttonId, setting, line));
        }
        Server.NextFrame(Frame);
        return true;
    }

    /// <summary>A click refused: the bubble at that button and its row's line say why for a while (a preset's and a top button's: its own words), and that player's chat and board.</summary>
    void BotsRefuse(int slot, long account, string button, Fun.Setting? setting, string line)
    {
        var row = setting == null ? 0 : Array.IndexOf(Bots.Settings, setting) + 1;
        botClicks.Refuse(slot, button, row > 0 ? HudBots.WhyVar(row) : "", line, DateTime.UtcNow);
        Post(Messages.FunRefused, line, account);
    }

    /// <summary>A preset: every bot setting as Bots.Plan has it in the mode that is on - its picks, the rest back to Normal, the count last so the bots join with the rest set -
    /// made a frame later, as a row's change is; the bots that played before it join again once if it changed what they take as they join; one message says who did it.</summary>
    void BotsPreset(CCSPlayerController player, Bots.Preset preset)
    {
        var (slot, who) = (player.Slot, Hud.Clean(player.PlayerName, 32));
        Server.NextFrame(() =>
        {
            if (Bots.Plan(preset, mode) is not { } plan) return;
            var before = Bots.Quota(FunValue);
            var changed = plan.Where(x => Fun.Showing(x.Setting, FunValue).Id != x.Choice.Id).ToList();
            foreach (var (setting, choice) in changed) BotsSet?.Invoke(setting, Bots.For(choice, slot), who);
            if (before > 0 && changed.Any(x => Bots.Rejoins(x.Setting))) BotsRejoin?.Invoke();
            Post(Messages.BotsPreset, Bots.PresetLine(who, preset, plan.Where(x => Fun.Showing(x.Setting, FunValue).Id == x.Choice.Id).ToList(), mode)); // what landed
            Frame();
        });
    }

    /// <summary>Kick all bots (every bot leaves, the two counts back to Off) or Default (every bot setting back to normal, the bots gone with them), a frame later.</summary>
    void BotsAll(CCSPlayerController player, string which)
    {
        var who = Hud.Clean(player.PlayerName, 32);
        Server.NextFrame(() =>
        {
            BotsReset?.Invoke(which, who);
            Post(Messages.BotsOff, Bots.OffLine(who, which));
            Frame();
        });
    }
}
