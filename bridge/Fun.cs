// The fun settings (the owner's feature request of 2026-10-24): a page of the in-game menu where anybody switches a few game settings
// live, for everybody - low gravity, bunny hopping, no fall damage and so on - the way the grenade drops are switched, with the same
// line on the news for everyone. Unlike the grenade drops, every change a player makes lasts only until the map changes, and the
// bridge's reset after 10 minutes with nobody on puts them back too.
//
// Shared like Hud.cs: the bridge's --selftest checks all of it, GunGameWelcome draws the page and asks for a change, and MapSettingsReset
// makes it (gg_fun) - the plugin that owns the server's game settings: their baseline, the put-back at every map end, and the
// game-changers no map may set (BlockList.cs). A player's change is never blocked, and never becomes the baseline. Plain BCL only.
//
// What "Normal" means: no player's change. On most maps that is the server's own value; a map that sets its own (fs_highsky's low
// gravity, a surf map's air acceleration) keeps it - Normal puts back what the setting was before the first player changed it on this map.
//
// Friendly fire's three (the owner's request of 2026-10-29: Friendly fire, Instakill, Grenades x10, all On by default) are the exception:
// the server's own cfg (server/gamemode_armsrace_server.cfg) turns them on at every map load, so their first choice, On, sets those
// same values - a player's On means on, also on one of the few maps that turn friendly fire off themselves. Instakill and Grenades x10 need
// friendly fire on to do anything (Needs), which the page shows.
//
// Most settings are a convar without the cheat flag (cvarlist on CS2 1.41.9.1; README "Fun settings" says what was tried live). Since Over
// Strike 2 (2026-11-01, the owner's #16 and #24) also: headshots only (mp_damage_headshot_only, no cheat flag), Infinite ammo
// (sv_infinite_ammo 1 - a cheat-flagged convar the server sets itself, as Arms Race's own 2 is set), and three effects that are no convar
// at all - Vampire, Ghost and the no-recoil half of Laser guns: the plugin's own switches (Effect names, "os2_..."), which its Effects
// module acts on; the settings module keeps them like convars, so a player's change of one is undone at the map's end and by the reset
// like any other. Left out still: speed (no convar raises a player's run speed - the weapon in hand sets it) and host_timescale.
using System.Globalization;

public static class Fun
{
    /// <summary>One way a setting can be: its button's word and the convars it sets. Normal sets nothing: it is the absence of a player's
    /// change (see the file summary).</summary>
    public sealed record Choice(string Id, string Label, (string ConVar, string Value)[] Sets);

    /// <summary>A setting that does something only while another one shows a given choice (Instakill and Grenades x10 do nothing while
    /// friendly fire is off) - or, with Not, only while it does not (friendly fire's three do nothing while free-for-all is on: everybody
    /// is an enemy then): the page shows Idle instead of its words then.</summary>
    public sealed record Need(string Setting, string Choice, string Idle, bool Not = false);

    /// <summary>A row of the fun page: its title, one line of what it does, and two or three choices - the first is Normal (or Off), which
    /// sets nothing; only a setting the server's cfg turns on has a first choice that sets something, the cfg's own values (friendly fire's
    /// three). Stock: the server's own value of each convar the setting touches - CS2's, or the server cfg's where it sets one - so the page
    /// can say when a map runs its own.</summary>
    public sealed record Setting(string Id, string Title, string Words, Choice[] Choices, (string ConVar, string Value)[] Stock, Need[]? Needs = null)
    {
        public Choice Normal => Choices[0];
    }

    /// <summary>The fun page's effects that are no game convar: the plugin's own switches, "1" on and "0" off (Vampire, Ghost, no recoil).
    /// The settings module keeps them like convars (FunState, the undo at the map's end, the reset); the Effects module acts on them.</summary>
    public const string EffectPrefix = "os2_", Vampire = "os2_vampire", Ghost = "os2_ghost", NoRecoil = "os2_norecoil";

