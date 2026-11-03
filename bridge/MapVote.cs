// The end-of-match vote (#26, the owner's decision of 2026-11-01; its ballot the owner's of 2026-11-03), the part both programs share: the
// rules' numbers, the click the plugin sends, and the codec of the vote's lines in the live state. When a match ends, every player gets a vote
// screen for MapVote.Seconds, or until a strict majority of the players present has voted for one option; the ballot holds every queued
// request (each requester's vote already cast on their first), a map of each other mode, random maps of the server's mode until it holds at
// least MinMaps maps, and Random. A player may add a map from the map browser while it is open (the owner's 2026-11-03): it goes on the
// ballot for everybody, with that player's vote. The rules themselves are the bridge's (VoteRound.cs); docs/reference.md ("The end-of-match
// vote and the game modes") says them in full.
//
// Shared like Hud.cs: the bridge compiles it and the OverStrike2 plugin links it in to draw the screen. Plain BCL only. The lines ride at the
// end of the /hud/<secret> text, after HudState's own; HudState.Decode skips them and VoteState.Decode reads only them, so one text serves both.
using System.Globalization;
using System.Text;

public static class MapVote
{
    public const int Seconds = 60;     // the vote's timer (the owner's 60 s), or until a strict majority agrees (his 2026-11-01)
    public const int MinMaps = 8;      // the ballot holds at least this many maps (the owner's 2026-11-03): the cards of one page
    public const int MaxMaps = 48;     // and at most this many - six pages of eight - however many are queued or added; Random comes on top
    public const int MaxOptions = MaxMaps + 1;
    public const int MaxAdded = 1;     // maps one player may add to one vote from the map browser (each comes with that player's vote)

    /// <summary>The click's verb on /act: !mv &lt;round&gt; &lt;n&gt; casts or changes the player's vote, n 0 takes it back.</summary>
    public const string Verb = "!mv";

    /// <summary>What a player hears who asks for another map while the vote is open (the owner's 26, 2026-11-02): in chat ChatCommands.VoteOnlyLines, on a click WaitLine.</summary>
    public const string IsOn = "The vote for the next map is on", RequestsReopen = "Requests reopen after the vote.",
        WaitLine = IsOn + ": click a card or type its number - a map's Play adds it to the vote. " + RequestsReopen;

    /// <summary>What the plugin adds to its /hud poll to say it draws the vote: without a poll carrying it for 15 s the bridge opens no vote.</summary>
    public const string Capability = "mv=1";

    public static string Click(long round, int option) => $"{Verb} {round.ToString(CultureInfo.InvariantCulture)} {option.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether a click may say this: !mv, a round of 1-12 digits and an option of 1-2. The bridge checks the round and the option.</summary>
    public static bool IsAction(string command)
    {
        var p = command.Split(' ');
        return p.Length == 3 && p[0] == Verb && p[1].Length is > 0 and <= 12 && p[1].All(char.IsAsciiDigit) && p[2].Length is > 0 and <= 2 && p[2].All(char.IsAsciiDigit);
    }

    /// <summary>The click's round and option, or null when it is not a vote click.</summary>
    public static (long Round, int Option)? ParseClick(string command) =>
        IsAction(command) && command.Split(' ') is [_, var r, var n] ? (long.Parse(r, CultureInfo.InvariantCulture), int.Parse(n, CultureInfo.InvariantCulture)) : null;

    /// <summary>The votes one option needs to end the vote early: a strict majority of the players present.</summary>
    public static int Needed(int present) => present / 2 + 1;
}

/// <summary>How an option got onto the ballot: a queued request (its requesters' votes cast before the screen opens), a map the dice
/// put there, Random - the dice after the vote - a map a player added while the vote was open (with that player's vote), or Keep: the map
/// that is on stays (a vote a player started with Next while a match runs; the owner's 2026-11-03).</summary>
public enum VoteSource { Queue, Rolled, Random, Added, Keep }

/// <summary>The vote and the game modes as the plugin reads them off the live state. See docs/reference.md for every field.</summary>
public sealed class VoteState
{
    public sealed record ModeInfo(string Id, string Name, int MaxPlayers, bool Offered);
    public sealed record Option(int N, string Mode, VoteSource Source, string Arg, string Key, string Label, string Size, int Votes, string Who);
    public sealed record Outcome(int N, string How, string Label, string Mode);

