// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text;

/// <summary>How an option got onto the ballot: a map a player put on it (Play, !play, Previous - with that player's vote), a map the dice put
/// there (one of each other mode, and the fill of the server's mode), Random - the dice after the vote - or Keep: the map that is on stays.</summary>
public enum VoteSource { Picked, Rolled, Random, Keep }

/// <summary>The vote as the plugin reads it off the live state. See docs/reference.md for every field.</summary>
public sealed class VoteState
{
    /// <summary>One card, or Random: Tags are the map's tags (MapTags) as the ballot showed them when the card went on it, PICKED first for a
    /// player's pick - the screen adds the vote's own (next, lead, tie).</summary>
    public sealed record Option(int N, string Mode, VoteSource Source, string Key, string Label, string Size, int Votes, string Who, string Tags = "");
    public sealed record Outcome(int N, string How, string Label);

    public int WaitFor;                               // seconds before anybody can call a vote again; 0: they can
    public long Round;                                // 0: no vote
    public bool Open;                                 // voting now; false with a Round: decided, the change counting down, or the map kept (Kept), the window closing
    public int SecondsLeft, Present, Cast;            // while open: the vote's limit's seconds left (MapVote.Seconds), always; decided: the change's countdown, or the window's (Kept)
    public int DoneSeconds;                           // decided: how long that countdown runs in all (its bar's full length)
    public bool MatchEnded;                           // the match is over: Keep reads "Play this map again", and nobody voting is Random
    public bool NextMatch;                            // decided, the map played again, and the next match starts as the countdown ends: the plugin ends the match-end screen then (NextMatch.cs)
    public bool Freeze;                               // everybody frozen: the vote is open, or its result counts down - to a map change, or to the window's close
    public int Inactivity;                            // everybody has voted: the inactivity timer's seconds left, never more than SecondsLeft (MapVote.InactivitySeconds; 0: not counting)
    public long EndsAtMs, InactivityAtMs, WaitUntilMs; // the end times behind SecondsLeft, Inactivity and WaitFor, unix milliseconds (0: none): the change-driven text carries these (AsOf)
    public string ResetBy = "";                       // ...and who reset it last (with Inactivity only)
    public string StarterName = "";                   // who called it, as the window says it ("" at a match end)
    public List<Option> Options = new();              // in screen order - the most votes first, the winner first once decided - Random last; a player's view: ForPlayer
    public Dictionary<long, int> Choices = new();     // account id -> the option it voted for
    public Dictionary<int, List<string>> Voters = new(); // option -> the names of the players whose vote is on it (the cards' chips; the fakes too)
    public List<string> Waiting = new();              // the players present who have not voted yet, by name
    public Outcome? Result;                           // once decided

    /// <summary>Decided, on the Keep card: nothing changes, the window closes when its seconds are up.</summary>
    public bool Kept => !Open && Result is { } r && Numbered(r.N)?.Source == VoteSource.Keep;

    /// <summary>Kept at a match end, and the next match starts as the window closes (NextMatch): what its countdown counts to.</summary>
    public bool NextMatchCounts => Kept && NextMatch;

    static string Source(VoteSource s) => s switch { VoteSource.Picked => "picked", VoteSource.Rolled => "rolled", VoteSource.Keep => "keep", _ => "random" };
    static VoteSource? Source(string s) => s switch { "picked" => VoteSource.Picked, "rolled" => VoteSource.Rolled, "random" => VoteSource.Random, "keep" => VoteSource.Keep, _ => null };

    /// <summary>The vote as it stands at this moment (unix milliseconds on the bridge's clock): its clocks worked out from their end times. A text
    /// without end times (an older bridge) is as it came.</summary>
    public VoteState AsOf(long nowMs)
    {
        var v = (VoteState)MemberwiseClone();
        int Secs(long until) => (int)Math.Ceiling((until - nowMs) / 1000.0);
        if (EndsAtMs > 0) v.SecondsLeft = Math.Max(0, Secs(EndsAtMs));
        if (InactivityAtMs > 0) v.Inactivity = Math.Min(Math.Max(1, Secs(InactivityAtMs)), Math.Max(1, v.SecondsLeft));
        if (WaitUntilMs > 0) v.WaitFor = Math.Max(0, Secs(WaitUntilMs));
        return v;
    }

