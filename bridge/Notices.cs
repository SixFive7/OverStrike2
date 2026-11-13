// The bridge's half of the message system (Messages.cs is the catalogue and its routing: which surface, who sees it, the chat and board copies):
//   Post(kind, text): a message of that kind, where Messages.Route sends it. Its text may carry marks (M), which chat renders as colours and every other
//     surface drops. It rides the live state to the plugin (HudState: the top line, the note, the Lately lines, the chat lines, the centre lines);
//   Tell(steamId, text): the answer to what that player clicked (Messages.Answer): the line in their window, their chat, their board;
//   Announce(text): a plain news item (Messages.News): the top line, chat and the board for everybody.
static partial class Program
{
    static readonly Dictionary<long, (string Text, DateTime Until, string Cat)> NoteFor = new();
    static (string Text, string Cat, DateTime At, DateTime Until) TopNow = ("", "", default, default);
    static readonly List<Messages.LatelyLine> LatelyNow = new();
    static readonly List<(long Seq, long Account, string Text, DateTime At)> SaysNow = new();
    static readonly List<(long Seq, long Account, bool Alert, string Text, DateTime Until)> CentresNow = new();
    static long SaySeq = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() * 1000; // only ever up, also through a restart of the bridge: the plugin says each line once

    /// <summary>Every notice given, newest last (the last 200): the log reads the same, and --selftest asserts on it.</summary>
    static readonly List<string> Told = new();

    /// <summary>Every message --selftest made the bridge post, for the capitals check at its end (Sections); null outside --selftest.</summary>
    static List<string>? ToldAll;

    /// <summary>A message of this kind, where the catalogue routes it (Messages.Route). steamId: the player it is for or about, when the kind goes to a player.
    /// key: a line that replaces its own older version on the board. seconds: how long, when not the kind's. chat: false leaves chat out this once (a refreshed
    /// line says it only at its steps).</summary>
    static void Post(Messages.Kind kind, string text, string steamId = "", string key = "", double? seconds = null, bool chat = true)
    {
        ToldAll?.Add(text); // as written: the self-test holds the templates themselves to the capitals rule
        var m = Messages.Route(kind, text, steamId != "" ? Math.Max(0, AccountId(steamId)) : 0, seconds, chat);
        var now = Now();
        var until = now.AddSeconds(m.Seconds);
        Log($"{kind.Id}{(steamId != "" ? $" {steamId}" : "")}: {m.Plain}");
        Keep(m.Plain);
        if (m.On is { } on)
            switch (kind.Surface)
            {
                case Surface.Top: TopNow = (m.Plain, kind.Cat.Id, now, until); break;
                case Surface.Line: NoteFor[on] = (m.Plain, until, kind.Cat.Id); break;
                case Surface.Plain or Surface.Alert: CentresNow.Add((++SaySeq, on, kind.Surface == Surface.Alert, Hud.Clean(m.Text, Messages.CentreMax), until)); break;
            }
        if (m.Chat is { } to) SaysNow.Add((++SaySeq, to, Hud.Clean(m.Text, Messages.ChatMax), now));
        if (m.Board is { } board) Messages.AddLately(LatelyNow, new Messages.LatelyLine(HudState.Ms(now), board, key, m.Plain));
    }

    /// <summary>The answer to what this player clicked: for them alone, for a few seconds in their window; a newer one replaces it.</summary>
    static void Tell(string steamId, string text, double? seconds = null) => Post(Messages.Answer, text, steamId, seconds: seconds);

    /// <summary>This player's note goes now, if it is still the ask isIt knows: the confirmation it asked for is settled - confirmed, or made moot by another
    /// player's first click on the same button, which takes the window over - so it would only mislead for the rest of its seconds (the live lab's movie of
    /// release 214, clip e1). A newer note of theirs stays.</summary>
    static void Withdraw(string steamId, Func<string, bool> isIt)
    {
        var account = AccountId(steamId);
        if (account > 0 && NoteFor.TryGetValue(account, out var note) && isIt(note.Text)) NoteFor.Remove(account);
    }

    /// <summary>A plain news item for everybody.</summary>
    static void Announce(string text, double? seconds = null) => Post(Messages.News, text, seconds: seconds);

    static void Keep(string line)
    {
        Told.Add(line);
        if (Told.Count > 200) Told.RemoveRange(0, Told.Count - 200);
    }

    /// <summary>The messages still on, into the live state; what has run out goes.</summary>
    static void PutNotices(HudState h, DateTime now)
    {
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
        NoteFor.Clear(); TopNow = ("", "", default, default); LatelyNow.Clear(); SaysNow.Clear(); CentresNow.Clear(); Told.Clear();
    }
}
