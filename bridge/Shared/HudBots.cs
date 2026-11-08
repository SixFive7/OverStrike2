// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;

/// <summary>The Bots page (FUN, Bots) as texts and classes, the same for everybody, out of the live values (Bots.cs): twelve rows of up to five choices in two
/// columns, as the Fun page draws its own (os-frow), the presets under them, and the page's two buttons; and a click on it as what it asks for. A row's title,
/// its words - with who set it while it is off its default, as the Fun page says it - its choices, the one showing lit, a choice the mode cannot make greyed (off)
/// with the reason as the row's second line (osbw{r}, class clash); a preset lit while the live values show it, greyed with its reason in its words where its bots
/// do not fit. Every panel is the content's from v4.8.0 on: an older content has none of them, and takes nothing.</summary>
public static class HudBots
{
    /// <summary>The FUN tab's two sub-tabs: Settings (the Fun page, also the tab's own button) and Bots.</summary>
    public const string SubFun = "os_sub_fun", Tab = "os_tab_bots";

    public const string Note = "os_bots_note", Kick = "os_bots_kick", Default = "os_bots_default";

    public static string Row(int r) => $"os_b{r}";

    public static string Button(int r, int k) => $"os_b{r}_{k}";

    public static string Preset(int p) => $"os_bp{p}";

    /// <summary>The variable a row's second line reads: why its greyed choices cannot be made in this mode.</summary>
    public static string WhyVar(int r) => $"osbw{r}";

    /// <summary>The page as the live values have it, in this mode. setBy: who set a setting, by its id (the settings module's word); null: nobody known.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Page(Func<string, string?> value, Modes.Mode mode,
        Func<string, string?>? setBy = null)
    {
        var t = new List<(string, string)> { (Note, Bots.Note) };
        var c = new List<(string, string, bool)>();
        for (var r = 1; r <= Bots.Rows; r++)
        {
            var s = r <= Bots.Settings.Length ? Bots.Settings[r - 1] : null;
            var showing = s == null ? null : Fun.Showing(s, value);
            var who = s == null || showing == null ? "" : HudMenu.SetByWords(s, showing, setBy?.Invoke(s.Id));
            t.Add(($"{Row(r)}_title", s?.Title ?? ""));
            t.Add(($"{Row(r)}_txt", s == null ? "" : who != "" ? $"{who}  ·  {s.Words}" : s.Words));
            c.Add((Row(r), "used", s != null));
            c.Add((Row(r), "changed", s != null && showing != s.Normal && who != "")); // a player's choice stands out; Arms Race's own fill is nobody's
            for (var k = 1; k <= Bots.ChoicesPerRow; k++)
            {
                var choice = s != null && k <= s.Choices.Length ? s.Choices[k - 1] : null;
                t.Add(($"{Button(r, k)}_txt", choice?.Label ?? ""));
                c.Add((Button(r, k), "used", choice != null));
                c.Add((Button(r, k), "on", choice != null && choice == showing));
                c.Add((Button(r, k), "off", s != null && choice != null && choice != showing && Bots.Unavailable(s, choice, mode) != null));
            }
        }
        var lit = Bots.Showing(value, mode);
        for (var p = 1; p <= Bots.PresetCount; p++)
        {
            var preset = p <= Bots.Presets.Length ? Bots.Presets[p - 1] : null;
            var why = preset == null ? null : Bots.Unavailable(preset, mode);
            t.Add(($"{Preset(p)}_title", preset?.Title ?? ""));
            t.Add(($"{Preset(p)}_txt", preset == null ? "" : why ?? preset.Words));
            c.Add((Preset(p), "used", preset != null));
            c.Add((Preset(p), "on", preset != null && preset == lit));
            c.Add((Preset(p), "off", why != null));
        }
        c.Add((Kick, "off", Bots.Quota(value) == 0)); // nothing to kick: no bot plays
        return (t, c);
    }

    /// <summary>Each row's second line: why its greyed choices cannot be made in this mode (Bots.Unavailable), "" on a row with none. One reason a row: every choice
    /// a mode greys on one row is greyed for the same reason.</summary>
    public static List<(string Name, string Text)> Why(Func<string, string?> value, Modes.Mode mode) =>
        Bots.Settings.Take(Bots.Rows).Select((s, i) => (WhyVar(i + 1),
            s.Choices.Where(c => c != Fun.Showing(s, value)).Select(c => Bots.Unavailable(s, c, mode)).FirstOrDefault(x => x != null) ?? "")).ToList();

    /// <summary>The row a reason's variable belongs to (osbw2 -> os_b2): it takes class clash while that reason shows.</summary>
    public static string RowOf(string whyVar) => $"os_b{whyVar[4..]}";

    /// <summary>What a click on the Bots page asks for: a row's choice, a preset, Kick all bots or Default; null when it is no button of the page.</summary>
    public sealed record Click(Fun.Setting? Setting, Fun.Choice? Choice, Bots.Preset? Preset, string? All);

    public static Click? Clicked(string buttonId)
    {
        if (buttonId == Kick) return new(null, null, null, Bots.KickWord);
        if (buttonId == Default) return new(null, null, null, Bots.DefaultWord);
        if (buttonId.StartsWith("os_bp", StringComparison.Ordinal))
            return int.TryParse(buttonId[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p >= 1 && p <= Math.Min(Bots.PresetCount, Bots.Presets.Length)
                ? new(null, null, Bots.Presets[p - 1], null) : null;
        if (!buttonId.StartsWith("os_b", StringComparison.Ordinal) || buttonId[4..].Split('_') is not [var row, var col]
            || !int.TryParse(row, NumberStyles.None, CultureInfo.InvariantCulture, out var r) || !int.TryParse(col, NumberStyles.None, CultureInfo.InvariantCulture, out var k)) return null;
        if (r < 1 || r > Math.Min(Bots.Rows, Bots.Settings.Length)) return null;
        var s = Bots.Settings[r - 1];
        return k >= 1 && k <= s.Choices.Length ? new(s, s.Choices[k - 1], null, null) : null;
    }
}