    /// <summary>The lines, to append to HudState's text (no header of their own). endTimes: the change-driven text - end times in place of the
    /// seconds that count down (HudTransport.cs). The plugin reads mv and mvo by place, so a field nothing reads any more keeps its place, empty:
    /// mv's 7th (0) and mvo's 4th ("").</summary>
    public string Encode(bool endTimes = false)
    {
        var s = new StringBuilder();
        void Line(params object[] fields) => s.Append(string.Join('\t', fields.Select(f => f is string t ? Hud.Clean(t) : Convert.ToString(f, CultureInfo.InvariantCulture)))).Append('\n');
        if (WaitFor > 0 && endTimes) Line("mvwaitat", WaitUntilMs);
        else if (WaitFor > 0) Line("mvwait", WaitFor);
        if (Round <= 0) return s.ToString();
        Line("mv", Round, Open ? "open" : "done", endTimes ? 0 : SecondsLeft, Present, Cast, MatchEnded ? "end" : "mid", 0, Freeze ? 1 : 0, endTimes ? (Inactivity > 0 ? 1 : 0) : Inactivity, Hud.Clean(ResetBy, 24), Hud.Clean(StarterName, 24), DoneSeconds, NextMatch ? 1 : 0);
        if (endTimes) Line("mvt", EndsAtMs, Inactivity > 0 ? InactivityAtMs : 0);
        foreach (var o in Options) Line("mvo", o.N, o.Mode, Source(o.Source), "", o.Key, Hud.Clean(o.Label, 40), o.Size, o.Votes, o.Who, MapTags.Clean(o.Tags));
        foreach (var (account, n) in Choices) Line("mvv", account, n);
        foreach (var (n, names) in Voters) if (names.Count > 0) Line(["mvn", n, .. names.Select(x => (object)Hud.Clean(x, 24))]);
        if (Waiting.Count > 0) Line(["mvw", .. Waiting.Select(x => (object)Hud.Clean(x, 24))]);
        if (Result is { } r) Line("mvr", r.N, r.How, Hud.Clean(r.Label, 40));
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
                case "mvwait": v.WaitFor = Math.Max(0, Int(f, 1)); break;
                case "mvwaitat" when Long(f, 1) > 0: (v.WaitFor, v.WaitUntilMs) = (1, Long(f, 1)); break;
                case "mvt": (v.EndsAtMs, v.InactivityAtMs) = (Math.Max(0, Long(f, 1)), Math.Max(0, Long(f, 2))); break;
                case "mv" when Long(f, 1) > 0:
                    (v.Round, v.Open, v.SecondsLeft, v.Present, v.Cast, v.MatchEnded, v.Freeze, v.Inactivity, v.ResetBy, v.StarterName, v.DoneSeconds, v.NextMatch) =
                        (Long(f, 1), Str(f, 2) == "open", Int(f, 3), Int(f, 4), Int(f, 5), Str(f, 6) == "end", Int(f, 8) == 1, Math.Max(0, Int(f, 9)), Str(f, 10), Str(f, 11), Math.Max(0, Int(f, 12)), Int(f, 13) == 1);
                    break;
                case "mvo" when Int(f, 1) > 0 && Source(Str(f, 3)) is { } src && v.Options.Count < MapVote.MaxOptions:
                    v.Options.Add(new Option(Int(f, 1), Str(f, 2), src, Str(f, 5), Str(f, 6), Str(f, 7), Int(f, 8), Str(f, 9), MapTags.Clean(Str(f, 10))));
                    break;
                case "mvv" when Long(f, 1) >= 0 && Int(f, 2) > 0: v.Choices[Long(f, 1)] = Int(f, 2); break;
                case "mvn" when Int(f, 1) > 0: v.Voters[Int(f, 1)] = f.Skip(2).Where(x => x != "").Take(64).ToList(); break;
                case "mvw": v.Waiting = f.Skip(1).Where(x => x != "").Take(64).ToList(); break;
                case "mvr" when Int(f, 1) > 0: v.Result = new Outcome(Int(f, 1), Str(f, 2), Str(f, 3)); break;
            }
        }
        if (v.Round <= 0) { v.Options.Clear(); v.Choices.Clear(); v.Voters.Clear(); v.Waiting.Clear(); v.Result = null; v.Freeze = false; v.Inactivity = 0; v.ResetBy = ""; v.StarterName = ""; v.DoneSeconds = 0; v.NextMatch = false; } // option lines without a vote line are not a vote
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

    /// <summary>The vote as this player sees it: while it is open, the card their vote is on first, the rest as the bridge sorts them -
    /// the most votes first; a vote on Random, no vote, or a vote decided, as it is. account: the player's; -1 when not known.</summary>
    public VoteState ForPlayer(long account)
    {
        if (!Open || account < 0 || ChoiceOf(account) is not (> 0 and var mine)
            || Options.FindIndex(o => o.N == mine && o.Source != VoteSource.Random) is not (> 0 and var at)) return this;
        var v = (VoteState)MemberwiseClone();
        v.Options = [Options[at], .. Options.Where((o, i) => i != at)];
        return v;
    }

}
