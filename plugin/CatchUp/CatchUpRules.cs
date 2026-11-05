// The catch-up module's decisions, free of CounterStrikeSharp so the self-test drives every one of them on its own: the tier and
// bounty, the boost decided again mid-life, the damage pipeline and the healing. CatchUpModule.cs only feeds these the game's events and applies what they return.
using System.Globalization;
using System.Text;

namespace CatchUp;

/// <summary>What one life gets: a catch-up boost, the leader's bounty, or nothing. Decided at spawn, with the spawn's message; with
/// RecalculateOnKill decided again after every change to the ladder, silently, by the mid-life rules (Rules.Recalculate).</summary>
public sealed record Boost
{
    public static readonly Boost None = new();

    public int KillsBehind { get; init; }

    /// <summary>The leader's lead over second place, on the leader's own decision.</summary>
    public int Lead { get; init; }

    /// <summary>MinBehind of the tier given, 0 for none.</summary>
    public int Tier { get; init; }

    public double Health { get; init; }
    public double DamageTaken { get; init; }
    public double DamageDealt { get; init; }
    public double Lifesteal { get; init; }
    public double RegenPerSecond { get; init; }
    public double RegenDelaySeconds { get; init; }
    public bool SecondChance { get; init; }
    public double ExtraProtection { get; init; }
    public bool InfiniteAmmo { get; init; }
    public bool OneHitMelee { get; init; }

    /// <summary>The leader's handicap: extra damage taken from enemies.</summary>
    public double Bounty { get; init; }

    /// <summary>The duel factor against the leader (Rules.DuelFactor), the leader's bounty included: for the log.</summary>
    public double Factor { get; init; } = 1;

    /// <summary>Mid-life: the health is what the life already held, more than this decision gives, and damage taken, damage dealt and
    /// lifesteal were cut to make room (Rules.HoldHealth).</summary>
    public bool HealthHeld { get; init; }

    public bool IsBoost => Tier > 0 && (Health > 0 || DamageTaken < 0 || DamageDealt > 0 || Lifesteal > 0 || RegenPerSecond > 0
                                        || SecondChance || ExtraProtection > 0 || InfiniteAmmo || OneHitMelee);

    public bool IsBounty => Bounty > 0;

    /// <summary>What the log of a mid-life change keys on: the tier, the bounty's step, or nothing.</summary>
    public string Step => IsBounty ? $"bounty +{Math.Round(Bounty * 100).ToString(CultureInfo.InvariantCulture)}%" : Tier > 0 ? $"tier {Tier}+" : "no boost";
}

public enum Melee { None, Knife, Taser }

/// <summary>One enemy hit as the damage hook sees it, before the game applies armour.</summary>
public readonly record struct HitInput(
    float Damage, Boost Attacker, Boost Victim, int VictimHealth, int VictimArmor, Melee Melee,
    bool VictimProtected, bool SecondChanceLeft, bool GameZeroesIt);

/// <summary>What to do with it: block it, or let it through with this damage.</summary>
public readonly record struct HitResult(bool Block, float Damage, bool SecondChanceUsed, bool OneHit);

/// <summary>A life's boost decided again in its middle (RecalculateOnKill): the boost from now on; the health it holds, a fraction of the
/// spawn health that never goes down within the life; its maximum health, never lower than it was - the ceiling regeneration and lifesteal
/// fill, raised when the health grows while the current health is left alone; and whether the tier (or the bounty's step) changed, the cue
/// for the log's one line.</summary>
public readonly record struct MidLife(Boost Boost, double Health, int MaxHealth, bool TierChanged);

public static class Rules
{
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

    /// <summary>How many times faster a player kills the leader than the leader kills them, on paper (same aim, same gun): their
    /// effective health (health, plus lifesteal's share of the leader's health over a duel) over the damage they take, times the damage
    /// they deal, times the leader's bounty. 1 is an even fight.</summary>
    public static double DuelFactor(double health, double lifesteal, double damageDealt, double damageTaken, double bounty, bool lifestealCounts) =>
        (1 + health + (lifestealCounts ? lifesteal : 0)) * (1 + damageDealt) * (1 + bounty) / (1 + damageTaken);

    /// <summary>The boost for this player now, by the standings: the leader may get a bounty, everybody else their tier as the table gives
    /// it. A teammate of the leader (teammateOfLeader: the same team, no free-for-all) never fights the leader, so the duel factor the log
    /// shows for them leaves the leader's bounty out.</summary>
    public static Boost Decide(CatchUpConfig c, Standings s, ulong id, bool teammateOfLeader = false)
    {
        if (!c.Enabled || s.Players < 2) return Boost.None;
        var bounty = s.Leader == 0 ? 0 : BountyFor(c.Bounty, s.Lead);
        if (id == s.Leader) return new Boost { Lead = s.Lead, Bounty = bounty };
        return ForGap(c, s.Behind(id), teammateOfLeader ? 0 : bounty);
    }

