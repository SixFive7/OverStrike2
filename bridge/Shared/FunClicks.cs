// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>What a click on a Fun choice comes to, and the bubble it leaves at that button: an error stands beside the click, not elsewhere on the page. An impossible choice is greyed for everybody (Fun.Impossible, class off) and stays a Button, so the mouse on it shows
/// why (osfw{r}, HudMenu.FunWhy); a click on one that has been grey long enough to be seen is ignored. A click on one that went grey a moment ago was made before the player could see it:
/// refused, and that button alone says who changed what for RefusedSeconds (class refused, for that player; the same bubble, no hover needed). Free of CounterStrikeSharp: the plugin hands it
/// the clock and sends what it returns; --selftest walks it.</summary>
public sealed class FunClicks
{
    public const double RefusedSeconds = 8;

    /// <summary>The grey is on the player's screen, and seen, this long after the change: a frame (a quarter of a second), the network, a hand already on its way.</summary>
    public const double SeenSeconds = 2;

    /// <summary>Go: ask the settings module. Lit: the choice that shows already. Seen: greyed for long enough, ignored. Refused: refuse it now, saying who changed what.</summary>
    public enum Verdict { Go, Lit, Seen, Refused }

    readonly Dictionary<string, DateTime> greySince = new(StringComparer.Ordinal);                       // button -> since when it has been grey
    readonly Dictionary<int, (string Button, string Var, string Words, DateTime Until)> refused = new(); // slot -> that player's refused click
    readonly Dictionary<int, HashSet<string>> touched = new();                                           // slot -> the buttons that ever had class refused for it
    readonly Dictionary<int, HashSet<string>> spoken = new();                                            // slot -> the variables that ever had words for it

    /// <summary>Every frame, from the page's class off: whether this button is grey now.</summary>
    public void Grey(string button, bool on, DateTime now)
    {
        if (on) greySince.TryAdd(button, now);
        else greySince.Remove(button);
    }

    public Verdict Click(string button, Fun.Setting s, Fun.Choice c, Func<string, string?> value, DateTime now) =>
        Fun.Showing(s, value) == c ? Verdict.Lit
        : !Fun.Impossible(s, c, value) ? Verdict.Go
        : greySince.TryGetValue(button, out var since) && (now - since).TotalSeconds >= SeenSeconds ? Verdict.Seen : Verdict.Refused;

    /// <summary>This player's click on this button of this setting is refused: the bubble says so for RefusedSeconds; a newer refusal replaces it. words may carry marks: a bubble has plain words.</summary>
    public void Refuse(int slot, string button, Fun.Setting s, string words, DateTime now) =>
        Refuse(slot, button, HudMenu.FunWhyVar(Array.IndexOf(Fun.Settings, s) + 1), words, now);

    /// <summary>The same, its words in the row variable whyVar (the Bots page's osbw: HudBots.WhyVar); "" for a button with no row - a preset, a top button - whose class
    /// refused alone shows it.</summary>
    public void Refuse(int slot, string button, string whyVar, string words, DateTime now)
    {
        refused[slot] = (button, whyVar, M.Plain(words), now.AddSeconds(RefusedSeconds));
        (touched.TryGetValue(slot, out var mine) ? mine : touched[slot] = new(StringComparer.Ordinal)).Add(button);
    }

    /// <summary>A player left the slot: their refusal goes. What was touched stays, so the next player in it gets the class off and the words emptied.</summary>
    public void Gone(int slot) => refused.Remove(slot);

    (string Button, string Var, string Words)? Active(int slot, DateTime now)
    {
        if (!refused.TryGetValue(slot, out var r)) return null;
        if (now < r.Until) return (r.Button, r.Var, r.Words);
        refused.Remove(slot);
        return null;
    }

    /// <summary>The class refused for this player: on at the refused button while it lasts, off on every other one it was ever on. Only those: a button never refused is never sent one.</summary>
    public List<(string Id, string Class, bool On)> Classes(int slot, DateTime now)
    {
        if (!touched.TryGetValue(slot, out var buttons)) return [];
        var at = Active(slot, now)?.Button;
        return buttons.Select(b => (b, "refused", b == at)).ToList();
    }

    /// <summary>The bubbles' words for this player, out of what the page says for everybody (HudMenu.FunWhy): the refused button's row says who changed what while it lasts. A variable that never
    /// had words for this player is not sent: its bubble cannot show, and the entity's per-player lists stay short.</summary>
    public List<(string Name, string Text)> Words(int slot, IEnumerable<(string Name, string Text)> why, DateTime now)
    {
        var mine = spoken.TryGetValue(slot, out var had) ? had : spoken[slot] = new(StringComparer.Ordinal);
        var active = Active(slot, now);
        var words = new List<(string, string)>();
        foreach (var (name, text) in why)
        {
            var says = active is { } a && a.Var == name ? a.Words : text;
            if (says != "") mine.Add(name);
            if (mine.Contains(name)) words.Add((name, says));
        }
        return words;
    }
}
