// The chat commands (ChatCommands.cs): what the menu's click would do, answered in chat.
using CounterStrikeSharp.API.Core;
using System.Globalization;

namespace OverStrike2;

sealed partial class HudScreen
{
    // ---- the chat commands (ChatCommands.cs) ----

    /// <summary>The end-of-match vote is open: a bare number in chat is a vote (ChatCommands.Parse).</summary>
    public bool VoteOpen => vote is { Open: true, Round: > 0 };

    /// <summary>A chat command, a frame after the line: what the menu's click would do, and the answer in chat, in its functional colours (Messages.CommandReply; the words are
    /// ChatCommands' and ChatReply's) - the bridge's answer to that player has a chat copy of its own (Messages.Answer). An alias's reply first teaches the primary.</summary>
    public void Chat(CCSPlayerController player, ChatCommands.Parsed cmd)
    {
        var account = Account(player);
        void Say(string line) => Post(Messages.CommandReply, Hud.Clean(line, Messages.ChatMax), account);
        void Bridge(string command) { Typed(account); Act(player, account, command); }
        if (ChatCommands.Teach(cmd) is { } taught) Say(taught);
        switch (cmd.Kind)
        {
            case ChatCommands.Kind.Bridge:
                var action = cmd.Arg == "next" ? HudView.Command(HudView.BoardNext, state, vote) : cmd.Arg; // the board's Start a vote, as the board reads for this player
                if (action == null && cmd.Arg == "next" && state is not { ChangeIn: >= 0 } && state is not { Loading: true }) action = "!next"; // the wait after a vote: the bridge answers with the time left
                if (action == null) { Say(ChatReply.ChangeUnderWay); return; }
                if (action == HudView.OpenVote) { Menu(player); Say(ChatReply.VoteOn); return; }
                Bridge(action);
                return;
            case ChatCommands.Kind.Top:
                var sorted = Browser(player.Slot);
                sorted.Find("");
                (sorted.Sort, sorted.Descending) = (HudBrowser.SortBy.Votes, false);
                ShowFor(account, cmd.Arg);
                return;
            case ChatCommands.Kind.Page:
                ShowFor(account, cmd.Arg);
                if (cmd.Arg == "Fun") foreach (var line in ChatCommands.FunLines(mode.ArmsRace)) Say(line);
                else if (cmd.Typed == "") Say(ChatReply.MenuOn(cmd.Arg));
                return;
            case ChatCommands.Kind.Find:
                Menu(player, cmd.Arg);
                Say(ChatReply.Searching(cmd.Arg));
                return;
            case ChatCommands.Kind.Play:
                Play(player, account, cmd.Arg, Say, Bridge);
                return;
            case ChatCommands.Kind.Fun:
                FunTyped(player, cmd.Arg); // the same change as a click on the Fun page (HudScreenFun.cs)
                return;
            case ChatCommands.Kind.Vote:
                if (!vote.Open || vote.Round <= 0) { Say(ChatReply.NoVote); return; }
                foreach (var line in ChatCommands.VoteOnlyLines()) Say(line); // a vote opened since the line was typed: still a click
                return;
            case ChatCommands.Kind.VoteOnly:
                foreach (var line in ChatCommands.VoteOnlyLines()) Say(line);
                return;
            case ChatCommands.Kind.FunAll:
                FunAll(player, cmd.Arg); // the Fun page's Random or Default
                return;
            case ChatCommands.Kind.Commands:
                foreach (var line in ChatCommands.HelpLines()) Say(line);
                ShowFor(account, "Help");
                return;
            case ChatCommands.Kind.Usage:
                Say(ChatCommands.UsageOf(cmd.Name));
                return;
            case ChatCommands.Kind.Unknown:
                Say(ChatCommands.UnknownLine(cmd.Name));
                return;
        }
    }

    /// <summary>!play &lt;map&gt;: a number is a map's rank (its !map); a Workshop id no listed map has opens its card (the menu's download);
    /// an official map's name is that map; words are a search - one clear best match is played, otherwise the browser opens on them.</summary>
    void Play(CCSPlayerController player, long account, string what, Action<string> say, Action<string> bridge)
    {
        var q = what.Trim();
        if (WorkshopCard.IdOf(q) is { } wid && catalog?.Maps.All(m => m.Id != wid) == true && q.Length > 6) { OpenWorkshop(player, wid); say(ChatReply.CardOpen); return; }
        if (catalog == null) { say(ChatReply.ListLoading); return; }
        var exact = catalog.Maps.FirstOrDefault(m => m.Arg.Equals(q, StringComparison.OrdinalIgnoreCase) || m.Id.ToString(CultureInfo.InvariantCulture) == q
            || m.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (exact != null) { bridge($"!map {HudMenu.PlayArg(exact, "", mode.Id)}"); return; } // in the mode a card's Play would load it in (B7)
        var browser = new HudBrowser();
        browser.Find(q);
        var hits = browser.Matches(catalog, account, search);
        if (hits.Count == 1) { bridge($"!map {HudMenu.PlayArg(hits[0], "", mode.Id)}"); return; }
        Menu(player, q);
        say(hits.Count == 0 ? ChatReply.NoMatch(q) : ChatReply.Matches(hits.Count));
    }
}
