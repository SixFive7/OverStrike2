using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The plugin's half of the message system (../../bridge/Messages.cs is the catalogue; the bridge's half is its Notices.cs). Post routes a message of the
/// plugin's own - a fun change, a map's game-changer undone, a download line from the restart watcher - as the bridge routes its own. Every frame the plugin's
/// own messages are merged into the state as of that frame (MergeMessages), so the views (HudView) draw one state; the bridge's chat lines are said once each (SayNew), and
/// CS2's plain centre text and alert are held for their seconds (CentreTick).</summary>
sealed partial class HudScreen
{
    (string Text, string Cat, long AtMs, long UntilMs) localTop = ("", "", 0, 0);
    (string Tone, string Head, string Line, long UntilMs) localBanner = ("", "", "", 0);
    readonly List<Messages.LatelyLine> localLately = new();
    readonly List<(long Account, bool Alert, string Text, DateTime Until)> localCentres = new();
    readonly List<(long Seq, long Account, bool Alert, long UntilMs, string Text)> bridgeCentres = new();
    readonly Dictionary<(long Seq, int Slot), DateTime> centreSent = new();
    long lastSay; // the newest chat line of the bridge said; 0: none seen yet, so what the first answer lists is not said (a plugin reload)

    /// <summary>How often a held centre text is sent again: one send lasts about 3.7 s (measured 2026-11-05, t4/facts.md).</summary>
    public double CentreResendSeconds { get; set; } = 3;

    /// <summary>A message of the plugin's own, routed by the catalogue. text may carry marks (M). account: the player it is for or about, when the kind goes to a player;
    /// key: a line that replaces its own older version on the board; seconds: how long, when not the kind's; chat: false leaves chat out this once (a refreshed line says it
    /// only at its steps). Safe in any callback: chat goes out a frame later.</summary>
    public void Post(Messages.Kind kind, string text, long account = 0, string key = "", double? seconds = null, bool chat = true)
    {
        var now = DateTime.UtcNow;
        var nowMs = bridge?.BridgeNowMs(now) ?? HudState.Ms(now);
        var plain = Hud.Clean(M.Plain(text), kind.Surface == Surface.Line ? Messages.LineMax : Messages.TopMax);
        var secs = seconds ?? (kind.Seconds > 0 ? kind.Seconds : Messages.DefaultSeconds);
        switch (kind.Surface)
        {
            case Surface.Top: localTop = (plain, kind.Cat.Id, nowMs, nowMs + (long)(secs * 1000)); break;
            case Surface.Line when kind.On == Who.Player && account > 0:
                if (PlayerOf(account) is { } p) localNotes[p.Slot] = (plain, now.AddSeconds(secs), kind.Cat.Id);
                break;
            case Surface.Plain or Surface.Alert:
                localCentres.Add((kind.On == Who.Player ? account : 0, kind.Surface == Surface.Alert, Hud.Clean(text, Messages.CentreMax), now.AddSeconds(secs)));
                break;
        }
        if (kind.News) localNews = (plain, now.AddSeconds(secs));
        if (kind.Board == Who.Everybody || (kind.Board == Who.Player && account > 0))
            Messages.AddLately(localLately, new Messages.LatelyLine(nowMs, kind.Board == Who.Player ? account : 0, key, plain));
        if (chat && (kind.Chat == Who.Everybody || (kind.Chat == Who.Player && account > 0)))
        {
            var to = kind.Chat == Who.Player ? account : 0;
            Server.NextFrame(() => Say(text, to));
        }
        Server.NextFrame(Frame);
    }

    /// <summary>The status banner from the plugin (the HUD item's hold, and back): tone problem, warning, done or info, until its seconds are up.</summary>
    void PostBanner(Messages.Kind kind, string tone, string head, string line)
    {
        var now = DateTime.UtcNow;
        var nowMs = bridge?.BridgeNowMs(now) ?? HudState.Ms(now);
        localBanner = (tone, Hud.Clean(head, 80), Hud.Clean(line, Messages.TopMax), kind.Seconds > 0 ? nowMs + (long)(kind.Seconds * 1000) : 0);
        Server.NextFrame(Frame);
    }