    public static bool IsEffect(string name) => name.StartsWith(EffectPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Friendly fire's three are Arms Race's own (#27 c, docs/reference.md): Deathmatch is free-for-all and Wingman runs
    /// Valve's competitive friendly fire, so the Fun page leaves them out there.</summary>
    public static bool ArmsRaceOnly(Setting s) => s.Id is "ff" or "instakill" or "ffnades";

    /// <summary>Vampire's numbers: every player spawns with this much health while it is
    /// on, and the damage they deal heals them up to the cap.</summary>
    public const int VampireHealth = 1000, VampireCap = 2000;

    /// <summary>Ghost: invisible for this long after a kill.</summary>
    public const double GhostSeconds = 2;

    /// <summary>Vampire's heal: what a hit worth that much damage leaves the attacker with.</summary>
    public static int Heal(int health, int damage) => Math.Clamp(health + Math.Max(0, damage), 0, VampireCap) is var h && h > health ? h : health;

    /// <summary>The server command MapSettingsReset registers: gg_fun &lt;setting&gt; &lt;choice&gt; ["who"] | gg_fun reset ["who"] | gg_fun.
    /// Server only: GunGameWelcome runs it for a click, the bridge for its reset after nobody has been on for 10 minutes.</summary>
    public const string Command = "gg_fun";
    public const string ResetCommand = Command + " reset";

    public const int Rows = 16, ChoicesPerRow = 3; // the page's layout has exactly this many rows of this many buttons (Over Strike 2's content)

    static (string, string)[] Set(params (string, string)[] sets) => sets;

    /// <summary>The bunny-hop convars, on: jumps chain while jump is held, no speed cap on landing, no stamina penalty.</summary>
    static readonly (string, string)[] BhopOn =
        [("sv_enablebunnyhopping", "1"), ("sv_autobunnyhopping", "1"), ("sv_staminajumpcost", "0"), ("sv_staminalandcost", "0"), ("sv_staminamax", "0")];

    static readonly string[] DamageScales = ["mp_damage_scale_ct_body", "mp_damage_scale_ct_head", "mp_damage_scale_t_body", "mp_damage_scale_t_head"];

    /// <summary>Friendly fire's On, as gamemode_armsrace_server.cfg sets it at every map load (--selftest holds the two equal). Each
    /// ff_damage_reduction_* is the share of an enemy's damage a teammate takes, despite its name: 1 is the same as an enemy, 100 kills
    /// with any hit. Valve documents 0-1; the server accepts more (read back 2026-10-29) - that it multiplies was not yet seen in a game.</summary>
    public const string FriendlyFireOn = "1", InstakillShare = "100", GrenadeShare = "10";

    static readonly Need NeedsFriendlyFire = new("ff", "on", "Does nothing while friendly fire is off");
    static readonly Need NotWithFreeForAll = new("ffa", "on", "Does nothing while free-for-all is on: everybody is an enemy then", Not: true);

    /// <summary>The page, in order. Values are CS2's own units: sv_gravity 800 is normal, so 400 halves it and every jump goes twice as
    /// high; sv_jump_impulse 301.993 is a normal jump's upward speed (57 units high), 450 makes it 127.</summary>
    public static readonly Setting[] Settings =
    [
        new("gravity", "Gravity", "Lower gravity: higher jumps, longer falls, grenades fly further",
            [new("normal", "Normal", []), new("low", "Low", Set(("sv_gravity", "400"))), new("moon", "Moon", Set(("sv_gravity", "200")))],
            Set(("sv_gravity", "800"))),
        new("jump", "Super jump", "Jumps go twice as high; falling is as fast as ever",
            [new("normal", "Normal", []), new("high", "High", Set(("sv_jump_impulse", "450")))],
            Set(("sv_jump_impulse", "301.993"))),
        new("bhop", "Bunny hopping", "Hold jump to keep hopping without losing speed; Turbo adds strong air strafing",
            [new("off", "Off", []), new("on", "On", BhopOn), new("turbo", "Turbo", [.. BhopOn, ("sv_airaccelerate", "100")])],
            Set(("sv_enablebunnyhopping", "0"), ("sv_autobunnyhopping", "0"), ("sv_staminajumpcost", "0.08"), ("sv_staminalandcost", "0.05"), ("sv_staminamax", "80"), ("sv_airaccelerate", "12"))),
        new("fall", "Fall damage", "Off: no fall hurts, from any height",
            [new("on", "On", []), new("off", "Off", Set(("sv_falldamage_scale", "0")))],
            Set(("sv_falldamage_scale", "1"))),
        new("ice", "Ice floor", "Every floor as slippery as ice: you slide on after letting go",
            [new("off", "Off", []), new("on", "On", Set(("sv_friction", "1")))],
            Set(("sv_friction", "5.2"))),
        new("aim", "Laser guns", "No spread and no recoil: every shot goes where the crosshair is, also in the air",
            [new("off", "Off", []), new("on", "On", Set(("weapon_accuracy_nospread", "1"), ("weapon_air_spread_scale", "0"), (NoRecoil, "1")))],
            Set(("weapon_accuracy_nospread", "0"), ("weapon_air_spread_scale", "1"), (NoRecoil, "0"))),
        new("ammo", "Infinite ammo", "No reloads: the magazine never runs out",
            [new("off", "Off", []), new("on", "On", Set(("sv_infinite_ammo", "1")))],
            Set(("sv_infinite_ammo", "2"))), // 2 is Arms Race's own: endless reserve ammo, reloads as usual
        new("hs", "Headshots only", "Only a hit to the head does damage - aim high",
            [new("off", "Off", []), new("on", "On", Set(("mp_damage_headshot_only", "1")))],
            Set(("mp_damage_headshot_only", "0"))),
        new("vampire", "Vampire", $"Spawn with {VampireHealth} health; the damage you deal heals you, up to {VampireCap}",
            [new("off", "Off", []), new("on", "On", Set((Vampire, "1")))],
            Set((Vampire, "0"))),
        new("ghost", "Ghost", $"Invisible for {GhostSeconds:0} seconds after every kill",
            [new("off", "Off", []), new("on", "On", Set((Ghost, "1")))],
            Set((Ghost, "0"))),
        new("nades", "Big grenades", "A wider blast for the grenades every player gets",
            [new("normal", "Normal", []), new("big", "Big", Set(("sv_hegrenade_radius_multiplier", "2"))), new("huge", "Huge", Set(("sv_hegrenade_radius_multiplier", "3")))],
            Set(("sv_hegrenade_radius_multiplier", "1"))),
        new("damage", "Double damage", "Every hit does twice the damage: kills, and the ladder, go faster",
            [new("off", "Off", []), new("on", "On", [.. DamageScales.Select(c => (c, "2"))])],
            [.. DamageScales.Select(c => (c, "1"))]),
        new("ffa", "Free-for-all", "Teammates are enemies too: every player is a target and a kill",
            [new("off", "Off", []), new("on", "On", Set(("mp_teammates_are_enemies", "1")))],
            Set(("mp_teammates_are_enemies", "0"))),
        new("ff", "Friendly fire", "Your shots, knife and grenades hurt teammates too. Off: your team is safe from you",
            [new("on", "On", Set(("mp_friendlyfire", FriendlyFireOn))), new("off", "Off", Set(("mp_friendlyfire", "0")))],
            Set(("mp_friendlyfire", FriendlyFireOn)), [NotWithFreeForAll]),
        new("instakill", "Instakill", "Any bullet or knife hit on a teammate kills them. Off: the damage an enemy takes",
            [new("on", "On", Set(("ff_damage_reduction_bullets", InstakillShare), ("ff_damage_reduction_other", InstakillShare))),
             new("off", "Off", Set(("ff_damage_reduction_bullets", "1"), ("ff_damage_reduction_other", "1")))],
            Set(("ff_damage_reduction_bullets", InstakillShare), ("ff_damage_reduction_other", InstakillShare)), [NotWithFreeForAll, NeedsFriendlyFire]),
        new("ffnades", $"Grenades x{GrenadeShare}", $"A grenade does {GrenadeShare} times the damage to teammates. Off: the damage an enemy takes",
            [new("on", "On", Set(("ff_damage_reduction_grenade", GrenadeShare))), new("off", "Off", Set(("ff_damage_reduction_grenade", "1")))],
            Set(("ff_damage_reduction_grenade", GrenadeShare)), [NotWithFreeForAll, NeedsFriendlyFire]),
    ];

    /// <summary>Every convar the fun page can touch.</summary>
    public static IEnumerable<string> AllConVars => Settings.SelectMany(ConVarsOf).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The convars a setting touches: every one any of its choices sets.</summary>
    public static IEnumerable<string> ConVarsOf(Setting s) => s.Choices.SelectMany(c => c.Sets).Select(x => x.ConVar).Distinct(StringComparer.OrdinalIgnoreCase);

    public static Setting? Find(string id) => Settings.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static Choice? Find(Setting s, string id) => s.Choices.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Two convar values the same: numbers within a hair (the game prints 301.993 for 301.993377), true/false as 1/0.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a == null || b == null) return a == b;
        static string? Bool(string s) => s.Trim().ToLowerInvariant() switch { "true" => "1", "false" => "0", var x => x };
        var (x, y) = (Bool(a)!, Bool(b)!);
        if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var q))
            return Math.Abs(p - q) <= 1e-3 * Math.Max(1, Math.Max(Math.Abs(p), Math.Abs(q)));
        return string.Equals(x.Trim(), y.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The choice the live values show: of the choices that set something, the most specific one whose every convar has its
    /// value (Turbo over On when both match); otherwise Normal. A map that sets a value of its own that no choice has reads as Normal -
    /// it is how the map is, and no player changed it.</summary>
    public static Choice Showing(Setting s, Func<string, string?> value) =>
        s.Choices.Where(c => c.Sets.Length > 0 && c.Sets.All(x => Same(value(x.ConVar), x.Value))).OrderByDescending(c => c.Sets.Length).FirstOrDefault() ?? s.Normal;

    /// <summary>The convars of a setting whose live value is not the server's own: what the page calls "this map's own" while Normal shows.</summary>
    public static List<(string ConVar, string Now)> OwnValues(Setting s, Func<string, string?> value) =>
        s.Stock.Where(x => value(x.ConVar) is { } now && !Same(now, x.Value)).Select(x => (x.ConVar, value(x.ConVar)!)).ToList();

    /// <summary>The setting does nothing at the moment: a need of it is unmet (Instakill and Grenades x10 while friendly fire is off; friendly
    /// fire's three while free-for-all is on). It can still be switched; it takes effect once the other is back. Null when it is not idle.</summary>
    public static Need? IdleBecause(Setting s, Func<string, string?> value) =>
        s.Needs?.FirstOrDefault(n => Find(n.Setting) is { } other && (Showing(other, value).Id == n.Choice) == n.Not);

    public static bool Idle(Setting s, Func<string, string?> value) => IdleBecause(s, value) != null;

    /// <summary>What the news line says for a change: "Remy switched gravity to Low - until the map changes", or "Remy put gravity back to
    /// normal" for the first choice.</summary>
    public static string News(string who, Setting s, Choice c) =>
        c == s.Normal ? $"{who} put {s.Title.ToLowerInvariant()} back to {c.Label.ToLowerInvariant()}" : $"{who} switched {s.Title.ToLowerInvariant()} to {c.Label} - until the map changes";
}

