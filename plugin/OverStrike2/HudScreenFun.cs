using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace OverStrike2;

/// <summary>The Fun page's clicks: what it shows every frame is HudFrame's (../../bridge/Shared/HudMenu.cs: FunPage, FunWhy), what a click on it comes to FunClicks' (../../bridge/Shared/FunClicks.cs), the same
/// change typed in chat, and Random and Default. The settings module makes every change, in process, a frame after the click or the line (FunSet, FunReset; os_fun does the same from the
/// console): it owns the server's settings - the baseline, the put-back at the map's end and the block a player's change is exempt from; the grenade drops and catch-up are fun settings like
/// the rest, its effects. Once the value is there one message says who switched what, the same for every setting (Messages.FunChanged, FunRandom, FunDefault: the top line, chat and the TAB
/// board, for everybody). A setting's "Set by" stays on the page: the message said it. One plugin drives every content: a content before v4.6.0 has sixteen rows and two fixed ones, greys
/// nothing and has no bubble, so its note says what this content's page shows.</summary>
sealed partial class HudScreen
{
    readonly Random dice = new();                         // the Fun page's Random
    readonly FunClicks funClicks = new();                 // which choices are grey since when, and each player's refused click (HudFrame draws both)

    /// <summary>Where the fun page's values come from: the settings module (convars and its effects). Set at the plugin's load.</summary>
    public Func<string, string?>? FunSource { get; set; }

    /// <summary>Who set each fun setting that is off its default, by the setting's id: the settings module's word (os_fun's last argument); null
    /// while nobody is known. The settings module sets it at its load.</summary>
    public Func<string, string?>? FunSetBy { get; set; }

    /// <summary>Makes a fun change now, in the name of who asked: the settings module's own (Settings.ChangeFun, which os_fun runs too). Set at its load.</summary>
    public Func<Fun.Setting, Fun.Choice, string, string>? FunSet { get; set; }

    /// <summary>Every player change undone and every effect at its default, in the name of who asked: the settings module's (Settings.ResetFun). Set at its load.</summary>
    public Func<string, string>? FunReset { get; set; }

    /// <summary>A fun setting's convar (or effect) as the server has it now (Fun.cs), for the page and for checking a change landed.</summary>
    string? FunValue(string name) => FunSource?.Invoke(name);

    /// <summary>A click in the window, if it is one on the Fun page: Random, Default, or a choice - of the grid, or of an older content's fixed rows. Nothing else is asked of a choice that is lit, or
    /// that has been grey long enough to be seen: its look and its bubble have said it.</summary>
    bool FunClicked(CCSPlayerController player, string buttonId, DateTime now)
    {
        if (buttonId is HudMenu.FunRandom or HudMenu.FunDefault)
        {
            FunAll(player, buttonId == HudMenu.FunRandom ? Fun.RandomWord : Fun.DefaultWord);
            Server.NextFrame(Frame);
            return true;
        }
        if (HudMenu.FunClick(buttonId) is not { } fun) return false;
        var (setting, choice) = fun;
        var (slot, account) = (player.Slot, Account(player));
        var older = HudVersion.IsBefore(VersionOf(account), FunClicks.BubbleLevel);
        if (!mode.ArmsRace && Fun.ArmsRaceOnly(setting)) FunRefuse(slot, account, buttonId, setting, ChatReply.NotInMode(setting, mode.Name)); // a page from before the mode changed: its row is gone now
        else
            switch (funClicks.Click(buttonId, setting, choice, FunValue, now))
            {
                case FunClicks.Verdict.Go:
                    FunChange(player, setting, choice, line => FunRefuse(slot, account, buttonId, setting, line));
                    return true;
                case FunClicks.Verdict.Refused:
                    FunRefuse(slot, account, buttonId, setting, Fun.RefusedJust(setting, choice, FunValue, FunSetBy)!);
                    break;
                case FunClicks.Verdict.Seen when older:
                    FunRefuse(slot, account, buttonId, setting, Fun.RefusalLine(setting, choice, FunValue)!);
                    break;
                case FunClicks.Verdict.Lit when older:
                    Post(Messages.Answer, ChatReply.Already(setting, choice), account);
                    break;
            }
        Server.NextFrame(Frame);
        return true;
    }

