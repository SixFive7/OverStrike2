// The vote for the next map (the only way the map changes but for the dice's few automatic changes), the part both programs share: the rules'
// numbers, the click the plugin sends, and the codec of the vote's lines in the live state. The rules themselves are the bridge's
// (VoteRound.cs); docs/reference.md ("The vote and the game modes") says them in full.
//
// Shared like Hud.cs: the bridge compiles it and the OverStrike2 plugin links it in to draw the screen and freeze the players. Plain BCL only.
// The lines ride at the end of the /hud/<secret> text, after HudState's own; HudState.Decode skips them and VoteState.Decode reads only them, so
// one text serves both.
using System.Globalization;
using System.Text;

public static class MapVote
{
    public const int Seconds = 60;        // the vote's limit, the safety net for somebody away (while IdleLimitSeconds is 0); everybody having voted ends it sooner (InactivitySeconds)
    public const int PageCards = 8;       // one page of the ballot: the Maps page's eight cards. The ballot fills one page; a second only when what must be on it does not fit
    public const int MaxMaps = 48;        // a ballot's cards at the most - six pages - however many maps players put on it; Random comes on top
    public const int MaxOptions = MaxMaps + 1;
    public const int WaitSeconds = 120;   // after any vote, this long before anybody can call another; a match end opens its vote anyway

    /// <summary>The inactivity timer: once everybody present has voted, the vote closes this long after the last activity - a vote cast, changed or
    /// taken back, a map put on it, or a player's work in the map browser that reaches the server (a page, a sort, a filter, a search; scrolling and
    /// hovering never do) - or at the vote's limit (Seconds), whichever comes first. The live state carries both: Inactivity with who reset it last,
    /// and SecondsLeft. 0: it closes the moment the last one votes.</summary>
    public static int InactivitySeconds = 5;

    /// <summary>A second inactivity limit, off: in place of the fixed Seconds, the vote would end this long after the last activity by anyone
    /// while somebody has not voted, HardCapSeconds after it opened at the most. 0: the fixed limit (a proposal of 30 is open).</summary>
    public static int IdleLimitSeconds = 0;
    public const int HardCapSeconds = 180;

    /// <summary>The map browser's activity while a vote is open, which resets the inactivity timer: !mvb &lt;round&gt;, sent by the plugin for a
    /// page, a sort, a filter or a search a player made in it.</summary>
    public const string BusyVerb = "!mvb";

    public static string Busy(long round) => $"{BusyVerb} {round.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Random's number on the wire: a click on Random's button is option 99 (Random has no number a player sees or types).</summary>
    public const int RandomOption = 99;

    /// <summary>How the cards are numbered: not at all - a player votes by clicking a card. Fixed (a number per map, what chat typed after !vote)
    /// and Position (the card's place) stay one word away; every word and parse goes through VoteState.Shown and VoteState.Typed.</summary>
    public enum Numbering { None, Fixed, Position }
    public static Numbering Numbers = Numbering.None;

    /// <summary>The click's verb on /act: !mv &lt;round&gt; &lt;n&gt; casts or changes the player's vote, n 0 takes it back, n 99 is Random.</summary>
    public const string Verb = "!mv";

    public static string Click(long round, int option) => $"{Verb} {round.ToString(CultureInfo.InvariantCulture)} {option.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether a click may say this: !mv, a round of 1-12 digits and an option of 1-2. The bridge checks the round and the option.</summary>
    public static bool IsAction(string command)
    {
        var p = command.Split(' ');
        static bool Round(string r) => r.Length is > 0 and <= 12 && r.All(char.IsAsciiDigit);
        return p.Length == 3 && p[0] == Verb && Round(p[1]) && p[2].Length is > 0 and <= 2 && p[2].All(char.IsAsciiDigit)
            || p.Length == 2 && p[0] == BusyVerb && Round(p[1]);
    }

    /// <summary>The click's round and option, or null when it is not a vote click.</summary>
    public static (long Round, int Option)? ParseClick(string command) =>
        IsAction(command) && command.Split(' ') is [Verb, var r, var n] ? (long.Parse(r, CultureInfo.InvariantCulture), int.Parse(n, CultureInfo.InvariantCulture)) : null;

    /// <summary>"1 min 20 s", "45 s": the wait before the next vote, as notes and the board say it.</summary>
    public static string Left(int seconds) => seconds >= 60 ? $"{seconds / 60} min {seconds % 60} s" : $"{Math.Max(1, seconds)} s";
}

