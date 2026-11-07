// The HUD check: who has been seen clicking and with which content, the check's mode, the hold while Steam does not show the item, and the
// warning and the disconnect.
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

sealed partial class HudScreen
{
    /// <summary>Seen clicking since the player's last real join: here, or by the bridge (which keeps it through a plugin reload).</summary>
    bool Verified(long account) => hudSeen.ContainsKey(account) || HudCheck.IsVerified(state, account);

    /// <summary>The HUD a player's game showed at the check's click (HudVersion.IsWord), "" while not seen: here, else the bridge's word.</summary>
    string VersionOf(long account) => hudSeen.TryGetValue(account, out var v) && v != HudVersion.Unknown ? v
        : state?.Verified.TryGetValue(account, out var b) == true && b != HudVersion.Unknown ? b : hudSeen.ContainsKey(account) || state?.Verified.ContainsKey(account) == true ? HudVersion.Unknown : "";

    /// <summary>The check's window for a slot: the welcome window, until its X.</summary>
    void Prompt(int slot, DateTime now) => slots.Check(slot, now);

    /// <summary>The server does not yet know which HUD this player's game shows: the window's X is the version button.</summary>
    bool WantsVersion(long account) => VersionOf(account) is var v && (v == "" || v == HudVersion.Unknown);

    /// <summary>An account's first click since its last real join, or the one that says which HUD it is: verified here at once, and at the bridge
    /// (!hudok with the version, which only this real click sends - never css_gghud click, never chat). A known version is never forgotten for an
    /// unknown one (HudVersion.Unknown).</summary>
    void Verify(CCSPlayerController player, long account, bool wasChecked, string version)
    {
        if (account <= 0) return;
        if (version == HudVersion.Unknown && hudSeen.TryGetValue(account, out var known)) version = known;
        hudSeen[account] = version;
        gate.Verified(player.Slot);
        bridge?.Act(account, player.PlayerName, HudCheck.Command(version));
        log.LogInformation("Over Strike 2 HUD check: {Name} [U:1:{Account}] clicked in the HUD ({Version})", player.PlayerName, account, version == HudVersion.Unknown ? "which content not known" : version);
    }

