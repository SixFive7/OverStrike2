using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace OverStrike2;

/// <summary>The Fun page on screen: what it shows every frame (../../bridge/Shared/HudBrowser.cs: HudMenu.FunPage, FunWhy), what a click on it comes to (../../bridge/Shared/Fun.cs: FunClicks), the same
/// change typed in chat, and Random and Default. The settings module makes every change (gg_fun): it owns the server's settings - the baseline, the put-back at the map's end and the block a
/// player's change is exempt from; the grenade drops and catch-up are fun settings like the rest, its effects. Once the value is there one message says who switched what, the same for every
/// setting (Messages.FunChanged, FunRandom, FunDefault: the top line, chat and the TAB board, for everybody). A setting's "Set by" stays on the page: the message said it. One plugin drives every
/// content: a content before v4.6.0 has sixteen rows and two fixed ones, greys nothing and has no bubble, so its note says what this content's page shows.</summary>
sealed partial class HudScreen
{
    const float FunCheckSeconds = 0.5f;                   // a fun change is made a frame after the click (gg_fun); looked at after this long
    readonly Random dice = new();                         // the Fun page's Random
    readonly FunClicks funClicks = new();                 // which choices are grey since when, and each player's refused click
    List<(string Name, string Text)> funWhy = [];         // what the page's bubbles say, as of this frame

    /// <summary>Where the fun page's values come from: the settings module (convars and its effects). Set at the plugin's load.</summary>
    public Func<string, string?>? FunSource { get; set; }

    /// <summary>Who set each fun setting that is off its default, by the setting's id: the settings module's word (gg_fun's last argument); null
    /// while nobody is known. The settings module sets it at its load.</summary>
    public Func<string, string?>? FunSetBy { get; set; }

    /// <summary>A fun setting's convar (or effect) as the server has it now (Fun.cs), for the page and for checking a change landed.</summary>
    string? FunValue(string name) => FunSource?.Invoke(name);

    /// <summary>Every frame, the same for everybody: the page as the live values have it - the settings, who set them, what is grey - and the fixed rows of the contents before v4.6.0, worded and lit.</summary>
    void FunFrame(DateTime now)
    {
        var (texts, classes) = HudMenu.FunPage(FunValue, mode.ArmsRace, FunSetBy);
        foreach (var (id, text) in texts) Text(id, text);
        foreach (var (id, cls, on) in classes)
        {
            Class(id, cls, on);
            if (cls == "off") funClicks.Grey(id, on, now);
        }
        funWhy = HudMenu.FunWhy(FunValue);
        foreach (var (name, words) in HudMenu.FixedRowWords(FunValue, FunSetBy)) Var(HudView.Global, name, words);
        foreach (var (id, cls, on) in HudVote.FixedRowClasses(FunValue, mode.ArmsRace)) Class(id, cls, on);
    }

    /// <summary>Every frame, for a player with the window open: what the page's bubbles say for them, as variables on gg_screen that the bubbles inherit, and their refused click. One player's
    /// word on a refusal is not everybody's, so all of it goes per player, and only what changed goes out.</summary>
    void FunBubbles(CCSPlayerController p, DateTime now)
    {
        foreach (var (name, words) in funClicks.Words(p.Slot, funWhy, now))
        {
            Var(HudView.Screen, name, words, p);
            Class(HudMenu.FunRowOf(name), "clash", words != "", p); // the reason is the row's second line while it has one
        }
        foreach (var (id, cls, on) in funClicks.Classes(p.Slot, now)) Class(id, cls, on, p);
    }

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

    /// <summary>The change itself, clicked or typed: the settings module makes it a frame later; once the value is there, its message says who switched what. refused: what becomes of the
    /// marked line when the value did not land - somebody's change a moment before it (a Clash the settings module refused), or the module itself.</summary>
    void FunChange(CCSPlayerController player, Fun.Setting setting, Fun.Choice choice, Action<string> refused)
    {
        var who = Hud.Clean(player.PlayerName, 32);
        Server.NextFrame(() => Server.ExecuteCommand($"{Fun.Command} {setting.Id} {choice.Id} \"{Quoted(who)}\""));
        plugin.AddTimer(FunCheckSeconds, () =>
        {
            if (Fun.Showing(setting, FunValue) == choice) Post(Messages.FunChanged, Fun.ChangedLine(who, setting, choice));
            else refused(Fun.RefusedJust(setting, choice, FunValue, FunSetBy) ?? ChatReply.DidNotSwitch(setting));
            Frame();
        });
    }

    /// <summary>The Fun page's Random or Default, from a click or from !fun random / !fun default: Random draws every setting the page shows in this
    /// mode from its own choices (Fun.Randomized; catch-up on three times in four), Default puts every one back to its first choice - gg_fun reset,
    /// the reset after 10 minutes with nobody on. The same commands a click on each row runs, a frame later; one message says who did it and what changed.</summary>
    void FunAll(CCSPlayerController player, string which)
    {
        var who = Hud.Clean(player.PlayerName, 32);
        if (which == Fun.DefaultWord)
        {
            Server.NextFrame(() =>
            {
                Server.ExecuteCommand($"{Fun.ResetCommand} \"{Quoted(who)}\"");
                Post(Messages.FunDefault, Fun.DefaultLine(who));
            });
            return;
        }
        var changed = Fun.Randomized(dice.Next, mode.ArmsRace).Where(x => Fun.Showing(x.Setting, FunValue) != x.Choice).ToList();
        Server.NextFrame(() =>
        {
            foreach (var (setting, choice) in Fun.InSafeOrder(changed)) Server.ExecuteCommand($"{Fun.Command} {setting.Id} {choice.Id} \"{Quoted(who)}\""); // back to Normal first: no Clash on the way
            Post(Messages.FunRandom, Fun.RandomLine(who, changed));
        });
    }

    /// <summary>A player's name as gg_fun's last argument, inside its quotes.</summary>
    static string Quoted(string who) => who.Replace("\"", "'");
}