/// <summary>The players' changes on the map that is on, kept by MapSettingsReset: for every convar a player changed, what it was before the
/// first such change on this map. That is what Normal (and the reset of all of them) puts back, so a map's own value survives a player's
/// detour; and while a convar holds a player's value it is exempt from the game-changers block (BlockList.cs) and kept out of the
/// baseline. Free of CounterStrikeSharp: the plugin hands it the live values and makes the writes it returns.</summary>
public sealed class FunState
{
    readonly Dictionary<string, string> before = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>convar -> its value before the first player change on this map.</summary>
    public IReadOnlyDictionary<string, string> Before => before;

    /// <summary>A player's value is in this convar now (it is exempt from the block, and is put back at the map's end).</summary>
    public bool Chosen(string convar) => before.ContainsKey(convar);

    /// <summary>A player picked this choice: the writes it takes - each convar the choice sets gets its value (and its value from before
    /// is remembered, the first time on this map), every other convar of the setting that a player had changed gets its value from before
    /// back and is no longer a player's. Values that are right already are not written.</summary>
    public List<(string ConVar, string Value)> Apply(Fun.Setting setting, Fun.Choice choice, Func<string, string?> value)
    {
        var writes = new List<(string, string)>();
        foreach (var convar in Fun.ConVarsOf(setting))
        {
            var now = value(convar);
            var set = choice.Sets.Where(x => x.ConVar.Equals(convar, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).FirstOrDefault();
            if (set != null)
            {
                if (!before.ContainsKey(convar) && now != null) before[convar] = now;
                if (!Fun.Same(now, set)) writes.Add((convar, set));
            }
            else if (before.Remove(convar, out var was) && !Fun.Same(now, was)) writes.Add((convar, was));
        }
        return writes;
    }

    /// <summary>Every player change undone: each changed convar back to its value from before (the bridge's reset after 10 minutes with
    /// nobody on, and the map's end, before anything else is put back).</summary>
    public List<(string ConVar, string Value)> Reset(Func<string, string?> value)
    {
        var writes = before.Where(kv => !Fun.Same(value(kv.Key), kv.Value)).Select(kv => (kv.Key, kv.Value)).ToList();
        before.Clear();
        return writes;
    }

    /// <summary>The settings a player changed, as "gravity low, bhop on" - for the console and the log.</summary>
    public string Describe(Func<string, string?> value)
    {
        var changed = Fun.Settings.Where(s => Fun.ConVarsOf(s).Any(Chosen)).Select(s => $"{s.Id} {Fun.Showing(s, value).Id}").ToList();
        return changed.Count == 0 ? "no player changes" : string.Join(", ", changed);
    }
}
