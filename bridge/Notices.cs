// The HUD's notices (2026-10-21). The chat interface is gone: what the bridge used to say in chat is now one of two things -
//   Tell(steamId, text): a note to one player, the answer to what they clicked - a refusal, a confirmation to give, "you voted already";
//   Announce(text): the news line everybody sees - a pick, a skip vote, the queue moving, a crash, a restart.
// Both ride the live state to the plugin (HudState "note" and "news"), which puts them on screen. Nothing goes to chat: a line a
// player types is only read for presence (who is on), never as a command - the plugin answers a typed command with one private hint.
static partial class Program
{
    public const double NoteSeconds = 8, NewsSeconds = 8;
    public const int NoticeLength = 140; // a note, at most: two lines of the layout's note panel
    public const int NewsLength = 240;   // the news line, at most: a burst of queue news is merged into it (FlushNews), up to three lines

    static readonly Dictionary<long, (string Text, DateTime Until)> NoteFor = new();
    static (string Text, DateTime Until) NewsNow = ("", default), NewsBefore = ("", default); // the news line's two slots (the owner's 63 a)

    /// <summary>Every notice given, newest last (the last 200): the log reads the same, and --selftest asserts on it.</summary>
    static readonly List<string> Told = new();

    /// <summary>A note to the player with this SteamID, shown to them alone for a few seconds; a newer one replaces it.</summary>
    static void Tell(string steamId, string text, double seconds = NoteSeconds)
    {
        var line = Hud.Clean(text, NoticeLength);
        Log($"note to {steamId}: {line}");
        Keep(line);
        if (AccountId(steamId) is > 0 and var account) NoteFor[account] = (line, Now().AddSeconds(seconds));
    }

    /// <summary>The news line, shown to everybody for a few seconds. It has two slots (the owner's 63 a): a newer line covers the one that is on,
    /// and that one comes back when the newer has gone, if its own time is not up - a fun click no longer wipes a restart's warning off it.</summary>
    static void Announce(string text, double seconds = NewsSeconds)
    {
        var line = Hud.Clean(text, NewsLength);
        Log($"news: {line}");
        Keep(line);
        var now = Now();
        if (NewsNow.Text != "" && now < NewsNow.Until && NewsNow.Text != line) NewsBefore = NewsNow;
        NewsNow = (line, now.AddSeconds(seconds));
    }

    /// <summary>The news line as it shows now: the newest line whose time is not up.</summary>
    static (string Text, DateTime Until) NewsShown(DateTime now) =>
        NewsNow.Text != "" && now < NewsNow.Until ? NewsNow : NewsBefore.Text != "" && now < NewsBefore.Until ? NewsBefore : ("", default);

    static void Keep(string line)
    {
        Told.Add(line);
        if (Told.Count > 200) Told.RemoveRange(0, Told.Count - 200);
    }

    /// <summary>The notices still on, into the live state: the news and each note with their seconds left; expired notes go.</summary>
    static void PutNotices(HudState h, DateTime now)
    {
        if (NewsShown(now) is { Text: not "" } news) (h.News, h.NewsFor, h.NewsUntilMs) = (news.Text, (int)Math.Ceiling((news.Until - now).TotalSeconds), HudState.Ms(news.Until));
        foreach (var (account, (text, until)) in NoteFor.ToList())
        {
            if (now >= until) { NoteFor.Remove(account); continue; }
            h.Notes[account] = (text, (int)Math.Ceiling((until - now).TotalSeconds));
            h.NoteUntilMs[account] = HudState.Ms(until);
        }
    }

    static void ClearNotices() { NoteFor.Clear(); NewsNow = NewsBefore = ("", default); Told.Clear(); }
}