    /// <summary>The tier for a gap, as the table gives it, against a leader with that bounty.</summary>
    public static Boost ForGap(CatchUpConfig c, int behind, double leaderBounty)
    {
        var t = TierFor(c.Tiers, behind);
        if (t == null) return new Boost { KillsBehind = behind };
        return new Boost
        {
            KillsBehind = behind, Tier = t.MinBehind,
            Health = t.Health, DamageTaken = t.DamageTaken, DamageDealt = t.DamageDealt, Lifesteal = t.Lifesteal,
            RegenPerSecond = t.RegenPerSecond, RegenDelaySeconds = t.RegenDelaySeconds, SecondChance = t.SecondChance,
            ExtraProtection = t.ExtraSpawnProtectionSeconds, InfiniteAmmo = t.InfiniteAmmo, OneHitMelee = t.OneHitKnifeAndTaser,
            Factor = DuelFactor(t.Health, t.Lifesteal, t.DamageDealt, t.DamageTaken, leaderBounty, c.Balance.LifestealCounts),
        };
    }

    /// <summary>What a player's own four values multiply to: the duel factor without the leader's bounty.</summary>
    public static double Own(double health, double lifesteal, double damageDealt, double damageTaken, bool lifestealCounts) =>
        DuelFactor(health, lifesteal, damageDealt, damageTaken, 0, lifestealCounts);

    /// <summary>The own factor of a boost.</summary>
    public static double Own(Boost b, bool lifestealCounts) => Own(b.Health, b.Lifesteal, b.DamageDealt, b.DamageTaken, lifestealCounts);

    /// <summary>A new decision for a life that holds more health than it gives (lifeHealth, a fraction of the spawn health): health is never
    /// lowered within a life, so it stays, and damage taken, damage dealt and lifesteal share what is left of the decision's own factor - scaled
    /// down together (the same power of each one's factor, lifesteal's taken on top of the held health), rounded down to whole RoundTo steps,
    /// then given back a step at a time, in that order, while they fit and never past the decision's own values. When the health alone reaches
    /// that factor all three are zero. Everything else is the decision's: regeneration, the second chance, infinite ammo, the one-hit knife
    /// and Zeus, the bounty. A life that holds no more health than the decision gives gets the decision as it is. leaderBounty: the bounty on
    /// this player's hits on the leader, for the factor the log shows (0 for the leader and his teammates).</summary>
    public static Boost HoldHealth(BalanceConfig b, Boost decided, double lifeHealth, double leaderBounty)
    {
        if (lifeHealth <= decided.Health + 1e-9) return decided;
        var lsc = b.LifestealCounts;
        var target = Own(decided, lsc);
        var floor = 1 + lifeHealth; // the held health's share of the factor
        var limit = new[] { -decided.DamageTaken, decided.DamageDealt, decided.Lifesteal };
        var part = new double[3];   // resistance (as a positive share), damage, lifesteal
        if (!lsc) part[2] = decided.Lifesteal; // outside the factor: as the decision gives it
        double Given() => Own(lifeHealth, part[2], part[1], -part[0], lsc);
        if (target > floor + 1e-9)
        {
            var lifestealFactor = lsc ? (floor + decided.Lifesteal) / floor : 1;
            var all = lifestealFactor * (1 + decided.DamageDealt) / (1 + decided.DamageTaken);
            var k = all > 1 + 1e-12 ? Math.Clamp(Math.Log(target / floor) / Math.Log(all), 0, 1) : 0;
            part[0] = 1 - Math.Pow(1 + decided.DamageTaken, k);
            part[1] = Math.Pow(1 + decided.DamageDealt, k) - 1;
            if (lsc) part[2] = floor * (Math.Pow(lifestealFactor, k) - 1);
            for (var i = 0; i < 3; i++)
                if (lsc || i != 2) part[i] = Math.Min(limit[i], Down(part[i], b.RoundTo));
            for (var grew = b.RoundTo > 0; grew;)
            {
                grew = false;
                for (var i = 0; i < 3; i++)
                {
                    if (!lsc && i == 2) continue;
                    var next = Math.Round(part[i] + b.RoundTo, 6);
                    if (next > limit[i] + 1e-9) continue;
                    var was = part[i];
                    part[i] = next;
                    if (Given() <= target + 1e-9) grew = true;
                    else part[i] = was;
                }
            }
        }
        return decided with
        {
            Health = lifeHealth, DamageTaken = part[0] == 0 ? 0 : -part[0], DamageDealt = part[1], Lifesteal = part[2],
            Factor = Given() * (1 + Math.Max(0, leaderBounty)), HealthHeld = true,
        };
    }