    /// <summary>A click refused: the bubble at that button says why for a while, and that player's chat and board. A content before v4.6.0 has no bubble: the answer's own line, its note. line: marked (M).</summary>
    void FunRefuse(int slot, long account, string button, Fun.Setting setting, string line)
    {
        if (HudVersion.IsBefore(VersionOf(account), FunClicks.BubbleLevel)) { Post(Messages.Answer, line, account); return; }
        funClicks.Refuse(slot, button, setting, line, DateTime.UtcNow);
        Post(Messages.FunRefused, line, account);
    }

    /// <summary>!gravity low, a frame after the line: the same change as a click, with its answers in that player's chat. What the Fun page leaves out outside Arms Race (HudMenu.FunPage), chat leaves alone too.</summary>
    void FunTyped(CCSPlayerController player, string arg)
    {
        var parts = arg.Split(' ');
        if (Fun.Find(parts[0]) is not { } setting || Fun.Find(setting, parts[1]) is not { } choice) return;
        var account = Account(player);
        void Reply(string line) => Post(Messages.CommandReply, line, account);
        if (!mode.ArmsRace && Fun.ArmsRaceOnly(setting)) Reply(ChatReply.NotInMode(setting, mode.Name));
        else if (Fun.Showing(setting, FunValue) == choice) Reply(ChatReply.Already(setting, choice));
        else if (Fun.RefusalLine(setting, choice, FunValue) is { } refused) Reply(refused); // a Clash
        else FunChange(player, setting, choice, Reply);
    }

    /// <summary>The change itself, clicked or typed: the settings module makes it a frame later (not inside the player's own click or chat line: a change may hand out grenades or switch
    /// catch-up - the class summary), and the message that says who switched what follows at once, once the value is there. refused: what becomes of the marked line when the value did
    /// not land - somebody's change a moment before it (a Clash the settings module refused), or the module itself.</summary>
    void FunChange(CCSPlayerController player, Fun.Setting setting, Fun.Choice choice, Action<string> refused)
    {
        var who = Hud.Clean(player.PlayerName, 32);
        Server.NextFrame(() =>
        {
            FunSet?.Invoke(setting, choice, who);
            if (Fun.Showing(setting, FunValue) == choice) Post(Messages.FunChanged, Fun.ChangedLine(who, setting, choice));
            else refused(Fun.RefusedJust(setting, choice, FunValue, FunSetBy) ?? ChatReply.DidNotSwitch(setting));
            Frame();
        });
    }

    /// <summary>The Fun page's Random or Default, from a click or from !fun random / !fun default: Random draws every setting the page shows in this
    /// mode from its own choices (Fun.Randomized; catch-up on three times in four), back to Normal first so nothing Clashes on the way; Default puts
    /// every one back to its first choice, as the reset after 10 minutes with nobody on does. Made a frame later, as a row's change is; one message
    /// says who did it and what changed.</summary>
    void FunAll(CCSPlayerController player, string which)
    {
        var who = Hud.Clean(player.PlayerName, 32);
        if (which == Fun.DefaultWord)
        {
            Server.NextFrame(() =>
            {
                FunReset?.Invoke(who);
                Post(Messages.FunDefault, Fun.DefaultLine(who));
            });
            return;
        }
        var changed = Fun.Randomized(dice.Next, mode.ArmsRace).Where(x => Fun.Showing(x.Setting, FunValue) != x.Choice).ToList();
        Server.NextFrame(() =>
        {
            foreach (var (setting, choice) in Fun.InSafeOrder(changed)) FunSet?.Invoke(setting, choice, who);
            Post(Messages.FunRandom, Fun.RandomLine(who, changed.Where(x => Fun.Showing(x.Setting, FunValue) == x.Choice).ToList())); // what landed
        });
    }
}
