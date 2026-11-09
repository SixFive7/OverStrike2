// The catch-up module's decisions, free of CounterStrikeSharp so the self-test drives every one of them on its own: a hit's boost from the
// gap between the two players in it (Rules.PairFor, Rules.Hit), each player's own extras and the leader's bounty (Rules.Decide), decided
// again after every kill (Rules.Recalculate), the healing, the timing, and what one life took (LifeTally). CatchUpModule.cs only feeds these
// the game's events and applies what they return.
using System.Globalization;
using System.Text;

namespace CatchUp;

/// <summary>What one life gets by its own gap to the leader: the tier's extras that are the player's own - regeneration, the extra spawn
/// protection, infinite ammo - and, for the message and the log, the most its tier gives against players ahead of it (against the leader,
/// who is that far ahead); or the leader's bounty; or nothing. The hits themselves look the gap up between the two players in each one
/// (Rules.PairFor). Decided at spawn, with the spawn's message, and again after every change to the ladder, silently (Rules.Recalculate).</summary>
public sealed record Boost
{
    public static readonly Boost None = new();

    public int KillsBehind { get; init; }

    /// <summary>The leader's lead over second place, on the leader's own decision.</summary>
    public int Lead { get; init; }

    /// <summary>MinBehind of the tier given, 0 for none.</summary>
    public int Tier { get; init; }

    /// <summary>Against a player KillsBehind ahead - the leader - this life's hits do this much more...</summary>
    public double DamageDealt { get; init; }

    /// <summary>...and theirs on it this much less (negative): the tier's health and damage taken together (Rules.Taken).</summary>
    public double DamageTaken { get; init; }

    /// <summary>...it heals this share of what they lose to its hits...</summary>
    public double Lifesteal { get; init; }

    /// <summary>...it survives one killing hit from a player that far ahead, once a life...</summary>
    public bool SecondChance { get; init; }

    /// <summary>...and its knife and Zeus kill them in one hit.</summary>
    public bool OneHitMelee { get; init; }

    public double RegenPerSecond { get; init; }
    public double RegenDelaySeconds { get; init; }
    public double ExtraProtection { get; init; }
    public bool InfiniteAmmo { get; init; }

    /// <summary>The leader's handicap: extra damage taken from enemies.</summary>
    public double Bounty { get; init; }

    /// <summary>The duel factor against the leader (Rules.DuelFactor), the leader's bounty included: for the log.</summary>
    public double Factor { get; init; } = 1;

    public bool IsBoost => Tier > 0 && (DamageTaken < 0 || DamageDealt > 0 || Lifesteal > 0 || RegenPerSecond > 0 || SecondChance || ExtraProtection > 0
                                        || InfiniteAmmo || OneHitMelee);

    public bool IsBounty => Bounty > 0;

    /// <summary>What the log of a mid-life change keys on: the tier, the bounty's step, or nothing.</summary>
    public string Step => IsBounty ? $"bounty +{Math.Round(Bounty * 100).ToString(CultureInfo.InvariantCulture)}%" : Tier > 0 ? $"tier {Tier}+" : "no boost";
}

/// <summary>What one enemy hit gets from the gap between its two players - Gap, the victim's progress minus the attacker's, in kills: the
/// row of the tier table that gap reaches (Row, negative when it is the victim's: the attacker ahead), the multiplier on the hit, the share of
/// the health the victim loses that heals the attacker, the attacker's one-hit knife and Zeus, and the victim's second chance. Within one kill:
/// nothing, plain Counter-Strike - but the leader's bounty, which only a leader 2 or more kills ahead of everybody carries.</summary>
public readonly record struct Pair(int Gap, int Row, double Multiplier, double Lifesteal, bool OneHitMelee, bool SecondChance)
{
    public static readonly Pair Even = new(0, 0, 1, 0, false, false);
}

public enum Melee { None, Knife, Taser }

/// <summary>One enemy hit as the damage hook sees it, before the game applies armour.</summary>
public readonly record struct HitInput(
    float Damage, Pair Pair, int VictimHealth, int VictimArmor, Melee Melee, bool VictimProtected, bool SecondChanceLeft, bool GameZeroesIt);

/// <summary>What to do with it: block it, or let it through with this damage.</summary>
public readonly record struct HitResult(bool Block, float Damage, bool SecondChanceUsed, bool OneHit);

