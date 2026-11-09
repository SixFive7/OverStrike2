using CatchUp;

/// <summary>A model of Counter-Strike 2's damage for the self-test's hit counts - test code, never shipped. A hit does the gun's damage
/// times its hit group (the head 4, the chest 1), then the plugin's decision (Rules.Hit with the pair's boost and the victim's second
/// chance), then the game's armour: Arms Race gives armour and a helmet (mp_free_armor 2), so the health takes the damage times the gun's
/// armour penetration and the armour half the rest, until it runs out. Health counts its fractions (how the game rounds a hit's damage to
/// whole points is not modelled), and the second chance compares a hit with the health in whole points, as the plugin sees the pawn's. No
/// distance falloff, every hit to the same place. The guns' damage and penetration are CS2's published numbers, read from no game file here.</summary>
static class Cs2Hits
{
    public static readonly Dictionary<string, (int Damage, double Penetration)> Guns = new()
    {
        ["ak47"] = (36, 0.775), ["m4a4"] = (33, 0.70), ["galilar"] = (30, 0.775), ["ump45"] = (35, 0.65), ["glock"] = (30, 0.47),
    };

    const double HeadTimes = 4;

    /// <summary>One player in a fight: health, its ceiling for catch-up's heals (what the life spawned with), armour, the second chance, Vampire.</summary>
    public sealed class Fighter
    {
        public double Health = 100, Base = 100, Armor = 100;
        public bool SecondChanceLeft;
        public bool Vampire;
        public double VampireCarry;
        public bool Alive => Health > 0;
    }

    /// <summary>One hit of a gun on a victim with the pair's boost: the health the victim lost.</summary>
    public static double Hit(Fighter victim, string gun, bool head, Pair pair)
    {
        var (damage, penetration) = Guns[gun];
        var r = Rules.Hit(new HitInput((float)(damage * (head ? HeadTimes : 1)), pair, (int)Math.Ceiling(victim.Health), (int)victim.Armor, Melee.None, false,
            victim.SecondChanceLeft, false));
        if (r.SecondChanceUsed) victim.SecondChanceLeft = false;
        double d = r.Damage, toHealth = d;
        if (victim.Armor > 0)
        {
            toHealth = d * penetration;
            var toArmor = (d - toHealth) * 0.5;
            if (toArmor > victim.Armor)
            {
                toHealth = d - victim.Armor / 0.5;
                toArmor = victim.Armor;
            }
            victim.Armor -= toArmor;
        }
        victim.Health -= toHealth;
        return toHealth;
    }

    /// <summary>The attacker's heals after a hit that took that much health: catch-up's lifesteal (the pair's share, up to what the life
    /// spawned with) and Vampire (what lifesteal leaves of half the hit, up to Vampire's ceiling), as the plugin gives them.</summary>
    public static void Heal(Fighter attacker, double lost, Pair pair, double victimMax)
    {
        if (!attacker.Alive) return;
        if (pair.Lifesteal > 0 && attacker.Health < attacker.Base)
            attacker.Health = Math.Min(attacker.Base, attacker.Health + Rules.Lifesteal(pair.Lifesteal, (int)Math.Round(lost), (int)victimMax));
        if (attacker.Vampire)
        {
            var health = (int)Math.Ceiling(attacker.Health);
            var healed = global::Vampire.Hit(health, (int)Math.Round(lost), global::Vampire.ShareBeside(pair.Lifesteal), ref attacker.VampireCarry);
            attacker.Health += healed - health;
        }
    }

    /// <summary>Hits to kill a victim who does not shoot back, from their health.</summary>
    public static int ToKill(string gun, bool head, Pair pair, double health = 100, bool secondChance = false)
    {
        var victim = new Fighter { Health = health, Base = Math.Min(health, 100), SecondChanceLeft = secondChance };
        var n = 0;
        while (victim.Alive && n < 1000)
        {
            Hit(victim, gun, head, pair);
            n++;
        }
        return n;
    }

    /// <summary>An even trade: a and b land hits in turn, a first, each healed by their own lifesteal and Vampire. The hits a landed if a won,
    /// or -1 if the trade never ends.</summary>
    public static int Trade(Fighter a, Fighter b, string gun, bool head, Pair aOnB, Pair bOnA)
    {
        for (var n = 1; n <= 400; n++)
        {
            Heal(a, Hit(b, gun, head, aOnB), aOnB, 100);
            if (!b.Alive) return n;
            Heal(b, Hit(a, gun, head, bOnA), bOnA, 100);
            if (!a.Alive) return 0;
        }
        return -1;
    }
}