/// <summary>How an option got onto the ballot: a map a player put on it (Play, !play, Previous - with that player's vote), a map the dice put
/// there (one of each other mode, and the fill of the server's mode), Random - the dice after the vote - or Keep: the map that is on stays.</summary>
public enum VoteSource { Picked, Rolled, Random, Keep }

/// <summary>The vote and the game modes as the plugin reads them off the live state. See docs/reference.md for every field.</summary>
public sealed class VoteState
{
    public sealed record ModeInfo(string Id, string Name, int MaxPlayers, bool Offered);

    /// <summary>One card, or Random: Tags are the map's tags (MapTags) as the ballot showed them when the card went on it, PICKED first for a
    /// player's pick - the screen adds the vote's own (next, lead, tie). An older plugin reads the first ten fields.</summary>
    public sealed record Option(int N, string Mode, VoteSource Source, string Arg, string Key, string Label, string Size, int Votes, string Who, string Tags = "");
    public sealed record Outcome(int N, string How, string Label, string Mode);

    public string Mode = "", ModeName = "";           // the mode the server is in now
    public List<ModeInfo> Modes = new();              // every mode, in menu order
    public int WaitFor;                               // seconds before anybody can call a vote again; 0: they can
    public long Round;                                // 0: no vote
    public bool Open;                                 // voting now; false with a Round: decided, the change counting down
    public int SecondsLeft, Present, Cast;            // while open: the vote's limit's seconds left (MapVote.Seconds), always; decided: the change's countdown
    public bool MatchEnded;                           // the match is over: Keep reads "Play this map again", and nobody voting is Random
    public bool Freeze;                               // everybody frozen: the vote is open, or its result counts down to a map change
    public int Inactivity;                            // everybody has voted: the inactivity timer's seconds left, never more than SecondsLeft (MapVote.InactivitySeconds; 0: not counting)
    public string ResetBy = "";                       // ...and who reset it last (with Inactivity only)
    public long Starter;                              // the account that called it (0 at a match end)
    public List<Option> Options = new();              // in screen order - the most votes first - Random last
    public Dictionary<long, int> Choices = new();     // account id -> the option it voted for
    public Dictionary<int, List<string>> Voters = new(); // option -> the names of the players whose vote is on it (the cards' chips; the fakes too)
    public List<string> Waiting = new();              // the players present who have not voted yet, by name
    public Outcome? Result;                           // once decided

    static string Source(VoteSource s) => s switch { VoteSource.Picked => "picked", VoteSource.Rolled => "rolled", VoteSource.Keep => "keep", _ => "random" };
    static VoteSource? Source(string s) => s switch { "picked" => VoteSource.Picked, "rolled" => VoteSource.Rolled, "random" => VoteSource.Random, "keep" => VoteSource.Keep, _ => null };

    /// <summary>The lines, to append to HudState's text (no header of their own).</summary>
    public string Encode()
    {
        var s = new StringBuilder();
        void Line(params object[] fields) => s.Append(string.Join('\t', fields.Select(f => f is string t ? Hud.Clean(t) : Convert.ToString(f, CultureInfo.InvariantCulture)))).Append('\n');
        if (Mode != "") Line("mode", Mode, ModeName);
        foreach (var m in Modes) Line("mi", m.Id, m.Name, m.MaxPlayers, m.Offered ? 1 : 0);
        if (WaitFor > 0) Line("mvwait", WaitFor);
        if (Round <= 0) return s.ToString();
        Line("mv", Round, Open ? "open" : "done", SecondsLeft, Present, Cast, MatchEnded ? "end" : "mid", Starter, Freeze ? 1 : 0, Inactivity, Hud.Clean(ResetBy, 24));
        foreach (var o in Options) Line("mvo", o.N, o.Mode, Source(o.Source), o.Arg, o.Key, Hud.Clean(o.Label, 40), o.Size, o.Votes, o.Who, MapTags.Clean(o.Tags));
        foreach (var (account, n) in Choices) Line("mvv", account, n);
        foreach (var (n, names) in Voters) if (names.Count > 0) Line(["mvn", n, .. names.Select(x => (object)Hud.Clean(x, 24))]);
        if (Waiting.Count > 0) Line(["mvw", .. Waiting.Select(x => (object)Hud.Clean(x, 24))]);
        if (Result is { } r) Line("mvr", r.N, r.How, Hud.Clean(r.Label, 40), r.Mode);
        return s.ToString();
    }