public static class Rules
{
    /// <summary>When the spawn's message comes: right after the boost, or at the first spawn after joining once the join tagline is gone.</summary>
    public const double MessageDelaySeconds = 0.25, FirstSpawnDelaySeconds = 3;

    /// <summary>The tier for a gap, or null below the first.</summary>
    public static TierConfig? TierFor(IReadOnlyList<TierConfig> tiers, int behind)
    {
        TierConfig? best = null;
        foreach (var t in tiers)
            if (behind >= t.MinBehind && (best == null || t.MinBehind > best.MinBehind)) best = t;
        return best;
    }

    /// <summary>The leader's bounty for a lead over second place: PerStep per StepKills of lead, from MinLead, at most Max.</summary>
    public static double BountyFor(BountyConfig b, int lead) =>
        lead < b.MinLead ? 0 : Math.Min(b.Max, b.PerStep * (lead / b.StepKills));

    /// <summary>A tier's protection against the player ahead, as one multiplier on their hits: its extra health and its damage taken
    /// together, (1 + damage taken) / (1 + health) - the health is not a pool of its own, so it can differ from one attacker to the next.
    /// Curve B's 8+: 0.60 / 2.20, 27% of the damage.</summary>
    public static double Taken(TierConfig t) => (1 + t.DamageTaken) / (1 + t.Health);

    /// <summary>One enemy hit's boost from the gap between the two players (gap: the victim's progress minus the attacker's). The attacker
    /// two or more behind gets their row: more damage, lifesteal, the one-hit knife and Zeus. The victim two or more behind gets theirs: the
    /// attacker's hits on them do less, and their second chance. Within one kill both play even. The victim's bounty (the leader's) applies to
    /// every enemy hit on them; nobody who carries one is within a kill of anybody.</summary>
    public static Pair PairFor(CatchUpConfig c, int gap, double victimBounty)
    {
        var bounty = 1 + Math.Max(0, victimBounty);
        if (gap >= 2 && TierFor(c.Tiers, gap) is { } up)
            return new Pair(gap, up.MinBehind, (1 + up.DamageDealt) * bounty, Math.Min(up.Lifesteal, CatchUpConfig.MaxLifesteal), up.OneHitKnifeAndTaser, false);
        if (gap <= -2 && TierFor(c.Tiers, -gap) is { } down)
            return new Pair(gap, -down.MinBehind, Taken(down) * bounty, 0, false, down.SecondChance);
        return new Pair(gap, 0, bounty, 0, false, false);
    }

    /// <summary>How many times faster a player kills the leader than the leader kills them, on paper (same aim, same gun): the tier's
    /// effective health (health, plus lifesteal, which returns that share of the leader's health over a duel) over the damage they take,
    /// times the damage they deal, times the leader's bounty. 1 is an even fight. The same against any player that far ahead.</summary>
    public static double DuelFactor(double health, double lifesteal, double damageDealt, double damageTaken, double bounty) =>
        (1 + health + lifesteal) * (1 + damageDealt) * (1 + bounty) / (1 + damageTaken);

    /// <summary>What a tier's own four values multiply to: the duel factor without the leader's bounty.</summary>
    public static double Own(double health, double lifesteal, double damageDealt, double damageTaken) =>
        DuelFactor(health, lifesteal, damageDealt, damageTaken, 0);

    /// <summary>The own factor of a tier.</summary>
    public static double Own(TierConfig t) => Own(t.Health, t.Lifesteal, t.DamageDealt, t.DamageTaken);

    /// <summary>The boost for this player now, by the standings: the leader may get a bounty, everybody else their tier as the table gives
    /// it. A teammate of the leader (teammateOfLeader: the same team, no free-for-all) never fights the leader, so the duel factor the log
    /// shows for them leaves the leader's bounty out.</summary>
    public static Boost Decide(CatchUpConfig c, Standings s, ulong id, bool teammateOfLeader = false)
    {
        if (s.Players < 2) return Boost.None;
        var bounty = s.Leader == 0 ? 0 : BountyFor(c.Bounty, s.Lead);
        if (id == s.Leader) return new Boost { Lead = s.Lead, Bounty = bounty };
        return ForGap(c, s.Behind(id), teammateOfLeader ? 0 : bounty);
    }

