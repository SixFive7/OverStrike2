// The game server's log as it arrives: each line parsed (Parse) and handed to what it is about (Ingest).
using System.Text.RegularExpressions;

static partial class Program
{
    static void Ingest(string line, string? gameMap)
    {
        if (line.Length == 0 || Parse(line) is not { } e) return;
        Log($"{e.Kind} {e.Name} {e.SteamId} {e.Text}".TrimEnd() + (e.Kind == Kind.MapStart ? $" [X-Game-Map: {gameMap ?? "none"}]" : ""));
        switch (e.Kind)
        {
            case Kind.Chat: Present(e.SteamId, e.Name); break; // the players' own chat: who is on, never a command (the menu is the interface)
            case Kind.Connected or Kind.Entered: if (e.Kind == Kind.Entered) PlayerEnteredMap(e.SteamId, Now()); OnJoin(e, Arrive(e, Now())); break;
            case Kind.Disconnected: Depart(e.SteamId, e.Text, Now()); PlayerLeftMap(e.SteamId, e.Text, Now()); break; // BrokenWhy.cs: #6
            case Kind.MapStart: PlayerCrashMapStarted(); OnMapStart(e.Text != "" ? e.Text : gameMap ?? ""); break; // CS2's Started: line has no name; OnMapStart fills it in
            case Kind.MatchEnd: OnMatchEnd(); break;
            case Kind.Shutdown: ServerGoingDown("the quit's echo"); break;
        }
    }

    // ---- log-line parser. HTTP push form: MM/DD/YYYY - HH:MM:SS.mmm - <event> (disk form "L ... : " tolerated). See docs/reference.md. ----
    [GeneratedRegex(@"^(?:L )?\d\d/\d\d/\d{4} - \d\d:\d\d:\d\d(?:\.\d+)?(?: - |: )")] private static partial Regex PrefixRx();
    [GeneratedRegex(@"^""(.+?)<(\d+)><(\[U:1:\d+\]|STEAM_[0-5]:[01]:\d+)><([^>]*)>"" (.+)$")] private static partial Regex PlayerRx();
    [GeneratedRegex(@"<\d+><(?:\[U:1:\d+\]|STEAM_[0-5]:[01]:\d+)><[^>]*>""")] private static partial Regex TripleRx();
    [GeneratedRegex(@"^say(_team)? ""(.*)""$")] private static partial Regex SayRx();
    [GeneratedRegex(@"^Loading map ""([^""]+)""|^Started:")] private static partial Regex MapRx();
    [GeneratedRegex(@"^Game Over: (.+)$")] private static partial Regex OverRx(); // once per match, last line of the end-of-match block (see README)
    [GeneratedRegex(@"^rcon from ""[^""]*"": command ""(?:quit|_restart)\b")] private static partial Regex QuitRx();
    [GeneratedRegex(@"^\[U:1:(\d+)\]$")] private static partial Regex SteamId3Rx();
    [GeneratedRegex(@"\(reason ""([^""]*)""\)")] private static partial Regex ReasonRx();

    static LogEvent? Parse(string line)
    {
        var msg = PrefixRx().Replace(line, "", 1).Trim();
        if (msg.StartsWith('"')) // player line, or "Console<0>" say / "cvar" = "value" which carry no SteamID and are ignored
        {
            if (TripleRx().Count(msg) != 1) return null; // zero: no player SteamID; two: a name or chat text imitating one (spoof attempt)
            if (PlayerRx().Match(msg) is not { Success: true } p) return null;
            var (name, steamId, rest) = (p.Groups[1].Value, p.Groups[3].Value, p.Groups[5].Value);
            if (SayRx().Match(rest) is { Success: true } s) return new(Kind.Chat, name, steamId, s.Groups[2].Value.Trim());
            if (rest.StartsWith("connected")) return new(Kind.Connected, name, steamId, "");
            if (rest.StartsWith("entered the game")) return new(Kind.Entered, name, steamId, "");
            if (rest.StartsWith("disconnected")) return new(Kind.Disconnected, name, steamId, ReasonRx().Match(rest) is { Success: true } r ? r.Groups[1].Value : ""); // the reason tells a map change from a real departure
            return null;
        }
        if (MapRx().Match(msg) is { Success: true } m) return new(Kind.MapStart, "", "", m.Groups[1].Value); // "" for Started:
        if (OverRx().Match(msg) is { Success: true } o) return new(Kind.MatchEnd, "", "", o.Groups[1].Value);
        if (QuitRx().IsMatch(msg)) return new(Kind.Shutdown, "", "", msg); // the RCON echo is the only trace of a clean quit
        return null;
    }
}