    /// <summary>The check's mode now: what the bridge says (hud.check: off, warn, enforce), off without a fresh state, and never more than warn
    /// while Steam does not show the item to everybody - a player whose game cannot download it would be thrown out for nothing. A stricter mode
    /// gives everybody still waiting a fresh grace period and the window.</summary>
    void UpdateCheck(DateTime now)
    {
        var configured = HudCheck.Parse(state?.CheckMode);
        item.Poll(now);
        RequireAddon(now);
        var answered = bridge == null || bridge.LastAnswer == default ? double.MaxValue : (now - bridge.LastAnswer).TotalSeconds;
        var mode = HudCheck.ModeOf(state, answered);
        if (mode == HudCheck.Mode.Enforce && !item.Public(now)) mode = HudCheck.Mode.Warn;
        if (!addonRequired) mode = HudCheck.Mode.Off; // nobody is made to download it now, so nobody may be asked for it either
        if (mode == checkMode) return;
        log.LogInformation("Over Strike 2 HUD check: {From} -> {To} (hud.check {Configured}, item {Item})", HudCheck.Name(checkMode), HudCheck.Name(mode),
            HudCheck.Name(configured), item.Describe(now));
        if (mode > checkHigh) // once per step up on a map, never on a flap - and never for a player the bridge knows (a spawn before its first answer)
        {
            foreach (var slot in gate.Rearm(now))
                if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } w && !Verified(Account(w))) Prompt(slot, now);
            checkHigh = mode;
        }
        checkMode = mode;
    }

    /// <summary>While Steam does not show the HUD item to everybody (an update waiting for Valve's review can do that), the server must not
    /// require it - MultiAddonManager would hold every joiner at a download that cannot succeed. So a Web API verdict of gone, private or
    /// banned, or a page visit after the Web API showed a new version that finds Steam's not-shown page, takes the item off
    /// mm_extra_addons, and the visit that finds it shown puts it back (ItemPlan in HudCheck.cs: the page is visited only after a change, at most
    /// every 15 minutes for 3 hours - no idle polling). No fresh word from the Web API: as it was (at a start: as the install step set it).
    /// What that does, by MultiAddonManager's own code (v1.7.1): the list empty, new joiners come in without the HUD at once, no map reloads, and who is on keeps it for the map
    /// that is on, with the banner; the next map change no longer carries it, so their game unmounts it, and they play on without a menu until the hold ends. Nobody without
    /// a ballot may be frozen by a vote, so during a hold the plugin tells the bridge it draws nothing (HudActive: a match end rolls the dice) and the bridge refuses a vote.</summary>
    void RequireAddon(DateTime now)
    {
        var cause = item.Cause;
        var required = item.Required(addonRequired, now);
        if (!required && addonRequired)
        {
            Server.ExecuteCommand("mm_extra_addons \"\"");
            addonRequired = false;
            droppedCause = cause;
            toldNoMenu.Clear();
            keptMenu.Clear();
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false })) keptMenu.Add(Account(p));
            log.LogWarning("Over Strike 2 HUD: Steam does not show Workshop item {Addon} to everybody ({Cause}) - joiners no longer have to download it (mm_extra_addons emptied), the HUD check is off",
                addon, cause switch { HudCheck.Cause.Review => $"the version uploaded {item.Updated} under review", HudCheck.Cause.Unavailable => "the Web API says it is not downloadable", _ => "a page visit by hand found it hidden" });
            var (head, line) = HudCheck.Dropped(cause);
            PostBanner(Messages.Hold, "problem", head, line);
            Post(Messages.Hold, $"{head} - {line}");
        }
        else if (required && !addonRequired)
        {
            Server.ExecuteCommand($"mm_extra_addons {addon}");
            addonRequired = true;
            log.LogInformation("Over Strike 2 HUD: Steam shows Workshop item {Addon} to everybody again - joiners download it again (mm_extra_addons {Addon})", addon, addon);
            PostBanner(Messages.HoldBack, "done", HudCheck.BackHead, HudCheck.BackLine);
            Post(Messages.HoldBack, $"{HudCheck.BackHead} - {HudCheck.BackLine}");
            foreach (var account in toldNoMenu) Post(Messages.HoldNoMenu, HudCheck.BackChat, account); // they have no banner to read
            toldNoMenu.Clear();
            keptMenu.Clear();
        }
    }

    /// <summary>A spawn while joiners do not get the HUD: a player without it has no menu and no banner, so one private chat line - once per player until the HUD is back - says
    /// why and what to do (nothing: play on). Who was on when the hold began still has it on that map.</summary>
    void TellNoMenu(CCSPlayerController player)
    {
        if (addonRequired || keptMenu.Contains(Account(player)) || !toldNoMenu.Add(Account(player))) return;
        Post(Messages.HoldNoMenu, HudCheck.NoMenuChat(droppedCause, item.Updated), Account(player));
    }

    /// <summary>css_gghud item: the item's page visited now; says what the last visit found and whether joiners must download it.</summary>
    public string CheckItemNow()
    {
        item.Soon();
        return $"one item page visit due now, by hand (the answer shows in css_gghud in a few seconds); so far: {item.Describe(DateTime.UtcNow)}, addon {(addonRequired ? "required of joiners" : "NOT required")}";
    }

    /// <summary>css_gghud page &lt;id&gt;: what the item check would make of any item's page and Web API answer right now - logged when the
    /// answers are in (a few seconds); changes nothing.</summary>
    public string ProbeItem(long other)
    {
        _ = item.Probe(other).ContinueWith(t => log.LogInformation("Over Strike 2 HUD item probe: {Result}",
            t.IsCompletedSuccessfully ? t.Result : $"{other}: failed ({t.Exception?.GetBaseException().Message})"), TaskScheduler.Default);
        return $"probing {other}'s Workshop page and the Web API - the answer goes to the server log as 'Over Strike 2 HUD item probe:'";
    }

    /// <summary>What the check's clock says is due: the warning in chat, then the disconnect - with warn only a log line.</summary>
    void RunCheck(DateTime now)
    {
        foreach (var (slot, act, seconds) in gate.Frame(now, checkMode, Verified))
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, IsBot: false, IsHLTV: false } p) { gate.Gone(slot); continue; }
            var account = Account(p);
            switch (act)
            {
                case HudGate.Act.Warn: // the alert's two short lines on screen, and in chat what to do about it
                    Post(Messages.CheckWarning, HudCheck.Alert(seconds, checkMode != HudCheck.Mode.Enforce), account, chatLines: HudCheck.Warning(addon, seconds, checkMode != HudCheck.Mode.Enforce));
                    log.LogInformation("Over Strike 2 HUD check: warned {Name} [U:1:{Account}] ({Mode}, {Seconds} s left)", p.PlayerName, account, HudCheck.Name(checkMode), seconds);
                    break;
                case HudGate.Act.WouldKick:
                    log.LogInformation("Over Strike 2 HUD check (warn): would disconnect {Name} [U:1:{Account}] - no click from the HUD in {Grace} s", p.PlayerName, account, HudCheck.GraceSeconds);
                    break;
                case HudGate.Act.Kick:
                    log.LogWarning("Over Strike 2 HUD check: disconnecting {Name} [U:1:{Account}] - no click from the HUD in {Grace} s", p.PlayerName, account, HudCheck.GraceSeconds);
                    if (p.UserId is int userId) Server.ExecuteCommand($"kickid {userId} \"{HudCheck.Reason(addon)}\"");
                    else p.Disconnect(CounterStrikeSharp.API.ValveConstants.Protobuf.NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
                    break;
            }
        }
    }
}