    /// <summary>The tier for a gap to the leader, as the table gives it, against a leader with that bounty.</summary>
    public static Boost ForGap(CatchUpConfig c, int behind, double leaderBounty)
    {
        var t = TierFor(c.Tiers, behind);
        if (t == null) return new Boost { KillsBehind = behind };
        return new Boost
        {
            KillsBehind = behind, Tier = t.MinBehind,
            DamageDealt = t.DamageDealt, DamageTaken = Taken(t) - 1, Lifesteal = Math.Min(t.Lifesteal, CatchUpConfig.MaxLifesteal),
            SecondChance = t.SecondChance, OneHitMelee = t.OneHitKnifeAndTaser,
            RegenPerSecond = t.RegenPerSecond, RegenDelaySeconds = t.RegenDelaySeconds, ExtraProtection = t.ExtraSpawnProtectionSeconds,
            InfiniteAmmo = t.InfiniteAmmo,
            Factor = DuelFactor(t.Health, Math.Min(t.Lifesteal, CatchUpConfig.MaxLifesteal), t.DamageDealt, t.DamageTaken, leaderBounty),
        };
    }

    /// <summary>A life's boost decided again in its middle, after a change to the ladder: the decision now, with the spawn's extra protection
    /// (given at the spawn, ended by the first attack anyway); and whether the tier or the bounty's step changed, the cue for the log's one
    /// line. The hits need nothing of it: each one looks its own gap up.</summary>
    public static (Boost Boost, bool TierChanged) Recalculate(CatchUpConfig c, Standings s, ulong id, bool teammateOfLeader, Boost current)
    {
        var next = Decide(c, s, id, teammateOfLeader) with { ExtraProtection = current.ExtraProtection };
        return (next, current.Step != next.Step);
    }

    /// <summary>Damage that kills anybody: more than their health and armour together, several times over.</summary>
    public static float OneHitDamage(int health, int armor) => Math.Max(1, health + armor) * 4f + 100f;

    /// <summary>One enemy hit, in order: the extra spawn protection blocks it; the pair's multiplier scales it; the one-hit knife or Zeus of
    /// a player far enough behind makes it lethal; the second chance of a victim far enough behind turns a lethal hit into one that leaves 1
    /// health. A hit the game is going to zero anyway (headshots only, a bullet not on the head) is left alone, so it cannot use up a second
    /// chance.</summary>
    public static HitResult Hit(in HitInput h)
    {
        if (h.Damage <= 0 || h.GameZeroesIt) return new HitResult(false, h.Damage, false, false);
        if (h.VictimProtected) return new HitResult(true, 0, false, false);
        var damage = h.Damage * (float)h.Pair.Multiplier;
        var oneHit = false;
        if (h.Pair.OneHitMelee && h.Melee != Melee.None)
        {
            damage = Math.Max(damage, OneHitDamage(h.VictimHealth, h.VictimArmor));
            oneHit = true;
        }
        var used = false;
        if (h.Pair.SecondChance && h.SecondChanceLeft && damage >= h.VictimHealth)
        {
            damage = Math.Max(0, h.VictimHealth - 1); // armour, which the game applies after this, only leaves more
            used = true;
        }
        return new HitResult(false, damage, used, oneHit);
    }

    /// <summary>Which hit this is: a knife, a Zeus, or neither. The weapon the damage names (its ability) first, else the weapon in
    /// the attacker's hands; a blast or a fire is never a knife or a Zeus whatever is in hand.</summary>
    public static Melee MeleeOf(string? ability, string? inHand, bool blastOrFire)
    {
        if (blastOrFire) return Melee.None;
        var kind = Weapons.Kind(string.IsNullOrEmpty(ability) ? inHand : ability);
        return kind switch { WeaponKind.Knife => Melee.Knife, WeaponKind.Taser => Melee.Taser, _ => Melee.None };
    }

    /// <summary>Under headshots only, a bullet that is not on the head does no damage: true when the hit group says chest, stomach, an
    /// arm or a leg (2-7). Anything else - the head, the neck, an unknown group (0, or the invalid value) - is not taken as proof.</summary>
    public static bool GameZeroes(bool headshotsOnly, bool bullet, uint hitGroup) =>
        headshotsOnly && bullet && hitGroup is >= 2 and <= 7;