    public string Mode = "", ModeName = "";           // the mode the server is in now
    public List<ModeInfo> Modes = new();              // every mode, in menu order
    public long Round;                                // 0: no vote
    public bool Open;                                 // voting now; false with a Round: decided, the change counting down
    public int SecondsLeft, Present, Needed, Cast;
    public bool MidMatch;                             // started by a player's Next while a match runs: Keep is on it, and only its starter's window opens
    public long Starter;                              // that player's account id (0 at a match end)
    public List<Option> Options = new();              // in screen order, Random last; numbers never change once given, so a map added while the vote is open has a number after Random's
    public Dictionary<long, int> Choices = new();     // account id -> the option it voted for
    public Outcome? Result;                           // once decided

    static string Source(VoteSource s) => s switch { VoteSource.Queue => "queue", VoteSource.Rolled => "rolled", VoteSource.Added => "added", VoteSource.Keep => "keep", _ => "random" };
    static VoteSource? Source(string s) => s switch { "queue" => VoteSource.Queue, "rolled" => VoteSource.Rolled, "random" => VoteSource.Random, "added" => VoteSource.Added, "keep" => VoteSource.Keep, _ => null };

    /// <summary>The lines, to append to HudState's text (no header of their own).</summary>
    public string Encode()
    {
        var s = new StringBuilder();
        void Line(params object[] fields) => s.Append(string.Join('\t', fields.Select(f => f is string t ? Hud.Clean(t) : Convert.ToString(f, CultureInfo.InvariantCulture)))).Append('\n');
        if (Mode != "") Line("mode", Mode, ModeName);
        foreach (var m in Modes) Line("mi", m.Id, m.Name, m.MaxPlayers, m.Offered ? 1 : 0);
        if (Round <= 0) return s.ToString();
        Line("mv", Round, Open ? "open" : "done", SecondsLeft, Present, Needed, Cast, MidMatch ? "mid" : "end", Starter);
        foreach (var o in Options) Line("mvo", o.N, o.Mode, Source(o.Source), o.Arg, o.Key, Hud.Clean(o.Label, 40), o.Size, o.Votes, o.Who);
        foreach (var (account, n) in Choices) Line("mvv", account, n);
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
                case "mv" when Long(f, 1) > 0:
                    (v.Round, v.Open, v.SecondsLeft, v.Present, v.Needed, v.Cast, v.MidMatch, v.Starter) = (Long(f, 1), Str(f, 2) == "open", Int(f, 3), Int(f, 4), Int(f, 5), Int(f, 6), Str(f, 7) == "mid", Math.Max(0, Long(f, 8)));
                    break;
                case "mvo" when Int(f, 1) > 0 && Source(Str(f, 3)) is { } src && v.Options.Count < MapVote.MaxOptions:
                    v.Options.Add(new Option(Int(f, 1), Str(f, 2), src, Str(f, 4), Str(f, 5), Str(f, 6), Str(f, 7), Int(f, 8), Str(f, 9)));
                    break;
                case "mvv" when Long(f, 1) >= 0 && Int(f, 2) > 0: v.Choices[Long(f, 1)] = Int(f, 2); break;
                case "mvr" when Int(f, 1) > 0: v.Result = new Outcome(Int(f, 1), Str(f, 2), Str(f, 3), Str(f, 4)); break;
            }
        }
        if (v.Round <= 0) { v.Options.Clear(); v.Choices.Clear(); v.Result = null; } // option lines without a vote line are not a vote
        return v;
    }

    /// <summary>The option this player voted for, 0 for none.</summary>
    public int ChoiceOf(long account) => Choices.GetValueOrDefault(account);

    /// <summary>The option with this number, or null.</summary>
    public Option? Numbered(int n) => Options.FirstOrDefault(o => o.N == n);

    /// <summary>The cards of the ballot, in screen order: every option but Random (since content v4_4_0 a button of its own, not a card) - Keep
    /// playing first on a vote a player started.</summary>
    public List<Option> Maps => Options.Where(o => o.Source != VoteSource.Random).ToList();

    /// <summary>Random, or null.</summary>
    public Option? Random => Options.FirstOrDefault(o => o.Source == VoteSource.Random);

    /// <summary>The highest number on the ballot: what chat may type.</summary>
    public int Highest => Options.Count == 0 ? 0 : Options.Max(o => o.N);
}
