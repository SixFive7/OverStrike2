namespace OverStrike2;

/// <summary>Every decision about who is standing on the last rung of the Arms Race ladder, made from nothing but the
/// names of the weapons a player is carrying. Deliberately free of CounterStrikeSharp types, so the sibling
/// <c>GunGameNades.SelfTest</c> project compiles this one file in source and drives the whole detector against fake
/// loadouts and a fake clock — no game server, no schema, no live match.
///
/// There used to be a ladder read here: <c>CCSGameModeRules_ArmsRace.m_WeaponSequence</c>, exposed by
/// CounterStrikeSharp as <c>WeaponSequence</c>. The member exists, but its indexer throws
/// <c>NotSupportedException("Networked vectors currently only support CHandle&lt;T&gt;")</c> for a vector of strings,
/// so the ladder is simply not readable on 1.0.374 — and reading it was never necessary. In Arms Race the engine
/// hands a respawning player the weapon for the rung they are on, so the only rung on which somebody has no gun at
/// all is the last one. "No gun" is the whole signal.</summary>
public static class ArmsRaceLoadout
{
    public const string Grenade = "weapon_hegrenade";

    /// <summary>How long after spawning a player is ignored. For the split second before the engine equips their rung
    /// they hold only a knife, which is indistinguishable from the last level.</summary>
    public const float SpawnGrace = 2.0f;

    /// <summary>How long a player must have been continuously gunless before the verdict is trusted, on top of
    /// <see cref="SpawnGrace"/>. Covers a hand-out slower than the grace and any single odd sweep.</summary>
    public const float ConfirmDelay = 1.0f;

    public enum Verdict
    {
        /// <summary>Carrying a gun, so somewhere below the last rung. Nothing to do, and any earlier verdict is void.</summary>
        HoldingAGun,

        /// <summary>Gunless, but not for long enough or never yet seen with a gun. Leave them alone.</summary>
        TooSoonToTell,

        /// <summary>Gunless, was seen with a gun earlier, and has been gunless long enough for that to mean something.</summary>
        LastLevel,
    }

    /// <summary>Melee weapons that do not start with <c>weapon_knife</c>. Every knife skin does, so this is short.</summary>
    static readonly string[] OtherMelee =
        ["weapon_bayonet", "weapon_melee", "weapon_axe", "weapon_hammer", "weapon_spanner", "weapon_fists", "weapon_shield"];

    /// <summary>Everything a player can carry that is not a gun and not melee. Only needs to be right about what
    /// Arms Race actually hands out plus this plugin's own grenade; the rest is there so an odd server setting or a
    /// future mode cannot make a grenade look like a progression weapon.</summary>
    static readonly string[] Equipment =
    [
        "weapon_hegrenade", "weapon_flashbang", "weapon_smokegrenade", "weapon_molotov", "weapon_incgrenade",
        "weapon_decoy", "weapon_tagrenade", "weapon_firebomb", "weapon_diversion", "weapon_frag_grenade",
        "weapon_snowball", "weapon_c4", "weapon_taser", "weapon_healthshot", "weapon_breachcharge",
        "weapon_bumpmine", "weapon_tablet", "weapon_zone_repulsor",
    ];

    /// <summary>Environment-variable truthiness. Anything unrecognised (including unset and empty) keeps the
    /// compiled-in default, so a typo in the compose file can never silently turn a feature off.</summary>
    public static bool EnvFlag(string? raw, bool fallback) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => fallback,
    };

    /// <summary>One spelling for every comparison. <c>CBaseEntity.DesignerName</c> is always <c>weapon_ak47</c>, but
    /// canonicalising anyway means a name that arrives without the prefix, or cased differently, still matches.</summary>
    public static string Canonical(string? name)
    {
        var s = (name ?? "").Trim().ToLowerInvariant();
        return s.Length == 0 ? "" : s.StartsWith("weapon_", StringComparison.Ordinal) ? s : "weapon_" + s;
    }

    /// <summary>Anything in the melee slot, whatever the build calls it: <c>weapon_knife</c>, <c>weapon_knife_t</c>,
    /// <c>weapon_knifegg</c>, every knife skin, and the handful of melee weapons without that prefix.</summary>
    public static bool IsKnife(string canonical) =>
        canonical.StartsWith("weapon_knife", StringComparison.Ordinal) || Array.IndexOf(OtherMelee, canonical) >= 0;

    /// <summary>Grenades, the bomb and the rest of the carryables — never a rung on the ladder.</summary>
    public static bool IsEquipment(string canonical) => Array.IndexOf(Equipment, canonical) >= 0;

    /// <summary>A progression weapon: anything that is neither melee nor equipment. An unrecognised name counts as a
    /// gun <em>on purpose</em>. Mistaking a gun for something else disarms a player on level one for the rest of the
    /// match; mistaking something else for a gun only means the feature does not fire. The asymmetry decides it.</summary>
    public static bool IsGun(string canonical) =>
        canonical.Length > 0 && !IsKnife(canonical) && !IsEquipment(canonical);

    /// <summary>Whether any of these weapons is a progression weapon.</summary>
    public static bool HasGun(IEnumerable<string> carried)
    {
        foreach (var w in carried)
            if (IsGun(w)) return true;
        return false;
    }

    /// <summary>The per-sweep verdict for one player, which is the entire last-level detector.</summary>
    /// <param name="carried">Canonical designer names of everything the player holds.</param>
    /// <param name="everHeldAGun">Whether this player has been seen carrying a gun at any point this match. Requiring
    /// it is the belt-and-braces half: the state being looked for is a transition off the top gun, and somebody never
    /// seen with a gun (a hot reload mid-match) is left alone rather than guessed at.</param>
    /// <param name="sinceSpawn">Seconds since this player last spawned; <see cref="float.MaxValue"/> if unknown.</param>
    /// <param name="gunlessFor">Seconds this player has been continuously without a gun; 0 if they have one.</param>
    public static Verdict Classify(IEnumerable<string> carried, bool everHeldAGun, float sinceSpawn, float gunlessFor)
    {
        if (HasGun(carried)) return Verdict.HoldingAGun;
        if (!everHeldAGun) return Verdict.TooSoonToTell;
        if (sinceSpawn < SpawnGrace) return Verdict.TooSoonToTell;
        if (gunlessFor < ConfirmDelay) return Verdict.TooSoonToTell;
        return Verdict.LastLevel;
    }
}
