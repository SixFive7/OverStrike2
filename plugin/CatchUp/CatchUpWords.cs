// The catch-up module's words, free of CounterStrikeSharp so the self-test holds them to the rules.
using System.Globalization;
using System.Text;

namespace CatchUp;

/// <summary>The words: the card's rows, the private message packed for the centre panel, one sentence for a log or a chat line, and the log's
/// lines - a spawn's decision, a change in the middle of a life, and a life's end with what it took.</summary>
public static class Words
{
    /// <summary>The centre panel wraps at about 31 characters and scrolls taller content (measured 2026-10-04): its lines stay inside these.</summary>
    public const int LineWidth = 28, MaxLines = 5;

    /// <summary>The card's rows: as many as the HUD's card has (HudFrame.CatchUpRows), each up to about 50 characters.</summary>
    public const int CardRows = 6, CardWidth = 50;

    /// <summary>What the card and the sentence say the fight's part of a boost is against: the players ahead, the most against the furthest.</summary>
    public const string Against = "Against players ahead of you, up to:";

    static string Pct(double fraction) => Math.Round(Math.Abs(fraction) * 100).ToString(CultureInfo.InvariantCulture) + "%";
    static string Sec(double seconds) => Math.Round(seconds, 1).ToString(CultureInfo.InvariantCulture);
    static string Kills(int n) => n == 1 ? "1 kill" : $"{n} kills";

    /// <summary>The fight's part of a boost: what it gives against players ahead - the most, against those its whole gap ahead.</summary>
    public static List<string> Fight(Boost b, bool shortWords)
    {
        var items = new List<string>();
        if (!b.IsBoost) return items;
        if (b.DamageDealt > 0) items.Add(shortWords ? $"+{Pct(b.DamageDealt)} dmg" : $"+{Pct(b.DamageDealt)} damage");
        if (b.DamageTaken < 0) items.Add(shortWords ? $"-{Pct(b.DamageTaken)} dmg taken" : $"-{Pct(b.DamageTaken)} damage taken");
        if (b.Lifesteal > 0) items.Add($"{Pct(b.Lifesteal)} lifesteal");
        if (b.SecondChance) items.Add(shortWords ? "2nd chance" : "a second chance");
        if (b.OneHitMelee) items.Add(shortWords ? "1-hit knife/zeus" : "one-hit knife and Zeus");
        return items;
    }

    /// <summary>The player's own part: what they get whoever they fight.</summary>
    public static List<string> Own(Boost b, bool shortWords)
    {
        var items = new List<string>();
        if (!b.IsBoost) return items;
        if (b.RegenPerSecond > 0) items.Add(shortWords ? "regen" : "regeneration");
        if (b.ExtraProtection > 0) items.Add(shortWords ? $"+{Sec(b.ExtraProtection)}s protection" : $"+{Sec(b.ExtraProtection)} s spawn protection");
        if (b.InfiniteAmmo) items.Add(shortWords ? "no reloads" : "infinite ammo");
        return items;
    }

    /// <summary>What the boost gives, in a fixed order, the fight's part first: long words or short ones; a bounty its one item.</summary>
    public static List<string> Items(Boost b, bool shortWords)
    {
        if (b.IsBounty) return [shortWords ? $"+{Pct(b.Bounty)} dmg taken (bounty)" : $"+{Pct(b.Bounty)} damage taken (a bounty)"];
        return [.. Fight(b, shortWords), .. Own(b, shortWords)];
    }

    /// <summary>Why: the kills behind the leader, or the lead.</summary>
    public static string Why(Boost b, bool shortWords) => b.IsBounty
        ? $"You lead by {Kills(b.Lead)}:"
        : shortWords ? $"{b.KillsBehind} behind, vs players ahead:" : $"{Kills(b.KillsBehind)} behind the leader:";

    /// <summary>The whole message in one sentence, for example: "You are 5 kills behind the leader: against players ahead of you, up to
    /// +20% damage, -50% damage taken, 10% lifesteal, a second chance; regeneration, +2 s spawn protection."</summary>
    public static string Sentence(Boost b)
    {
        if (b.IsBounty) return $"You lead by {Kills(b.Lead)}: {string.Join(", ", Items(b, false))}.";
        var fight = Fight(b, false);
        var own = Own(b, false);
        if (fight.Count + own.Count == 0) return "";
        var parts = new List<string>();
        if (fight.Count > 0) parts.Add($"against players ahead of you, up to {string.Join(", ", fight)}");
        if (own.Count > 0) parts.Add(string.Join(", ", own));
        return $"You are {Kills(b.KillsBehind)} behind the leader: {string.Join("; ", parts)}.";
    }

