// The catch-up module's words, free of CounterStrikeSharp so the self-test holds them to the rules.
using System.Globalization;
using System.Text;

namespace CatchUp;

/// <summary>The words: the private message (packed for the centre panel), one sentence for a log or a chat line, and the log line.</summary>
public static class Words
{
    static string Pct(double fraction) => Math.Round(Math.Abs(fraction) * 100).ToString(CultureInfo.InvariantCulture) + "%";
    static string Sec(double seconds) => Math.Round(seconds, 1).ToString(CultureInfo.InvariantCulture);
    static string Kills(int n) => n == 1 ? "1 kill" : $"{n} kills";

    /// <summary>What the boost gives, in a fixed order; long words or short ones.</summary>
    public static List<string> Items(Boost b, bool shortWords)
    {
        var items = new List<string>();
        if (b.IsBounty)
        {
            items.Add(shortWords ? $"+{Pct(b.Bounty)} dmg taken (bounty)" : $"+{Pct(b.Bounty)} damage taken (a bounty)");
            return items;
        }
        if (!b.IsBoost) return items;
        if (b.Health > 0) items.Add(shortWords ? $"+{Pct(b.Health)} HP" : $"+{Pct(b.Health)} health");
        if (b.DamageTaken < 0) items.Add(shortWords ? $"-{Pct(b.DamageTaken)} dmg taken" : $"-{Pct(b.DamageTaken)} damage taken");
        if (b.DamageDealt > 0) items.Add(shortWords ? $"+{Pct(b.DamageDealt)} dmg" : $"+{Pct(b.DamageDealt)} damage");
        if (b.Lifesteal > 0) items.Add($"{Pct(b.Lifesteal)} lifesteal");
        if (b.RegenPerSecond > 0) items.Add(shortWords ? "regen" : "regeneration");
        if (b.SecondChance) items.Add(shortWords ? "2nd chance" : "a second chance");
        if (b.ExtraProtection > 0) items.Add(shortWords ? $"+{Sec(b.ExtraProtection)}s protection" : $"+{Sec(b.ExtraProtection)} s spawn protection");
        if (b.InfiniteAmmo) items.Add(shortWords ? "no reloads" : "infinite ammo");
        if (b.OneHitMelee) items.Add(shortWords ? "1-hit knife/zeus" : "one-hit knife and Zeus");
        return items;
    }

    /// <summary>Why: the kills behind the leader, or the lead.</summary>
    public static string Why(Boost b, bool shortWords) => b.IsBounty
        ? $"You lead by {Kills(b.Lead)}:"
        : shortWords ? $"{Kills(b.KillsBehind)} behind:" : $"{Kills(b.KillsBehind)} behind the leader:";

    /// <summary>The whole message in one sentence, for example: "You are 5 kills behind the leader: +40% health, ...".</summary>
    public static string Sentence(Boost b)
    {
        var items = Items(b, shortWords: false);
        if (items.Count == 0) return "";
        return b.IsBounty ? $"You lead by {Kills(b.Lead)}: {string.Join(", ", items)}."
            : $"You are {Kills(b.KillsBehind)} behind the leader: {string.Join(", ", items)}.";
    }

