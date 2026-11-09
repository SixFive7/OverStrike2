// Vampire, one of the fun page's effects (Fun.cs, Fun.Vampire): its numbers and its heal. Shared like Hud.cs: the bridge's --selftest holds
// the page's words to these numbers, the plugin's effects module heals by them (plugin/OverStrike2/FunEffects.cs), and the catch-up self-test
// links this file to count the hits a fight takes with it. Plain BCL only.
//
// Until plugin 5.8.0 a player spawned with 1,000 health and every point of damage dealt came back, up to 2,000: 34 rifle hits to the body or 9
// to the head killed a fresh player, and two players trading hits evenly could never kill each other, since each hit taken was paid back by the
// hit landed. Now a player spawns with the game's own 100, every hit on an enemy heals a tenth of the health it took, every kill of an enemy
// 40 more, up to 150: a fresh player dies as in plain Counter-Strike, and a player on a run carries up to half a life more, enough to live
// through one headshot that would have killed them.

/// <summary>Vampire's numbers and its heal.</summary>
public static class Vampire
{
    /// <summary>What a player spawns with while Vampire is on: the game's own.</summary>
    public const int SpawnHealth = 100;

    /// <summary>Vampire's ceiling: its heals go no higher.</summary>
    public const int Cap = 150;

    /// <summary>The share of the health an enemy loses to a hit that heals its attacker, in percent.</summary>
    public const int HitPercent = 10;

    /// <summary>Health back for a kill of an enemy.</summary>
    public const int KillHeal = 40;

    /// <summary>The most of a hit's damage that heals its attacker, Vampire and catch-up's lifesteal together, in percent (the catch-up module's
    /// CatchUpConfig.MaxLifesteal is the same half): no fight heals faster than it hurts.</summary>
    public const int HealCapPercent = 50;

    public static double HitShare => HitPercent / 100.0;

    public static double HealCap => HealCapPercent / 100.0;

    /// <summary>Vampire's share of one hit beside the share another heal takes of it (catch-up's lifesteal): what is left of HealCap, at most HitShare.</summary>
    public static double ShareBeside(double other) => Math.Clamp(Math.Min(HitShare, HealCap - Math.Max(0, other)), 0, HitShare);

    /// <summary>One hit's heal: the attacker's health after it, the fraction carried to the attacker's next hit (so ten hits that each take 14
    /// heal 14, not 10). Never above Cap, and never a cut of a health already above it.</summary>
    public static int Hit(int health, int healthLost, double share, ref double carry)
    {
        if (health >= Cap) { carry = 0; return health; }
        if (healthLost <= 0 || share <= 0) return health;
        carry += share * healthLost;
        var whole = (int)Math.Floor(carry + 1e-9);
        carry = Math.Max(0, carry - whole);
        return Math.Min(Cap, health + whole);
    }

    /// <summary>A kill's heal: the killer's health after it.</summary>
    public static int Kill(int health) => health >= Cap ? health : Math.Min(Cap, health + KillHeal);
}
