// The bridge's half of the message system (Messages.cs is the catalogue: which surface, who sees it, the chat and board copies):
//   Post(kind, text): a message of that kind, routed by the catalogue. Its text may carry marks (M), which chat renders as colours and every other
//     surface drops. It rides the live state to the plugin (HudState: the top line, the note, the Lately lines, the chat lines, the centre lines);
//   Tell(steamId, text): the answer to what that player clicked (Messages.Answer): the line in their window, their chat, their board;
//   Announce(text): a plain news item (Messages.News): the top line, chat and the board for everybody.
// The contents before v4.6.0 keep their news line and note: the same words on HudState's news and note lines, which this content has no panel for.
static partial class Program
{
    public const double NoteSeconds = 8, NewsSeconds = 8;
    public const int NoticeLength = Messages.LineMax;  // a note, at most: one line in the window (two in the note panel of the older contents)
    public const int NewsLength = Messages.TopMax;     // the news line of the older contents, at most: a burst of news merges into it, up to three lines

    static readonly Dictionary<long, (string Text, DateTime Until, string Cat)> NoteFor = new();
    static (string Text, DateTime Until) NewsNow = ("", default);
    static (string Text, string Cat, DateTime At, DateTime Until) TopNow = ("", "", default, default);
    static readonly List<Messages.LatelyLine> LatelyNow = new();
    static readonly List<(long Seq, long Account, string Text, DateTime At)> SaysNow = new();
    static readonly List<(long Seq, long Account, bool Alert, string Text, DateTime Until)> CentresNow = new();
    static long SaySeq = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() * 1000; // only ever up, also through a restart of the bridge: the plugin says each line once

    /// <summary>Every notice given, newest last (the last 200): the log reads the same, and --selftest asserts on it.</summary>
    static readonly List<string> Told = new();

    /// <summary>Every message --selftest made the bridge post, for the capitals check at its end (Sections); null outside --selftest.</summary>
    static List<string>? ToldAll;

    /// <summary>A message of this kind, routed by the catalogue (Messages.cs). steamId: the player it is for or about, when the kind goes to a player. key: a line that
    /// replaces its own older version on the board. seconds: how long, when not the kind's. chat: false leaves chat out this once (a refreshed line says it only at its steps).</summary>
    static void Post(Messages.Kind kind, string text, string steamId = "", string key = "", double? seconds = null, bool chat = true)
    {
        var now = Now();
        var plain = Hud.Clean(M.Plain(text), kind.Surface == Surface.Line ? NoticeLength : NewsLength);
        var secs = seconds ?? (kind.Seconds > 0 ? kind.Seconds : NewsSeconds);
        var account = steamId != "" ? Math.Max(0, AccountId(steamId)) : 0;
        Log($"{kind.Id}{(steamId != "" ? $" {steamId}" : "")}: {plain}");
        Keep(plain);
        ToldAll?.Add(text);
        switch (kind.Surface)
        {
            case Surface.Top: TopNow = (plain, kind.Cat.Id, now, now.AddSeconds(secs)); break;
            case Surface.Line when kind.On == Who.Player && account > 0: NoteFor[account] = (plain, now.AddSeconds(secs), kind.Cat.Id); break;
            case Surface.Plain or Surface.Alert:
                CentresNow.Add((++SaySeq, kind.On == Who.Player ? account : 0, kind.Surface == Surface.Alert, Hud.Clean(text, Messages.CentreMax), now.AddSeconds(secs)));
                break;
        }
        if (kind.News) NewsNow = (plain, now.AddSeconds(secs));
        if (chat && (kind.Chat == Who.Everybody || (kind.Chat == Who.Player && account > 0)))
            SaysNow.Add((++SaySeq, kind.Chat == Who.Player ? account : 0, Hud.Clean(text, 400), now));
        if (kind.Board == Who.Everybody || (kind.Board == Who.Player && account > 0))
            Messages.AddLately(LatelyNow, new Messages.LatelyLine(HudState.Ms(now), kind.Board == Who.Player ? account : 0, key, plain));
    }

    /// <summary>The answer to what this player clicked: for them alone, for a few seconds in their window; a newer one replaces it.</summary>
    static void Tell(string steamId, string text, double seconds = NoteSeconds) => Post(Messages.Answer, text, steamId, seconds: seconds);

    /// <summary>A plain news item for everybody.</summary>
    static void Announce(string text, double seconds = NewsSeconds) => Post(Messages.News, text, seconds: seconds);

    static void Keep(string line)
    {
        Told.Add(line);
        if (Told.Count > 200) Told.RemoveRange(0, Told.Count - 200);
    }

    /// <summary>The messages still on, into the live state; what has run out goes.</summary>
    static void PutNotices(HudState h, DateTime now)
    {
        if (now < NewsNow.Until && NewsNow.Text != "") (h.News, h.NewsFor, h.NewsUntilMs) = (NewsNow.Text, (int)Math.Ceiling((NewsNow.Until - now).TotalSeconds), HudState.Ms(NewsNow.Until));
        if (now < TopNow.Until && TopNow.Text != "") (h.TopText, h.TopCat, h.TopAtMs, h.TopUntilMs) = (TopNow.Text, TopNow.Cat, HudState.Ms(TopNow.At), HudState.Ms(TopNow.Until));
        foreach (var (account, (text, until, cat)) in NoteFor.ToList())
        {
            if (now >= until) { NoteFor.Remove(account); continue; }
            h.Notes[account] = (text, (int)Math.Ceiling((until - now).TotalSeconds));
            h.NoteUntilMs[account] = HudState.Ms(until);
            h.NoteCats[account] = cat;
        }
        h.Lately.AddRange(LatelyNow);
        SaysNow.RemoveAll(l => (now - l.At).TotalSeconds > Messages.SayKeepSeconds);
        h.Says.AddRange(SaysNow.Select(l => (l.Seq, l.Account, l.Text)));
        CentresNow.RemoveAll(l => now >= l.Until);
        h.Centres.AddRange(CentresNow.Select(l => (l.Seq, l.Account, l.Alert, HudState.Ms(l.Until), l.Text)));
    }

    static void ClearNotices()
    {
        NoteFor.Clear(); NewsNow = ("", default); TopNow = ("", "", default, default); LatelyNow.Clear(); SaysNow.Clear(); CentresNow.Clear(); Told.Clear();
    }
}
