// The chat commands (ChatCommands.cs): what the menu's click would do, answered in chat.
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace OverStrike2;

sealed partial class HudScreen
{
    // ---- the chat commands (ChatCommands.cs) ----

    /// <summary>A vote for the next map is open, a match end's or a player's: a bare !vote brings its window back, and !vote 3 votes for the card with number 3
    /// (Chat). A bare number in chat is plain chat, as every line that is no command - on MAPS > Browse no search either (ChatCommands.SearchOf).</summary>
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
                var sorted = Player(player.Slot).Browser;
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
                if (Finds(account, cmd.Arg)) Searched(account, cmd.Arg); // the words row's recent searches: the ones that found maps
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
                VoteTyped(player, account, cmd.Arg, Say);
                return;
            case ChatCommands.Kind.Bots:
                BotsTyped(player, cmd.Arg); // the same change as a click on the Bots page (HudScreenBots.cs)
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

    /// <summary>!vote 3: the vote for the card with number 3 - the click a card sends (MapVote.Click), so the bridge counts it as one. Every card keeps its number for
    /// the whole vote, the same for everybody (VoteState.Option.N); Random's is MapVote.RandomOption (!vote random). Nothing is sent for a number no card has, or
    /// for the card this player's vote is on already (a vote never goes back).</summary>
    void VoteTyped(CCSPlayerController player, long account, string arg, Action<string> say)
    {
        if (!vote.Open || vote.Round <= 0) { say(ChatReply.NoVote); return; }
        var n = int.Parse(arg, CultureInfo.InvariantCulture);
        if (vote.Numbered(n) is not { } card) { say(ChatReply.NoCard(n)); return; }
        if (vote.ChoiceOf(account) == n) { say(ChatReply.VoteAlready(card.Label)); return; }
        Typed(account);
        Act(player, account, MapVote.Click(vote.Round, n), "chat");
        say(ChatReply.YourVote(card.Label));
    }

    // ---- a chat line typed on MAPS > Browse: its search, when its words find a map (233) ----

    int chatSearches; // since the plugin's load: the chat lines that searched a player's browser (os_hud)

    /// <summary>A chat line as the words it would search this player's browser for, or null (ChatCommands.SearchOf): only while their window is on MAPS > Browse in a game that
    /// shows the HUD (HudFrame.BrowserOpen). Read inside the player's own chat message, so it only looks; the search waits for the next frame (ChatSearch).</summary>
    public string? SearchOf(CCSPlayerController player, string line) =>
        ChatCommands.SearchOf(line, HudFrame.BrowserOpen(slots.WindowOpen(player.Slot), slots.PageOf(player.Slot), Player(player.Slot), vote, HasCards(player)), VoteOpen);

    /// <summary>The words of a chat line SearchOf took, which shows in chat as it was typed: the browser's search when they find a map (SearchFor).</summary>
    public void ChatSearch(CCSPlayerController player, string words)
    {
        if (SearchFor(player, words)) chatSearches++;
    }

    /// <summary>Words a player searches the browser for - a chat line's, or a click on one of the search's words: when they find a map they become its search, as !s's do -
    /// the words in its search area with its cross, the best matches first, from its first page - and the newest of this player's searches; when they find none the
    /// cards stay as they are, and the window's line alone says so. False then, and with no map list yet (the line was chat anyway).</summary>
    bool SearchFor(CCSPlayerController player, string words)
    {
        if (catalog == null) return false;
        var account = Account(player);
        if (!Finds(account, words)) { WindowNote(player.Slot, ChatReply.ChatSearchNone(words)); return false; }
        var browser = Player(player.Slot).Browser;
        (browser.Sort, browser.Descending) = (HudBrowser.SortBy.Rank, false); // the default order, which puts the best matches first
        Menu(player, words);
        Searched(account, words);
        log.LogInformation("Over Strike 2 HUD: {Name} searched the map browser: {Words}", player.PlayerName, Hud.Clean(words, HudSearch.MaxQuery));
        return true;
    }

    /// <summary>Whether these words find a map on the list, with no filter: what a search for them would show.</summary>
    bool Finds(long account, string words)
    {
        if (catalog == null) return false;
        var probe = new HudBrowser();
        probe.Find(words);
        return probe.Matches(catalog, account, search).Count > 0;
    }

    readonly Dictionary<long, List<string>> recentSearches = new(); // account -> its last searches, newest first: the search's words row (content v4.11.0)

    /// <summary>A search that found maps, as this player's newest (HudBrowser.Remember): by account, through a map change's reconnect.</summary>
    void Searched(long account, string words) => recentSearches[account] = HudBrowser.Remember(recentSearches.GetValueOrDefault(account), words);

    /// <summary>A click on the search area: how searching works, on the window's line and in chat, and the search helped for HudBrowser.HelpSeconds - the words row, the
    /// box lit, the window lifted, a pointer at the chat line (content v4.11.0) - or no longer, at a second click.</summary>
    void AskedHowToSearch(CCSPlayerController player, DateTime now)
    {
        LocalNote(player.Slot, ChatReply.HowToSearch);
        var me = Player(player.Slot);
        me.SearchHelpUntil = now < me.SearchHelpUntil ? DateTime.MinValue : now.AddSeconds(HudBrowser.HelpSeconds);
    }

    /// <summary>A click on one of the search's words: that word searched, as a chat line's would be.</summary>
    void WordClicked(CCSPlayerController player, (bool Recent, int Index) word)
    {
        var list = word.Recent ? (IReadOnlyList<string>)(recentSearches.GetValueOrDefault(Account(player)) ?? []) : HudFrame.PopularOf(catalog);
        if (word.Index < list.Count) SearchFor(player, list[word.Index]);
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