    /// <summary>Health back for a hit on an enemy: that share of what they lost, never more than their whole health.</summary>
    public static int Lifesteal(double share, int healthLost, int victimMaxHealth) =>
        share <= 0 || healthLost <= 0 ? 0 : (int)Math.Round(share * Math.Min(healthLost, Math.Max(1, victimMaxHealth)));

    /// <summary>Regeneration for one tick of dt seconds. The fractions carry over, so 2 health a second at a half-second tick is 1, 1.</summary>
    public static int Regen(Boost b, double sinceHurt, double dt, int baseHealth, ref double carry)
    {
        if (b.RegenPerSecond <= 0 || sinceHurt < b.RegenDelaySeconds) { carry = 0; return 0; }
        carry += b.RegenPerSecond * Math.Max(1, baseHealth) * dt;
        var whole = (int)Math.Floor(carry);
        carry -= whole;
        return whole;
    }

    /// <summary>Until when the extra spawn protection holds: the game's own protection plus the boost's seconds. None without extra.</summary>
    public static double ProtectedUntil(double spawnedAt, double gameProtection, Boost b) =>
        b.ExtraProtection > 0 ? spawnedAt + Math.Max(0, gameProtection) + b.ExtraProtection : spawnedAt;

    /// <summary>Seconds after the spawn until the message: the first spawn after joining waits FirstSpawnDelaySeconds, any other MessageDelaySeconds.</summary>
    public static double MessageDelay(bool firstSinceJoin) => firstSinceJoin ? FirstSpawnDelaySeconds : MessageDelaySeconds;
}

/// <summary>What one life took and did, for its one line in the log when it ends (Words.Life), so a fight can be checked afterwards: every
/// enemy's hits on it - how many, how many to the head, the health and armour they took, the multiplier catch-up put on them - its own shots
/// and hits on enemies, its heals by kind, and its second chance.</summary>
public sealed class LifeTally
{
    /// <summary>One attacker's hits on this life.</summary>
    public sealed class From
    {
        public string Name = "";
        public int Hits, Head, Health, Armor;
        public double MinMultiplier = double.MaxValue, MaxMultiplier;
    }

    /// <summary>By the attacker's slot, in the order of their first hit.</summary>
    public readonly List<(int Slot, From From)> Attackers = new();

    public int Shots, HitsLanded, Lifesteal, Regen, Vampire;
    public bool SecondChance;

    /// <summary>An enemy's hit on this life: head is the head's hit group, health and armor what the game took, multiplier catch-up's on it.</summary>
    public void Took(int slot, string name, bool head, int health, int armor, double multiplier)
    {
        var at = Attackers.FindIndex(a => a.Slot == slot);
        var f = at >= 0 ? Attackers[at].From : new From { Name = name };
        if (at < 0) Attackers.Add((slot, f));
        f.Hits++;
        if (head) f.Head++;
        f.Health += Math.Max(0, health);
        f.Armor += Math.Max(0, armor);
        f.MinMultiplier = Math.Min(f.MinMultiplier, multiplier);
        f.MaxMultiplier = Math.Max(f.MaxMultiplier, multiplier);
    }

    public int Hits => Attackers.Sum(a => a.From.Hits);
    public int Head => Attackers.Sum(a => a.From.Head);
    public int Health => Attackers.Sum(a => a.From.Health);
    public int Armor => Attackers.Sum(a => a.From.Armor);
    public int Healed => Lifesteal + Regen + Vampire;

    /// <summary>One attacker's part of the line: "Alice 5 hits (1 to the head), 112 health and 24 armour, x0.84" (x0.84-1.00 when it moved).</summary>
    public static string Part(From f)
    {
        var mult = f.Hits == 0 ? "" : Math.Abs(f.MaxMultiplier - f.MinMultiplier) < 0.005
            ? string.Create(CultureInfo.InvariantCulture, $", x{f.MaxMultiplier:0.00}")
            : string.Create(CultureInfo.InvariantCulture, $", x{f.MinMultiplier:0.00}-{f.MaxMultiplier:0.00}");
        var sb = new StringBuilder($"{f.Name} {f.Hits} hit{(f.Hits == 1 ? "" : "s")}");
        if (f.Head > 0) sb.Append($" ({f.Head} to the head)");
        sb.Append($", {f.Health} health and {f.Armor} armour{mult}");
        return sb.ToString();
    }
}