    CCSPlayerController? PlayerOf(long account) => Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account);

    /// <summary>A marked line in chat, in the chat's functional colours: for everybody (account 0) or one player.</summary>
    public void Say(string marked, long account = 0)
    {
        var palette = new M.Palette(ChatColors.Default, ChatColors.Lime, ChatColors.LightBlue, ChatColors.Gold, ChatColors.LightRed, ChatColors.Green, NameColour);
        var lines = M.ChatLines(marked).Select(line => " " + M.Render(line, palette)).ToList();
        foreach (var p in Utilities.GetPlayers().Where(x => x is { IsValid: true, IsBot: false, IsHLTV: false } && (account == 0 || Account(x) == account)))
            foreach (var line in lines) p.PrintToChat(line);
    }

    /// <summary>A name's colour in chat: its player's team's; none for a name nobody on the server has, or a spectator's.</summary>
    static char? NameColour(string name) =>
        Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && x.PlayerName == name) is { Team: CsTeam.Terrorist or CsTeam.CounterTerrorist } p ? ChatColors.ForTeam(p.Team) : null;

    /// <summary>The state as of this frame gets the plugin's own messages: the newest top line of the two, the banner, the Lately lines.</summary>
    void MergeMessages(HudState s, long nowMs)
    {
        if (localTop.Text != "" && nowMs >= localTop.UntilMs) localTop = ("", "", 0, 0);
        if (localTop.Text != "" && localTop.AtMs >= s.TopAtMs) (s.TopText, s.TopCat, s.TopAtMs, s.TopUntilMs) = (localTop.Text, localTop.Cat, localTop.AtMs, localTop.UntilMs);
        if (localBanner.Head != "" && localBanner.UntilMs > 0 && nowMs >= localBanner.UntilMs) localBanner = ("", "", "", 0);
        if (localBanner.Head != "") (s.BanTone, s.BanHead, s.BanLine, s.BanUntilMs) = (localBanner.Tone, localBanner.Head, localBanner.Line, localBanner.UntilMs);
        if (localLately.Count > 0) s.Lately = [.. s.Lately, .. localLately];
    }

    /// <summary>The bridge's chat lines not said yet, in their order, and its centre texts for CentreTick.</summary>
    void SayNew(HudState? raw)
    {
        if (raw == null) return;
        foreach (var (seq, account, text) in raw.Says.OrderBy(l => l.Seq))
        {
            if (seq <= lastSay) continue;
            if (lastSay > 0) Say(text, account);
            lastSay = seq;
        }
        if (lastSay == 0) lastSay = 1;
        foreach (var c in raw.Centres) if (bridgeCentres.All(x => x.Seq != c.Seq)) bridgeCentres.Add(c);
    }

    /// <summary>The held centre texts: sent again every CentreResendSeconds until their end. The window hides them, which costs nothing.</summary>
    public void CentreTick()
    {
        if (localCentres.Count == 0 && bridgeCentres.Count == 0) return;
        var now = DateTime.UtcNow;
        var nowMs = bridge?.BridgeNowMs(now) ?? HudState.Ms(now);
        localCentres.RemoveAll(c => now >= c.Until);
        bridgeCentres.RemoveAll(c => nowMs >= c.UntilMs);
        foreach (var (account, alert, text, _) in localCentres) SendCentre(text.GetHashCode(), account, alert, text, now);
        foreach (var (seq, account, alert, _, text) in bridgeCentres) SendCentre(seq, account, alert, text, now);
    }

    void SendCentre(long seq, long account, bool alert, string text, DateTime now)
    {
        var words = M.Centre(text);
        foreach (var p in Utilities.GetPlayers().Where(x => x is { IsValid: true, IsBot: false, IsHLTV: false } && (account == 0 || Account(x) == account)))
        {
            if (centreSent.TryGetValue((seq, p.Slot), out var last) && (now - last).TotalSeconds < CentreResendSeconds) continue;
            centreSent[(seq, p.Slot)] = now;
            if (centreSent.Count > 256) foreach (var old in centreSent.Where(kv => (now - kv.Value).TotalSeconds > 60).Select(kv => kv.Key).ToList()) centreSent.Remove(old);
            if (alert) p.PrintToCenterAlert(words); else p.PrintToCenter(words);
        }
    }
}