    /// <summary>The centre-panel lines: the reason first, then the items, wrapped at commas. Tries long words with the reason on a line
    /// of its own, then short words, then short words with the short reason inline; the first that fits MaxLines x LineWidth wins,
    /// else the last. Empty when there is nothing to say.</summary>
    public static string[] Lines(Boost b, MessageConfig m)
    {
        if (Items(b, false).Count == 0) return [];
        string[]? last = null;
        foreach (var (shortWords, inline) in new[] { (false, false), (true, false), (true, true) })
        {
            var lines = Pack(Why(b, shortWords && inline), Items(b, shortWords), m.LineWidth, inline);
            if (lines.Length <= m.MaxLines && lines.All(l => l.Length <= m.LineWidth)) return lines;
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
            var piece = items[i] + (i < items.Count - 1 ? "," : "");
            if (line.Length > 0 && line.Length + 1 + piece.Length > width)
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
    public static string Decision(string who, Boost b, Standings s, string? leaderName, int baseHealth, int spawnHealth)
    {
        var standing = $"(leader {leaderName ?? "tied"} {s.Top}, them {s.Top - b.KillsBehind}, {s.Players} players)";
        if (b.IsBounty) return $"{who} leads by {Kills(b.Lead)} ({s.Top} to {s.Second}, {s.Players} players): bounty +{Pct(b.Bounty)} damage taken from enemies";
        if (!b.IsBoost) return $"{who} spawns {Kills(b.KillsBehind)} behind {standing}: no boost";
        var parts = Parts(b, $"({baseHealth} -> {spawnHealth})", spawn: true);
        var cap = b.Capped
            ? string.Create(CultureInfo.InvariantCulture, $"capped: duel factor {b.RawFactor:0.00} -> {b.Factor:0.00}, allowed {b.Allowed:0.00}")
            : string.Create(CultureInfo.InvariantCulture, $"duel factor {b.Factor:0.00}, allowed {b.Allowed:0.00}");
        return string.Create(CultureInfo.InvariantCulture, $"{who} spawns {Kills(b.KillsBehind)} behind {standing}: tier {b.Tier}+: {string.Join(", ", parts)}; {cap}");
    }

    /// <summary>The server log's line for a boost whose tier (or bounty step) changed in the middle of a life (RecalculateOnKill), for
    /// example "P2's boost changes mid-life, tier 8+ -> tier 5+: +120% health held (100, at most 220), -5% damage taken, +10% damage, ...;
    /// duel factor 3.06 - 5 kills behind (leader P1 20, them 15, 4 players)". maxHealth is the life's maximum after the change. The
    /// standing comes last, so no line of this kind reads as a spawn's (Decision).</summary>
    public static string Change(string who, Boost before, Boost now, Standings s, string? leaderName, int baseHealth, int maxHealth)
    {
        var where = now.Lead > 0
            ? $"leads by {Kills(now.Lead)} ({s.Top} to {s.Second}, {s.Players} players)"
            : $"{Kills(now.KillsBehind)} behind (leader {leaderName ?? "tied"} {s.Top}, them {s.Top - now.KillsBehind}, {s.Players} players)";
        var parts = Parts(now, $"({baseHealth}, at most {maxHealth})", spawn: false);
        if (now.IsBounty) parts.Add($"bounty +{Pct(now.Bounty)} damage taken from enemies");
        if (parts.Count == 0) parts.Add("nothing");
        var factor = now.Tier > 0 ? string.Create(CultureInfo.InvariantCulture, $"; duel factor {now.Factor:0.00}") : "";
        return $"{who}'s boost changes mid-life, {before.Step} -> {now.Step}: {string.Join(", ", parts)}{factor} - {where}";
    }

    /// <summary>A boost's values for the log. The health with its numbers (health); the spawn protection only at a spawn: it is the spawn's.</summary>
    static List<string> Parts(Boost b, string health, bool spawn)
    {
        var parts = new List<string>();
        if (b.Health > 0) parts.Add($"+{Pct(b.Health)} health {(b.HealthHeld ? "held " : "")}{health}");
        if (b.DamageTaken < 0) parts.Add($"-{Pct(b.DamageTaken)} damage taken");
        if (b.DamageDealt > 0) parts.Add($"+{Pct(b.DamageDealt)} damage");
        if (b.Lifesteal > 0) parts.Add($"{Pct(b.Lifesteal)} lifesteal");
        if (b.RegenPerSecond > 0) parts.Add($"regeneration {Pct(b.RegenPerSecond)}/s after {Sec(b.RegenDelaySeconds)} s");
        if (b.SecondChance) parts.Add("second chance");
        if (spawn && b.ExtraProtection > 0) parts.Add($"+{Sec(b.ExtraProtection)} s spawn protection");
        if (b.InfiniteAmmo) parts.Add("infinite ammo");
        if (b.OneHitMelee) parts.Add("one-hit knife and Zeus");
        return parts;
    }
}