    /// <summary>The vote's lines out of the live state's text; everything else in it is skipped. Never null: no lines, no vote.</summary>
    public static VoteState Decode(string? text)
    {
        var v = new VoteState();
        static int Int(string[] f, int i) => i < f.Length && int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        static long Long(string[] f, int i) => i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        static string Str(string[] f, int i) => i < f.Length ? f[i] : "";
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            switch (f[0])
            {
                case "mode": (v.Mode, v.ModeName) = (Str(f, 1), Str(f, 2)); break;
                case "mi" when Str(f, 1) != "": v.Modes.Add(new ModeInfo(Str(f, 1), Str(f, 2), Int(f, 3), Int(f, 4) == 1)); break;
                case "mvwait": v.WaitFor = Math.Max(0, Int(f, 1)); break;
                case "mv" when Long(f, 1) > 0:
                    (v.Round, v.Open, v.SecondsLeft, v.Present, v.Cast, v.MatchEnded, v.Starter, v.Freeze, v.Inactivity, v.ResetBy) =
                        (Long(f, 1), Str(f, 2) == "open", Int(f, 3), Int(f, 4), Int(f, 5), Str(f, 6) == "end", Math.Max(0, Long(f, 7)), Int(f, 8) == 1, Math.Max(0, Int(f, 9)), Str(f, 10));
                    break;
                case "mvo" when Int(f, 1) > 0 && Source(Str(f, 3)) is { } src && v.Options.Count < MapVote.MaxOptions:
                    v.Options.Add(new Option(Int(f, 1), Str(f, 2), src, Str(f, 4), Str(f, 5), Str(f, 6), Str(f, 7), Int(f, 8), Str(f, 9), MapTags.Clean(Str(f, 10))));
                    break;
                case "mvv" when Long(f, 1) >= 0 && Int(f, 2) > 0: v.Choices[Long(f, 1)] = Int(f, 2); break;
                case "mvn" when Int(f, 1) > 0: v.Voters[Int(f, 1)] = f.Skip(2).Where(x => x != "").Take(64).ToList(); break;
                case "mvw": v.Waiting = f.Skip(1).Where(x => x != "").Take(64).ToList(); break;
                case "mvr" when Int(f, 1) > 0: v.Result = new Outcome(Int(f, 1), Str(f, 2), Str(f, 3), Str(f, 4)); break;
            }
        }
        if (v.Round <= 0) { v.Options.Clear(); v.Choices.Clear(); v.Voters.Clear(); v.Waiting.Clear(); v.Result = null; v.Freeze = false; v.Inactivity = 0; v.ResetBy = ""; } // option lines without a vote line are not a vote
        return v;
    }

    /// <summary>The option this player voted for, 0 for none.</summary>
    public int ChoiceOf(long account) => Choices.GetValueOrDefault(account);

    /// <summary>The option with this number (the wire's), or null.</summary>
    public Option? Numbered(int n) => Options.FirstOrDefault(o => o.N == n);

    /// <summary>The cards of the ballot, in screen order: every option but Random (a button of its own, not a card).</summary>
    public List<Option> Maps => Options.Where(o => o.Source != VoteSource.Random).ToList();

    /// <summary>Random, or null.</summary>
    public Option? Random => Options.FirstOrDefault(o => o.Source == VoteSource.Random);

    /// <summary>The number a card shows (MapVote.Numbers): its fixed number, its place on the ballot, or 0 for none.</summary>
    public int Shown(Option o) => o.Source == VoteSource.Random ? 0 : MapVote.Numbers switch
    {
        MapVote.Numbering.Fixed => o.N,
        MapVote.Numbering.Position => Maps.FindIndex(x => x.N == o.N) + 1,
        _ => 0,
    };

    /// <summary>The card a typed number names (!vote 3, or a bare 3 in chat), or null: none such, or no numbers at all.</summary>
    public Option? Typed(int k) => MapVote.Numbers switch
    {
        MapVote.Numbering.Fixed => Maps.FirstOrDefault(o => o.N == k),
        MapVote.Numbering.Position => k >= 1 && k <= Maps.Count ? Maps[k - 1] : null,
        _ => null,
    };

    /// <summary>The highest number chat may type: a bare number above it stays plain chat; 0 when cards have no numbers.</summary>
    public int Highest => Maps.Count == 0 ? 0 : MapVote.Numbers switch
    {
        MapVote.Numbering.Fixed => Maps.Max(o => o.N),
        MapVote.Numbering.Position => Maps.Count,
        _ => 0,
    };
}
