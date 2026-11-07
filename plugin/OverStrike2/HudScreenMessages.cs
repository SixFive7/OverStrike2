using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The plugin's half of the message system (../../bridge/Shared/Messages.cs is the catalogue; the bridge's half is its Notices.cs). Post routes a message of the
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

    /// <summary>A message of the plugin's own, where the catalogue routes it (Messages.Route, as the bridge's are). text may carry marks (M). account: the player it is for
    /// or about, when the kind goes to a player; key: a line that replaces its own older version on the board; seconds: how long, when not the kind's; chat: false leaves
    /// chat out this once (a refreshed line says it only at its steps); chatLines: chat's own lines, when they are not the surface's words (the HUD check's alert and
    /// its warning). Safe in any callback: chat goes out a frame later.</summary>
    public void Post(Messages.Kind kind, string text, long account = 0, string key = "", double? seconds = null, bool chat = true, IReadOnlyList<string>? chatLines = null)
    {
        var m = Messages.Route(kind, text, account, seconds, chat);
        var now = DateTime.UtcNow;
        var nowMs = bridge?.BridgeNowMs(now) ?? HudState.Ms(now);
        if (m.On is { } on)
            switch (kind.Surface)
            {
                case Surface.Top: localTop = (m.Plain, kind.Cat.Id, nowMs, nowMs + (long)(m.Seconds * 1000)); break;
                case Surface.Line: if (PlayerOf(on) is { } p) Player(p.Slot).Note = (m.Plain, now.AddSeconds(m.Seconds), kind.Cat.Id); break;
                case Surface.Plain or Surface.Alert: localCentres.Add((on, kind.Surface == Surface.Alert, Hud.Clean(m.Text, Messages.CentreMax), now.AddSeconds(m.Seconds))); break;
            }
        if (kind.News) localNews = (m.Plain, now.AddSeconds(m.Seconds));
        if (m.Board is { } board) Messages.AddLately(localLately, new Messages.LatelyLine(nowMs, board, key, m.Plain));
        if (m.Chat is { } to) Server.NextFrame(() => { foreach (var line in chatLines ?? [m.Text]) Say(line, to); });
        Server.NextFrame(Frame);
    }

    /// <summary>gg_notice from the restart watcher: a line for everybody, for this long. Its kind says its words: a download's line, or plain news. A download's refreshed line goes
    /// to chat only when it starts and at each quarter.</summary>
    public string Notice(string text, int seconds)
    {
        var line = Messages.Capital(Sections.Capitalize(Hud.Clean(text, Messages.TopMax)));
        var secs = Math.Clamp(seconds, 3, 3600);
        var (kind, key, step) = Messages.OfNotice(line);
        var said = key == "" || !noticeSteps.TryGetValue(key, out var was) || was != step;
        if (key != "") noticeSteps[key] = step;
        Post(kind, line, key: key, seconds: secs, chat: said);
        return $"shown for {secs} s: {line}";
    }

    readonly Dictionary<string, int> noticeSteps = new();

    // ---- a Workshop card's download, in chat and on the board: the card keeps its short form ----

    readonly Dictionary<long, int> workshopSteps = new();

    void WorkshopLine(CCSPlayerController p, WorkshopCard card, int percent)
    {
        if (card.Item is not { } item) return;
        var step = percent / 25;
        var said = !workshopSteps.TryGetValue(item.Id, out var was) || was != step;
        workshopSteps[item.Id] = step;
        var done = item.Bytes * percent / 100;
        Post(Messages.WorkshopDownload, Messages.WorkshopLine(p.PlayerName, item.Title, percent, done, item.Bytes), Account(p), key: $"workshop:{item.Id}", chat: said);
    }

    // ---- catch-up's card: chat and the board through Post, the rows in the player's record (HudFrame draws them) ----

    /// <summary>Whether this player's game holds a content with the catch-up card: known by its version button (HudCheck), and not older. Any other gets the centre panel.</summary>
    public bool HasCards(CCSPlayerController p) => VersionOf(Account(p)) is var v && v != "" && v != HudVersion.Unknown && !HudVersion.IsBefore(v, HudVote.CardsLevel);

    /// <summary>Catch-up's boost or a leader's bounty: under the crosshair for these seconds, a row per item, green for a boost and red for a bounty; the same in that player's chat.</summary>
    public void ShowCatchUp(CCSPlayerController player, string head, IReadOnlyList<string> items, bool bad, double seconds)
    {
        var rows = items.Select(Messages.Capital).Take(HudFrame.CatchUpRows).ToArray();
        var now = DateTime.UtcNow;
        Player(player.Slot).CatchUp = (head, rows, bad, now, now.AddSeconds(seconds));
        var line = $"{head}: {string.Join("  ·  ", rows)}";
        Post(bad ? Messages.Bounty : Messages.CatchUpBoost, bad ? M.Refused(line) : M.Done(line), Account(player), seconds: seconds);
    }

    /// <summary>css_gghud centre: how CS2's plain centre text holds, for the client harness: that text for that many seconds, sent again every so many seconds.</summary>
    public string TestCentre(double every, double seconds, string text, bool alert)
    {
        CentreResendSeconds = Math.Max(0, every);
        localCentres.Add((0, alert, Hud.Clean(text, Messages.CentreMax), DateTime.UtcNow.AddSeconds(seconds)));
        return $"\"{text}\" for {seconds} s, sent again every {CentreResendSeconds} s";
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

    /// <summary>A name's colour in chat: its player's team's, the name whole or as a line clipped it (ChatCommands.IsName); none for a name nobody on the server has, or a spectator's.</summary>
    static char? NameColour(string name)
    {
        var humans = Utilities.GetPlayers().Where(x => x is { IsValid: true, IsBot: false }).ToList();
        return (humans.FirstOrDefault(x => x.PlayerName == name) ?? humans.FirstOrDefault(x => ChatCommands.IsName(name, x.PlayerName))) is { Team: CsTeam.Terrorist or CsTeam.CounterTerrorist } p ? ChatColors.ForTeam(p.Team) : null;
    }

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
            if (lastSay > 0) Say(Answered(account, text), account);
            lastSay = seq;
        }
        if (lastSay == 0) lastSay = 1;
        foreach (var c in raw.Centres) if (bridgeCentres.All(x => x.Seq != c.Seq)) bridgeCentres.Add(c);
    }

    readonly Dictionary<long, DateTime> typedAt = new(); // account -> when a chat command of theirs last went to the bridge

    /// <summary>How long after a typed command the bridge's line to that player counts as its answer.</summary>
    const double TypedAnswerSeconds = 5;

    /// <summary>A chat command of this account's goes to the bridge: what comes back in the next moments is a typist's answer (Answered).</summary>
    void Typed(long account)
    {
        var now = DateTime.UtcNow;
        typedAt[account] = now;
        if (typedAt.Count > 256) foreach (var old in typedAt.Where(x => (now - x.Value).TotalSeconds > TypedAnswerSeconds).Select(x => x.Key).ToList()) typedAt.Remove(old);
    }

    /// <summary>A line of the bridge's as chat says it: a confirmation asks a click of a menu user, and of a player who just typed the command the same line again (ChatCommands.InChat).</summary>
    string Answered(long account, string text) =>
        account > 0 && typedAt.TryGetValue(account, out var at) && (DateTime.UtcNow - at).TotalSeconds <= TypedAnswerSeconds ? ChatCommands.InChat(text) : text;

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