    /// <summary>The boost of a life decided again in its middle by the standings now: Decide, then HoldHealth with the health the life holds.
    /// The extra spawn protection stays the spawn's (spawnProtection): it was given at the spawn and ends at the first attack anyway.</summary>
    public static Boost Redecide(CatchUpConfig c, Standings s, ulong id, bool teammateOfLeader, double lifeHealth, double spawnProtection)
    {
        var decided = Decide(c, s, id, teammateOfLeader);
        var bounty = !c.Enabled || s.Players < 2 || s.Leader == 0 || id == s.Leader || teammateOfLeader ? 0 : BountyFor(c.Bounty, s.Lead);
        return HoldHealth(c.Balance, decided, lifeHealth, bounty) with { ExtraProtection = spawnProtection };
    }

    /// <summary>One life after a change to the ladder (RecalculateOnKill), from its boost now (current), the health it holds (lifeHealth),
    /// its spawn health (baseHealth) and its maximum health: the new boost; health only ever up - a higher maximum, which regeneration and
    /// lifesteal fill, never a cut, and never a heal of the current health; and whether the tier changed. The message and the spawn
    /// protection stay the spawn's: a change in the middle of a life is silent.</summary>
    public static MidLife Recalculate(CatchUpConfig c, Standings s, ulong id, bool teammateOfLeader, Boost current, double lifeHealth, int baseHealth, int maxHealth)
    {
        var next = Redecide(c, s, id, teammateOfLeader, lifeHealth, current.ExtraProtection);
        var grows = next.Health > lifeHealth + 1e-9;
        var max = grows ? Math.Max(maxHealth, SpawnHealth(baseHealth, next)) : maxHealth;
        return new MidLife(next, grows ? next.Health : lifeHealth, max, current.Step != next.Step);
    }

    static double Down(double value, double step)
    {
        if (value <= 0) return 0;
        if (step <= 0) return value;
        return Math.Round(Math.Floor(value / step + 1e-9) * step, 6);
    }

    /// <summary>The damage multiplier for an enemy hit: the attacker's damage, the victim's resistance and the victim's bounty.</summary>
    public static float Multiplier(Boost attacker, Boost victim) =>
        (float)((1 + attacker.DamageDealt) * (1 + victim.DamageTaken) * (1 + victim.Bounty));

    /// <summary>Damage that kills anybody: more than their health and armour together, several times over.</summary>
    public static float OneHitDamage(int health, int armor) => Math.Max(1, health + armor) * 4f + 100f;

    /// <summary>One enemy hit, in order: the extra spawn protection blocks it; the multipliers scale it; a boosted knife or Zeus makes
    /// it lethal; the second chance turns a lethal hit into one that leaves 1 health. A hit the game is going to zero anyway (headshots
    /// only, a bullet not on the head) is left alone, so it cannot use up a second chance.</summary>
    public static HitResult Hit(in HitInput h)
    {
        if (h.Damage <= 0 || h.GameZeroesIt) return new HitResult(false, h.Damage, false, false);
        if (h.VictimProtected) return new HitResult(true, 0, false, false);
        var damage = h.Damage * Multiplier(h.Attacker, h.Victim);
        var oneHit = false;
        if (h.Attacker.OneHitMelee && h.Melee != Melee.None)
        {
            damage = Math.Max(damage, OneHitDamage(h.VictimHealth, h.VictimArmor));
            oneHit = true;
        }
        var used = false;
        if (h.Victim.SecondChance && h.SecondChanceLeft && damage >= h.VictimHealth)
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

    /// <summary>Spawn health with the boost, relative to the spawn health the game (or a mode) gave.</summary>
    public static int SpawnHealth(int baseHealth, Boost b) => Math.Max(1, (int)Math.Round(Math.Max(1, baseHealth) * (1 + b.Health)));

    /// <summary>A life's base and ceiling once something else has cut the pawn's maximum below that ceiling: Vampire switched off mid-life, after which
    /// nobody keeps more than 100. The life follows the pawn, so regeneration, lifesteal and a later boost count from there. A maximum at or above
    /// the ceiling changes nothing: a mode's own higher maximum stays.</summary>
    public static (int Base, int Max) AfterMaximumCut(int baseHealth, int maxHealth, int pawnMax) =>
        pawnMax >= maxHealth ? (baseHealth, maxHealth) : (Math.Min(baseHealth, pawnMax), pawnMax);

    /// <summary>Health back for a hit on an enemy: the lifesteal share of what they lost, never more than their whole health.</summary>
    public static int Lifesteal(Boost attacker, int healthLost, int victimMaxHealth) =>
        attacker.Lifesteal <= 0 || healthLost <= 0 ? 0 : (int)Math.Round(attacker.Lifesteal * Math.Min(healthLost, Math.Max(1, victimMaxHealth)));

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

    /// <summary>Seconds after the spawn until the message: the first spawn after joining waits FirstSpawnDelaySeconds, any other DelaySeconds.</summary>
    public static double MessageDelay(MessageConfig m, bool firstSinceJoin) =>
        firstSinceJoin ? Math.Max(m.DelaySeconds, m.FirstSpawnDelaySeconds) : m.DelaySeconds;
}
