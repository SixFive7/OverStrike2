// The STATS page's records (the owner's 304 a, 2026-11-19: fun records, and at least the most voted for and the most played maps), worked out
// on the event loop out of the match history (History.cs), the likes (votes.json), our ballots (Ballots.cs) and every crash counted (state.json's CrashesEver),
// and sent to the plugin as one block with every word final (HudStats: the bridge's /stats/, fetched when the live state's StatsVersion moves). Made again
// whenever one of those changes, and every hour (the maps new this week move with the clock); its version moves only when its words do. Ties go to the most
// recent: the map played last, the player in a match last.
using System.Globalization;

static partial class Program
{
    /// <summary>The block the plugin fetches (Hud.StatsPath), made on the event loop and served as it stands from the HTTP thread.</summary>
    static volatile string StatsText = new HudStats().Encode();

    static HudStats StatsNow = new() { Version = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond }; // its version starts new with every bridge
    static bool StatsStale = true;
    static DateTime StatsMadeAt;

    /// <summary>Something the page counts changed: the block is made again after this piece of work (AfterWork).</summary>
    static void StatsDirty() => StatsStale = true;

    /// <summary>The least share of head shots counts only past this many kills (the Sharpshooter's record).</summary>
    public const int SharpshooterKills = 50;

    /// <summary>From AfterWork: the block made again when something changed, or the hour is up; its version moves when its words did.</summary>
    static void RefreshStats()
    {
        var now = Now();
        if (!StatsStale && now - StatsMadeAt < TimeSpan.FromHours(1)) return;
        StatsStale = false;
        StatsMadeAt = now;
        var made = MakeStats(History.Entries, Likes, Ballots, S.CrashesEver, HistoryNow(), S.StatsSince);
        made.Version = StatsNow.Version;
        if (made.Encode() == StatsNow.Encode()) return;
        made.Version = StatsNow.Version + 1;
        StatsNow = made;
        StatsText = made.Encode();
    }

    /// <summary>The page's records. entries: the history, newest first; likes: votes.json (map key -> Steam id -> value); ballots: oldest first; crashes: every
    /// crash counted per map; now: the history's clock (local, with its offset); since: when the frags began to be counted.</summary>
    static HudStats MakeStats(IReadOnlyList<HudHistory.Entry> entries, Dictionary<string, Dictionary<string, int>> likes, IReadOnlyList<BallotRecord> ballots,
        IReadOnlyDictionary<string, int> crashes, DateTimeOffset now, DateTime? since)
    {
        var st = new HudStats();
        void V(string name, string text) => st.Vars.Add((name, text));
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        static string Count(int n, string one, string many) => $"{N(n)} {(n == 1 ? one : many)}";
        // the map's name: as the map list has it now, else as a match or a ballot had it
        var lastName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lastPlayed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Reverse())
        {
            lastName[e.Key] = e.MapName != "" ? e.MapName : e.LogName;
            lastPlayed[e.Key] = e.End;
        }
        foreach (var b in ballots) foreach (var c in b.Cards) lastName.TryAdd(c.Key, c.Name);
        string MapName(string key) => KnownMap(key)?.Name is { Length: > 0 } listed ? listed : lastName.GetValueOrDefault(key) ?? (key.StartsWith("name:", StringComparison.Ordinal) ? key[5..] : key);
        DateTimeOffset Recent(string key) => lastPlayed.GetValueOrDefault(key, DateTimeOffset.MinValue);

