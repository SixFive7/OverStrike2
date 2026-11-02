// The welcome panel, shared by two programs: the bridge compiles this file (--selftest measures the panel and drives its timing),
// and the GunGameWelcome plugin links it in and shows it. Plain BCL only: no CounterStrikeSharp type.
using System.Text;

public static class Guide
{
    /// <summary>The console command the plugin registers and the bridge sends over RCON when a player really joins.</summary>
    public const string Command = "css_guide";

    /// <summary>How the plugin's reply to Command starts when the panel went up; any other reply means it did not.</summary>
    public const string Shown = "guide: showing";

    public const int LineWidth = 28;   // measured in game 2026-10-04: the panel wraps at ~31 characters
    public const int MaxLines = 5;     // taller content makes CS2 scroll the panel, and every tick's re-send then flickers
    public const int ShowSeconds = 12; // on screen this long after it goes up, then cleared

    /// <summary>The panel, one line each: a title and the tagline, then a pointer to !help. Everything else is discovered there. (The centre
    /// panel is the fallback while GUNGAME_HUD_ADDON is empty; with the HUD on, a join gets TaglineHtml instead.)</summary>
    public static string[] Lines => ["Welcome to Over Strike 2!", Tagline, "Commands, voting, queue:", "Type !help in chat"];

    /// <summary>The server's name and tagline (the owner's decision of 2026-11-01); the tagline is shown in the centre of the
    /// screen for a few seconds to every player who joins.</summary>
    public const string Name = "Over Strike 2", Tagline = "Over the top - On the Huis";

    /// <summary>How long the tagline stays up after a real join: a few seconds - about 3, because the CatchUp plugin's own centre message
    /// waits that long for it at a player's first spawn after joining.</summary>
    public const int TaglineSeconds = 3;

    /// <summary>The tagline panel: the name in gold and bold, the tagline under it - the same tags as the welcome panel.</summary>
    public static string TaglineHtml() => $"<font color='#FFD700'><b>{Escape(Name.ToUpperInvariant())}</b></font><br>{Escape(Tagline)}";

    /// <summary>The HTML for PrintToCenterHtml: only the tags CounterStrikeSharp's own center menu uses (font colour, bold, br).
    /// The title is gold and bold, a word starting with '!' green.</summary>
    public static string Html()
    {
        var html = new StringBuilder($"<font color='#FFD700'><b>{Escape(Lines[0])}</b></font>");
        foreach (var line in Lines.Skip(1)) html.Append("<br>").Append(string.Join(' ', line.Split(' ').Select(word =>
            word.StartsWith('!') ? $"<font color='#7CFC00'>{Escape(word)}</font>" : Escape(word))));
        return html.ToString();
    }

    static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>When the panel is up, per player slot, kept free of CounterStrikeSharp so the bridge's --selftest drives it on a handed
/// clock. The plugin feeds it events and ticks and sends each player what Frame returns: the HTML, or a single space - how
/// CounterStrikeSharp's own center menu clears the panel.</summary>
public sealed class GuidePanel
{
    public const double SpawnDelaySeconds = 1.5; // after a spawn, let the HUD settle before the panel goes up
    public const double WaitLimitSeconds = 20;   // a player who never spawns (spectating, picking a team) gets it anyway after this
    public const string Clear = " ";

    sealed class View { public DateTime From, Until; public bool Closing; }
    readonly Dictionary<int, View> views = new();
    readonly Dictionary<int, DateTime> waiting = new();
    readonly string html;
    readonly double seconds;

    /// <summary>The welcome panel (Guide.Html, Guide.ShowSeconds), or any other centre panel on the same clock - the tagline at a join.</summary>
    public GuidePanel(string? html = null, double seconds = Guide.ShowSeconds)
    {
        this.html = html ?? Guide.Html();
        this.seconds = seconds;
    }

    public bool Idle => views.Count == 0 && waiting.Count == 0;

    /// <summary>The bridge asked (css_guide): right away for a player who is alive, else at their next spawn.</summary>
    public void Request(int slot, bool alive, DateTime now)
    {
        if (alive) Open(slot, now.AddSeconds(SpawnDelaySeconds));
        else waiting[slot] = now;
    }

    public void Spawned(int slot, DateTime now)
    {
        if (waiting.Remove(slot)) Open(slot, now.AddSeconds(SpawnDelaySeconds));
    }

    public void Gone(int slot) { views.Remove(slot); waiting.Remove(slot); }

    /// <summary>!info shows it again, from now; !info off clears it.</summary>
    public void Command(int slot, string arg, DateTime now)
    {
        waiting.Remove(slot);
        if (arg.Trim().ToLowerInvariant() is "off" or "close" or "stop" or "0") { if (views.TryGetValue(slot, out var shut)) shut.Closing = true; return; }
        Open(slot, now);
    }

    /// <summary>One tick: what each slot with the panel up should be sent now. After Guide.ShowSeconds, and after !info off, the
    /// slot gets Clear once and is forgotten.</summary>
    public List<(int Slot, string Html)> Frame(DateTime now)
    {
        var frame = new List<(int, string)>();
        foreach (var (slot, at) in waiting.ToList())
            if ((now - at).TotalSeconds >= WaitLimitSeconds) { waiting.Remove(slot); Open(slot, now); }
        foreach (var (slot, v) in views.ToList())
        {
            if (!v.Closing && now < v.From) continue;
            if (v.Closing || now >= v.Until) { views.Remove(slot); frame.Add((slot, Clear)); }
            else frame.Add((slot, html));
        }
        return frame;
    }

    void Open(int slot, DateTime from) => views[slot] = new View { From = from, Until = from.AddSeconds(seconds) };
}