    /// <summary>The card's rows under its head (Why, long words): Against on a row of its own, then the fight's items and then the player's own,
    /// each two to a row while they fit CardWidth - at most CardRows. A bounty: its one row.</summary>
    public static string[] Card(Boost b)
    {
        if (b.IsBounty) return [Capital(Items(b, false)[0])];
        var fight = Fight(b, false);
        var own = Own(b, false);
        if (fight.Count + own.Count == 0) return [];
        var rows = new List<string>();
        if (fight.Count > 0)
        {
            rows.Add(Against);
            rows.AddRange(Pair(fight));
        }
        rows.AddRange(Pair(own));
        return [.. rows.Select(Capital)];
    }

    /// <summary>Items two to a row while two fit CardWidth.</summary>
    static IEnumerable<string> Pair(List<string> items)
    {
        for (var i = 0; i < items.Count;)
        {
            if (i + 1 < items.Count && items[i].Length + 2 + items[i + 1].Length <= CardWidth)
            {
                yield return $"{items[i]}, {items[i + 1]}";
                i += 2;
            }
            else yield return items[i++];
        }
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The centre-panel lines: the reason first, then the items, wrapped at commas. Tries long words with the reason on a line
    /// of its own, then short words, then short words with the short reason inline; the first that fits MaxLines x LineWidth wins,
    /// else the last. Empty when there is nothing to say.</summary>
    public static string[] Lines(Boost b)
    {
        if (Items(b, false).Count == 0) return [];
        string[]? last = null;
        foreach (var (shortWords, inline) in new[] { (false, false), (true, false), (true, true) })
        {
            var why = b.IsBounty || !shortWords || !inline ? Why(b, false) : Why(b, true);
            var items = Items(b, shortWords);
            if (!b.IsBounty && !(shortWords && inline) && Fight(b, false).Count > 0) items.Insert(0, shortWords ? "vs players ahead:" : "against players ahead:");
            var lines = Pack(why, items, LineWidth, inline);
            if (lines.Length <= MaxLines && lines.All(l => l.Length <= LineWidth)) return lines;
            last = lines;
        }
        return last!;
    }

    static string[] Pack(string why, List<string> items, int width, bool inline)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        if (inline) line.Append(why);
        else lines.Add(why);
        for (var i = 0; i < items.Count; i++)
        {
            var piece = items[i] + (i < items.Count - 1 && !items[i].EndsWith(':') ? "," : "");
            if (line.Length > 0 && (line.Length + 1 + piece.Length > width || (i > 0 && items[i - 1].EndsWith(':'))))
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(piece);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines.ToArray();
    }

    /// <summary>The panel's HTML: the reason gold and bold, the rest white, only the tags CounterStrikeSharp's own centre menu uses.</summary>
    public static string Html(string[] lines, Boost b)
    {
        if (lines.Length == 0) return "";
        var why = Why(b, false);
        var whyShort = Why(b, true);
        var html = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) html.Append("<br>");
            var line = lines[i];
            var head = i == 0 && line.StartsWith(why, StringComparison.Ordinal) ? why
                : i == 0 && line.StartsWith(whyShort, StringComparison.Ordinal) ? whyShort : "";
            var colour = b.IsBounty ? "#FF6347" : "#FFD700";
            if (head.Length > 0)
            {
                html.Append($"<font color='{colour}'><b>{Escape(head)}</b></font>");
                line = line[head.Length..];
            }
            if (line.Length > 0) html.Append(Escape(line));
        }
        return html.ToString();
    }

    static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The server log's line for one decision. Names come from the game at run time.</summary>
    public static string Decision(string who, Boost b, Standings s, string? leaderName)
    {
        var standing = $"(leader {leaderName ?? "tied"} {s.Top}, them {s.Top - b.KillsBehind}, {s.Players} players)";
        if (b.IsBounty) return $"{who} leads by {Kills(b.Lead)} ({s.Top} to {s.Second}, {s.Players} players): bounty +{Pct(b.Bounty)} damage taken from enemies";
        if (!b.IsBoost) return $"{who} spawns {Kills(b.KillsBehind)} behind {standing}: no boost";
        return string.Create(CultureInfo.InvariantCulture, $"{who} spawns {Kills(b.KillsBehind)} behind {standing}: tier {b.Tier}+: {Parts(b, spawn: true)}; duel factor {b.Factor:0.00}");
    }

    /// <summary>The server log's line for a boost whose tier (or bounty step) changed in the middle of a life, for example "P2's boost changes
    /// mid-life, tier 8+ -> tier 5+: against players ahead up to +20% damage, -50% damage taken, 10% lifesteal, second chance; regeneration
    /// 5%/s after 3 s; duel factor 3.07 - 5 kills behind (leader P1 20, them 15, 4 players)". The standing comes last, so no line of this kind
    /// reads as a spawn's (Decision).</summary>
    public static string Change(string who, Boost before, Boost now, Standings s, string? leaderName)
    {
        var where = now.Lead > 0
            ? $"leads by {Kills(now.Lead)} ({s.Top} to {s.Second}, {s.Players} players)"
            : $"{Kills(now.KillsBehind)} behind (leader {leaderName ?? "tied"} {s.Top}, them {s.Top - now.KillsBehind}, {s.Players} players)";
        var parts = now.IsBounty ? $"bounty +{Pct(now.Bounty)} damage taken from enemies" : now.IsBoost ? Parts(now, spawn: false) : "nothing";
        var factor = now.Tier > 0 ? string.Create(CultureInfo.InvariantCulture, $"; duel factor {now.Factor:0.00}") : "";
        return $"{who}'s boost changes mid-life, {before.Step} -> {now.Step}: {parts}{factor} - {where}";
    }

    /// <summary>A boost's values for the log: the fight's part against the players ahead, then the player's own; the spawn protection only at a
    /// spawn - it is the spawn's.</summary>
    static string Parts(Boost b, bool spawn)
    {
        var fight = new List<string>();
        if (b.DamageDealt > 0) fight.Add($"+{Pct(b.DamageDealt)} damage");
        if (b.DamageTaken < 0) fight.Add($"-{Pct(b.DamageTaken)} damage taken");
        if (b.Lifesteal > 0) fight.Add($"{Pct(b.Lifesteal)} lifesteal");
        if (b.SecondChance) fight.Add("second chance");
        if (b.OneHitMelee) fight.Add("one-hit knife and Zeus");
        var own = new List<string>();
        if (b.RegenPerSecond > 0) own.Add($"regeneration {Pct(b.RegenPerSecond)}/s after {Sec(b.RegenDelaySeconds)} s");
        if (spawn && b.ExtraProtection > 0) own.Add($"+{Sec(b.ExtraProtection)} s spawn protection");
        if (b.InfiniteAmmo) own.Add("infinite ammo");
        var parts = new List<string>();
        if (fight.Count > 0) parts.Add($"against players ahead up to {string.Join(", ", fight)}");
        if (own.Count > 0) parts.Add(string.Join(", ", own));
        return parts.Count == 0 ? "nothing" : string.Join("; ", parts);
    }

    /// <summary>The server log's line for a life's end, for example "Bob (CT, 34.2 s, tier 8+, 12 shots, 5 hits on enemies) took 7 hits (2 to
    /// the head): 143 damage to health and 31 to armour - Alice 5 hits (1 to the head), 112 damage to health and 24 to armour, x0.84; Carol 2
    /// hits (1 to the head), 31 damage to health and 7 to armour, x1.00; healed 22 (lifesteal 10, regeneration 4, Vampire 8); second chance used;
    /// killed by Alice with glock". The damage is the hits' own, as the game reports it - a killing hit's past what the life had left.</summary>
    public static string Life(string who, string team, double seconds, Boost b, LifeTally t, string? killer, string weapon, bool enemyKill)
    {
        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{who} ({team}, {seconds:0.0} s, "));
        sb.Append(b.IsBounty ? $"{b.Step}, " : b.Tier > 0 ? $"tier {b.Tier}+, " : "");
        sb.Append($"{t.Shots} shot{(t.Shots == 1 ? "" : "s")}, {t.HitsLanded} hit{(t.HitsLanded == 1 ? "" : "s")} on enemies) took {t.Hits} hit{(t.Hits == 1 ? "" : "s")}");
        if (t.Head > 0) sb.Append($" ({t.Head} to the head)");
        sb.Append($": {t.Health} damage to health and {t.Armor} to armour");
        if (t.Attackers.Count > 0) sb.Append(" - ").Append(string.Join("; ", t.Attackers.Select(a => LifeTally.Part(a.From))));
        if (t.Healed > 0)
        {
            var kinds = new List<string>();
            if (t.Lifesteal > 0) kinds.Add($"lifesteal {t.Lifesteal}");
            if (t.Regen > 0) kinds.Add($"regeneration {t.Regen}");
            if (t.Vampire > 0) kinds.Add($"Vampire {t.Vampire}");
            sb.Append($"; healed {t.Healed} ({string.Join(", ", kinds)})");
        }
        if (t.SecondChance) sb.Append("; second chance used");
        sb.Append(killer == null ? $"; died without an enemy ({weapon})" : enemyKill ? $"; killed by {killer} with {weapon}" : $"; team-killed by {killer} with {weapon}");
        return sb.ToString();
    }
}