        // ---- the head ----
        var players = entries.SelectMany(e => e.Players).Select(p => p.Account).Distinct().Count();
        V("osst", entries.Count == 0 ? "No match yet - the records start with the first match a human plays"
            : $"{Count(entries.Count, "match", "matches")}  ·  {Count(players, "player", "players")}  ·  since {HudHistory.Day(entries[^1].From)}");
        st.Classes["stnone"] = entries.Count == 0;
        var firstPlayed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) firstPlayed[e.Key] = firstPlayed.TryGetValue(e.Key, out var f) && f < e.From ? f : e.From;
        var fresh = firstPlayed.Where(kv => kv.Value > now.AddDays(-7)).OrderByDescending(kv => kv.Value).Select(kv => MapName(kv.Key)).ToList();
        V("osstnw", fresh.Count == 0 ? "none yet" : $"{Count(fresh.Count, "map", "maps")}: {string.Join(", ", fresh.Take(3))}{(fresh.Count > 3 ? $" and {fresh.Count - 3} more" : "")}");

        // ---- the maps ----
        var plays = entries.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(g => (Key: g.Key, N: g.Count())).OrderByDescending(x => x.N).ThenByDescending(x => Recent(x.Key)).Take(5).ToList();
        for (var k = 1; k <= 5; k++)
        {
            var x = k <= plays.Count ? plays[k - 1] : default;
            V($"osstp{k}n", k <= plays.Count ? MapName(x.Key) : "");
            V($"osstp{k}v", k <= plays.Count ? Count(x.N, "match", "matches") : "");
            if (k >= 2) st.Steps[$"stp{k}"] = k <= plays.Count ? (int)Math.Round(10.0 * x.N / plays[0].N) : 0; // row 1 is always full: no step of its own
        }
        st.Pictures[HudStats.PlayedPicture] = plays.Count > 0 ? plays[0].Key : "";
        var votes = new Dictionary<string, (int N, DateTimeOffset Last)>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in ballots)
            foreach (var c in b.Cards.Where(c => c.Key != RandomKey && c.Votes > 0))
                votes[c.Key] = votes.TryGetValue(c.Key, out var v) ? (v.N + c.Votes, b.At > v.Last ? b.At : v.Last) : (c.Votes, b.At);
        var voted = votes.OrderByDescending(kv => kv.Value.N).ThenByDescending(kv => kv.Value.Last).Take(5).ToList();
        for (var k = 1; k <= 5; k++)
        {
            V($"osstv{k}n", k <= voted.Count ? MapName(voted[k - 1].Key) : "");
            V($"osstv{k}v", k <= voted.Count ? Count(voted[k - 1].Value.N, "vote", "votes") : "");
        }
        var tally = likes.Select(kv => (Key: kv.Key, Up: kv.Value.Values.Count(x => x > 0), Down: kv.Value.Values.Count(x => x < 0),
            Crash: kv.Value.Values.Count(x => x == HudCatalog.CrashVote))).ToList();
        var liked = tally.Where(t => t.Up > 0).OrderByDescending(t => t.Up).ThenBy(t => t.Down).ThenByDescending(t => Recent(t.Key)).Take(5).ToList();
        for (var k = 1; k <= 5; k++)
        {
            V($"osstl{k}n", k <= liked.Count ? MapName(liked[k - 1].Key) : "");
            V($"osstl{k}v", k <= liked.Count ? $"+{N(liked[k - 1].Up)}{(liked[k - 1].Down > 0 ? $"  -{N(liked[k - 1].Down)}" : "")}" : "");
        }
        st.Pictures[HudStats.LikedPicture] = liked.Count > 0 ? liked[0].Key : "";
        var disliked = tally.Where(t => t.Down > 0).OrderByDescending(t => t.Down).ThenBy(t => t.Up).ThenByDescending(t => Recent(t.Key)).Take(3).ToList();
        for (var k = 1; k <= 3; k++)
        {
            V($"osstd{k}n", k <= disliked.Count ? MapName(disliked[k - 1].Key) : "");
            V($"osstd{k}v", k <= disliked.Count ? Count(disliked[k - 1].Down, "dislike", "dislikes") : "");
        }
        var shame = crashes.Select(kv => kv.Key).Concat(tally.Where(t => t.Crash > 0).Select(t => t.Key)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => (Key: key, N: crashes.GetValueOrDefault(key) + tally.Where(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Sum(t => t.Crash)))
            .Where(x => x.N > 0).OrderByDescending(x => x.N).ThenByDescending(x => Recent(x.Key)).Take(3).ToList();
        for (var k = 1; k <= 3; k++)
        {
            V($"osstc{k}n", k <= shame.Count ? MapName(shame[k - 1].Key) : "");
            V($"osstc{k}v", k <= shame.Count ? Count(shame[k - 1].N, "crash", "crashes") : "");
        }

        // ---- the players ----
        var who = new Dictionary<long, (string Name, DateTimeOffset Last)>(); // a player's newest name, and when they were last in a match
        foreach (var e in entries.Reverse()) foreach (var p in e.Players) who[p.Account] = (p.Name.Trim() == "" ? "a player" : p.Name.Trim(), e.End);
        string Name(long account) => who.TryGetValue(account, out var w) ? w.Name : "a player";
        DateTimeOffset Last(long account) => who.TryGetValue(account, out var w) ? w.Last : DateTimeOffset.MinValue;
        (long Account, T Value)? Best<T>(IEnumerable<(long Account, T Value)> rows) where T : IComparable<T> =>
            rows.OrderByDescending(r => r.Value).ThenByDescending(r => Last(r.Account)).Select(r => ((long, T)?)r).FirstOrDefault();
        var matchesOf = entries.SelectMany(e => e.Players.Select(p => p.Account)).GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        var secondsOf = entries.SelectMany(e => e.Players).GroupBy(p => p.Account).ToDictionary(g => g.Key, g => g.Sum(p => (long)p.Seconds));
        var evenings = entries.SelectMany(e => e.Players.Select(p => (p.Account, Day: e.From.Date))).Distinct().GroupBy(x => x.Account).ToDictionary(g => g.Key, g => g.Count());
        var late = entries.Where(e => e.End.Hour < 5).SelectMany(e => e.Players.Select(p => p.Account)).GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        var firstIn = entries.Where(e => e.Players.Count > 0).Select(e => e.Players.OrderBy(p => p.First).ThenBy(p => p.Account).First().Account).GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        var wins = entries.Where(e => e.Winner > 0).GroupBy(e => e.Winner).ToDictionary(g => g.Key, g => g.Count());
        var frags = entries.SelectMany(e => e.Players).Where(p => p.Frags != null).GroupBy(p => p.Account).ToDictionary(g => g.Key, g => g.Select(p => p.Frags!).ToList());
        var killsOf = frags.ToDictionary(kv => kv.Key, kv => kv.Value.Sum(f => f.Kills));
        void Row(int k, (long Account, int Value)? best, Func<int, string> words, bool optional = false)
        {
            var on = best is { Value: > 0 };
            V($"ossr{k}n", on ? Name(best!.Value.Account) : "");
            V($"ossr{k}v", on ? words(best!.Value.Value) : "");
            if (optional) st.Classes[$"str{k}"] = on;
        }
        Row(1, Best(matchesOf.Select(kv => (kv.Key, kv.Value))), n => Count(n, "match", "matches"));
        var hours = Best(secondsOf.Select(kv => (kv.Key, kv.Value)));
        V("ossr2n", hours is { Value: > 0 } h2 ? Name(h2.Account) : "");
        V("ossr2v", hours is { Value: > 0 } h3 ? Hours(h3.Value) : "");
        Row(3, Best(evenings.Select(kv => (kv.Key, kv.Value))), n => Count(n, "evening", "evenings"));
        Row(4, Best(late.Select(kv => (kv.Key, kv.Value))), n => $"{Count(n, "match", "matches")} after midnight");
        Row(5, Best(firstIn.Select(kv => (kv.Key, kv.Value))), n => n == 1 ? "once" : n == 2 ? "twice" : $"{N(n)} times");
        Row(6, Best(wins.Select(kv => (kv.Key, kv.Value))), n => Count(n, "win", "wins"), optional: true);
        Row(7, Best(killsOf.Select(kv => (kv.Key, kv.Value))), n => Count(n, "kill", "kills"), optional: true);

        // ---- the matches ----
        string At(HudHistory.Entry e) => $"{MapName(e.Key)}  ·  {HudHistory.Day(e.From)}";
        var longest = entries.OrderByDescending(e => e.End - e.From).ThenByDescending(e => e.End).FirstOrDefault();
        V("ossm1n", longest == null ? "" : At(longest));
        V("ossm1v", longest == null ? "" : HudHistory.Duration(longest));
        var shortest = entries.Where(e => e.EndedBy == HudHistory.ByMatchEnd && (e.End - e.From).TotalMinutes >= 1).OrderBy(e => e.End - e.From).ThenByDescending(e => e.End).FirstOrDefault();
        V("ossm2n", shortest == null ? "" : At(shortest));
        V("ossm2v", shortest == null ? "" : HudHistory.Duration(shortest));
        var busiest = entries.GroupBy(e => e.From.Date).Select(g => (Day: g.First().From, N: g.Count(), Players: g.SelectMany(e => e.Players.Select(p => p.Account)).Distinct().Count(), Last: g.Max(e => e.End)))
            .OrderByDescending(x => x.N).ThenByDescending(x => x.Last).FirstOrDefault();
        V("ossm3n", busiest.N == 0 ? "" : $"{HudHistory.Day(busiest.Day)}  ·  {Count(busiest.Players, "player", "players")}");
        V("ossm3v", busiest.N == 0 ? "" : Count(busiest.N, "match", "matches"));
        var blowout = entries.Where(e => e.Mode == Modes.WingmanId && e.Score.Split(':') is [var a, var b] && int.TryParse(a, out _) && int.TryParse(b, out _))
            .Select(e => (E: e, A: int.Parse(e.Score.Split(':')[0], CultureInfo.InvariantCulture), B: int.Parse(e.Score.Split(':')[1], CultureInfo.InvariantCulture)))
            .OrderByDescending(x => Math.Abs(x.A - x.B)).ThenByDescending(x => x.E.End).FirstOrDefault();
        V("ossm4n", blowout.E == null ? "" : At(blowout.E));
        V("ossm4v", blowout.E == null ? "" : $"{Math.Max(blowout.A, blowout.B)}:{Math.Min(blowout.A, blowout.B)}");

        // ---- the modes ----
        var moded = entries.Where(e => Modes.Find(e.Mode) is { } && e.Mode != "").ToList();
        var shares = Modes.All.Select(m => (Mode: m, Share: moded.Count == 0 ? 0 : (double)moded.Count(e => e.Mode == m.Id) / moded.Count)).ToList();
        V("osstmd", moded.Count == 0 ? "" : string.Join("  ·  ", shares.Select(s => $"{s.Mode.Name} {Math.Round(100 * s.Share).ToString(CultureInfo.InvariantCulture)} %")));
        foreach (var (mode, share) in shares)
            st.Steps[mode.Id switch { Modes.ArmsRaceId => "stmar", Modes.DeathmatchId => "stmdm", _ => "stmwm" }] = (int)Math.Round(10 * share);

        // ---- the fun records: the frags, counted since `since` ----
        var inMatch = entries.SelectMany(e => e.Players.Where(p => p.Frags is { Kills: > 0 }).Select(p => (e, p))).OrderByDescending(x => x.p.Frags!.Kills).ThenByDescending(x => x.e.End).FirstOrDefault();
        void Fun(int k, string? name, string words)
        {
            var on = name != null;
            V($"ossf{k}n", on ? name! : "");
            V($"ossf{k}v", on ? words : "");
            st.Classes[$"stf{k}"] = on;
        }
        Fun(1, inMatch.p == null ? null : (inMatch.p.Name.Trim() == "" ? "a player" : inMatch.p.Name.Trim()), inMatch.p == null ? "" : Count(inMatch.p.Frags!.Kills, "kill", "kills"));
        var sharp = Best(frags.Where(kv => kv.Value.Sum(f => f.Kills) >= SharpshooterKills).Select(kv => (kv.Key, Value: (int)Math.Round(100.0 * kv.Value.Sum(f => f.Hs) / kv.Value.Sum(f => f.Kills)))));
        Fun(2, sharp is { Value: > 0 } s2 ? Name(s2.Account) : null, sharp is { } s3 ? $"{s3.Value} %" : "");
        var knife = Best(frags.Select(kv => (kv.Key, Value: kv.Value.Sum(f => f.Knife))));
        Fun(3, knife is { Value: > 0 } k2 ? Name(k2.Account) : null, knife is { } k3 ? Count(k3.Value, "knife kill", "knife kills") : "");
        var chickens = Best(frags.Select(kv => (kv.Key, Value: kv.Value.Sum(f => f.Chickens))));
        Fun(4, chickens is { Value: > 0 } c2 ? Name(c2.Account) : null, chickens is { } c3 ? Count(c3.Value, "chicken", "chickens") : "");
        var far = entries.SelectMany(e => e.Players.Where(p => p.Frags is { Far: > 0 }).Select(p => (e, p))).OrderByDescending(x => x.p.Frags!.Far).ThenByDescending(x => x.e.End).FirstOrDefault();
        Fun(5, far.p == null ? null : Name(far.p.Account), far.p == null ? "" : $"{Math.Round(far.p.Frags!.Far).ToString(CultureInfo.InvariantCulture)} m");
        var pairs = entries.Where(e => e.Pairs != null).SelectMany(e => e.Pairs!.Select(p => (p, e.End))).GroupBy(x => (x.p.Killer, x.p.Victim))
            .Select(g => (g.Key.Killer, g.Key.Victim, N: g.Sum(x => x.p.Kills), Last: g.Max(x => x.End))).OrderByDescending(x => x.N).ThenByDescending(x => x.Last).FirstOrDefault();
        Fun(6, pairs.N == 0 ? null : $"{Name(pairs.Killer)} on {Name(pairs.Victim)}", pairs.N == 0 ? "" : Count(pairs.N, "kill", "kills"));
        V("ossfnote", since is { } from ? $"Counting since {HudHistory.Day(new DateTimeOffset(from, TimeSpan.Zero).ToOffset(now.Offset))}" : "");

        // ---- You: every player in the history, and everybody who liked or disliked a map ----
        var ranked = matchesOf.OrderByDescending(kv => kv.Value).ThenByDescending(kv => Last(kv.Key)).Select((kv, i) => (kv.Key, Place: i + 1)).ToDictionary(x => x.Key, x => x.Place);
        var given = new Dictionary<long, (int Up, int Down)>();
        foreach (var votesOn in likes.Values)
            foreach (var (id, value) in votesOn)
                if (AccountId(id) is > 0 and var account)
                    given[account] = given.TryGetValue(account, out var g) ? (g.Up + (value > 0 ? 1 : 0), g.Down + (value < 0 ? 1 : 0)) : (value > 0 ? 1 : 0, value < 0 ? 1 : 0);
        string Given(long account) => given.TryGetValue(account, out var g) ? $"{Count(g.Up, "like", "likes")}  ·  {Count(g.Down, "dislike", "dislikes")}" : "";
        foreach (var account in matchesOf.Keys.Concat(given.Keys).Distinct())
        {
            if (!matchesOf.TryGetValue(account, out var n)) { st.You[account] = ["No match yet", "", "", Given(account), "", ""]; continue; }
            var favourite = entries.Where(e => e.Players.Any(p => p.Account == account)).GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Key: g.Key, N: g.Count(), Last: g.Max(e => e.End))).OrderByDescending(x => x.N).ThenByDescending(x => x.Last).First();
            var first = entries.Where(e => e.Players.Any(p => p.Account == account)).Min(e => e.From);
            st.You[account] = [Count(n, "match", "matches"), Hours(secondsOf.GetValueOrDefault(account)), $"{MapName(favourite.Key)}  ·  {Count(favourite.N, "match", "matches")}",
                Given(account), $"since {HudHistory.Day(first)}", $"#{ranked[account]} of {N(ranked.Count)} by matches"];
        }
        return st;
    }

    /// <summary>A player's time in matches as the page says it: "45 min", "3.5 h", "12 h".</summary>
    static string Hours(long seconds) => seconds < 3600 ? HudHistory.Minutes(seconds / 60.0)
        : seconds < 36000 ? $"{(seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture)} h" : $"{Math.Round(seconds / 3600.0).ToString("N0", CultureInfo.InvariantCulture)} h";
}
