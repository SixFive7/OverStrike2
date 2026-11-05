// The vote for the next map (the only way the map changes but for the dice's few automatic changes), the part both programs share: the rules'
// numbers, the click the plugin sends, and the codec of the vote's lines in the live state. The rules themselves are the bridge's
// (VoteRound.cs); docs/reference.md ("The vote and the game modes") says them in full.
//
// Shared like Hud.cs: the bridge compiles it and the OverStrike2 plugin links it in to draw the screen and freeze the players. Plain BCL only.
// The lines ride at the end of the /hud/<secret> text, after HudState's own; HudState.Decode skips them and VoteState.Decode reads only them, so
// one text serves both.
using System.Globalization;

public static class MapVote
{
    public const int Seconds = 60;        // the vote's limit, the safety net for somebody away (while IdleLimitSeconds is 0); everybody having voted ends it sooner (InactivitySeconds)
    public const int PageCards = 10;      // one page of the ballot: ten cards, two columns of five. The ballot fills one page; a second only when what must be on it does not fit
    public const int MaxMaps = 48;        // a ballot's cards at the most - six pages - however many maps players put on it; Random comes on top
    public const int MaxOptions = MaxMaps + 1;
    public const int WaitSeconds = 120;   // after any vote, this long before anybody can call another; a match end opens its vote anyway
    public const int KeptSeconds = 8;     // the vote kept the map: everybody's window shows that for this long, everybody still frozen, and closes at the same moment

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
