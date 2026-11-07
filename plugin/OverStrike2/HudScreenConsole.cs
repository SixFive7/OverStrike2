// os_hud's verbs on the HUD: the operator's (tab, release) and the client harness's (level, show, content, press, click), whose game cannot click.
using CounterStrikeSharp.API;

namespace OverStrike2;

sealed partial class HudScreen
{
    /// <summary>os_hud tab off|down: TAB's mode until the plugin reloads (null: back to hud.tab from the bridge).</summary>
    public string SetTab(string? mode)
    {
        tabOverride = mode == null ? null : HudTab.Parse(mode);
        Server.NextFrame(Frame);
        return tabOverride == null ? "TAB: back to hud.tab from the bridge" : $"TAB: {HudTab.Name(tabOverride.Value)} until the plugin reloads";
    }

    /// <summary>os_hud release: every window closed and every cursor given back - the way out if one ever sticks.</summary>
    public string ReleaseAll()
    {
        var now = DateTime.UtcNow;
        var cursors = tab.ReleaseAll();
        var windows = 0;
        foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }))
            if (slots.WindowOpen(p.Slot)) { slots.Close(p.Slot, now); windows++; }
        foreach (var slot in captured.Keys.ToList()) captured[slot] = true; // force the next frame to send capture off to everybody
        Server.NextFrame(Frame);
        return $"released: {windows} window(s), {cursors.Count} TAB cursor(s); capture off for everybody next frame";
    }

    /// <summary>os_hud level &lt;n&gt;: the server's content level as the version classes show it - latest-N, which from v4.6.0 on lights the
    /// outdated banner of every content below N, and the svge/svlt of the join banners of v4.0.0 to v4.5.0 (the client harness); null is the real one.</summary>
    public int? TestLevel { get; set; }

    /// <summary>os_hud level: a real join again for that account's slot, so the join banner of v4.0.0 to v4.5.0 shows for a while (the client harness).</summary>
    public string Rejoin(long account)
    {
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } p) return $"no player [U:1:{account}]";
        joins.Joined(account, DateTime.UtcNow);
        Server.NextFrame(Frame);
        return $"join banner for {p.PlayerName} for {HudVersion.JoinSeconds:0} s (server level {TestLevel ?? HudVersion.Level})";
    }

    /// <summary>os_hud show &lt;account&gt; &lt;page&gt;: the window on that page for that account, as if they had clicked their way there - for
    /// the client test harness, whose CS2 never has focus and so cannot click. Pages: start, maps, fun, bots, players, game, sync, help, how; and while a
    /// vote is on, vote (MAPS on its ballot) and browse (MAPS on the map browser that adds to the vote).</summary>
    public string ShowFor(long account, string page)
    {
        var word = page.Trim().ToLowerInvariant();
        var sub = word is "vote" or "browse" ? word : "";
        if (sub != "") page = nameof(HudSlots.Page.Maps);
        if (!Enum.TryParse<HudSlots.Page>(page, ignoreCase: true, out var p)) return $"no page {page} (pages: {string.Join(", ", Enum.GetNames<HudSlots.Page>())}, and vote or browse while a vote is on)";
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } player) return $"no player [U:1:{account}] on the server";
        if (sub != "" && vote.Round <= 0) return "no vote is on: vote and browse need one (Start a vote first: os_hud press <account> os_b_next)";
        worldReady = true;
        if (sub != "") Player(player.Slot).VoteView = (vote.Round, VoteView(player.Slot).Page, sub == "browse" && vote.Open);
        slots.Show(player.Slot, p, DateTime.UtcNow);
        Server.NextFrame(() => { EnsureEntity(); Frame(); });
        return $"window on {p}{(sub != "" ? $" ({sub})" : "")} for {player.PlayerName}";
    }

    /// <summary>os_hud content &lt;account&gt; &lt;level&gt;: the HUD that account's game showed at the HUD check's click, as if it had clicked - for the
    /// client harness, which cannot click: 46 is v4.6.0, any level a content; 1 an older content (its gold button); 0 forgets it. It lasts until that
    /// player's next disconnect, a map change's included (Gone): the bridge never hears of it.</summary>
    public string SetContent(long account, int level)
    {
        if (level <= 0) hudSeen.Remove(account);
        else hudSeen[account] = level == 1 ? HudVersion.Older : HudVersion.OfLevel(level);
        Server.NextFrame(Frame);
        return $"account {account}: HUD {(level <= 0 ? "not seen" : hudSeen[account])}";
    }

    /// <summary>os_hud press &lt;account&gt; &lt;button&gt;: that player's click on a button, through the same path as a real one - a tab,
    /// a filter, a Fun choice, a card's Play - for the client harness, whose CS2 never has focus. Like a real click it is the HUD check's click:
    /// the version button (os_hudver_v4.6.0) verifies that content, any other button a HUD of unknown content (HudCheck.cs).</summary>
    public string Press(long account, string buttonId)
    {
        if (entity is not { IsValid: true } e) return "no HUD entity";
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } p) return $"no player [U:1:{account}] on the server";
        OnClicked(p, e, buttonId);
        return $"pressed {buttonId} for {p.PlayerName}";
    }

    /// <summary>os_hud click: what a click on that button would send for that account, sent - without a client, to try the path
    /// from the button to the bridge. Returns what happened, for the console.</summary>
    public string SimulateClick(long account, string buttonId)
    {
        buttonId = HudIds.ToOurs(buttonId);
        var command = HudView.Command(buttonId, state, vote);
        if (command == null) return $"button {buttonId} sends nothing for account {account} (state {(state == null ? "unknown" : "known")})";
        if (bridge == null) return "no bridge link";
        var name = Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account)?.PlayerName ?? "HUDtest"; // the player's own name: the bridge keeps it
        bridge.Act(account, name, command);
        return $"sent {command} for account {account}; the bridge's answer arrives with the next frame";
    }
}
