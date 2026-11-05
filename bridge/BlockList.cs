// The game-changers no map may set. The plugin's settings module already puts back, when a map ends, every
// setting the map changed; for the settings on this list it does not wait that long: a map that turns on no-spread, switches the radar
// off, changes the game mode or turns cheats on gets that setting put back right after its own cfgs and entities have run - and again
// whenever it tries later (a round start, a trigger) - and every block is logged. The movement settings bhop and surf maps need to work
// (air acceleration, bunny hopping, stamina, gravity, fall damage) are not on it, and a player's choice on the fun page (Fun.cs) is
// never blocked.
//
// The list is a config file, plugin/OverStrike2/blocked.txt, shipped beside the plugin's DLL: one convar per line, per mode, optionally the
// value it must keep (otherwise the server's baseline), then // and why. Shared like Hud.cs: the bridge's --selftest checks the parser,
// the decision and the committed file; the plugin links it in. Plain BCL only.
public static class BlockList
{
    /// <summary>One line of the file: the convar, the value it must keep (null: the server's baseline) and why it is listed.</summary>
    public sealed record Entry(string ConVar, string? Value, string Why);

    /// <summary>The file's lines as entries. Blank lines and lines that start with // are skipped; a name that is not a plain convar
    /// name, a line with more than a name and a value before its //, or a name listed twice is an error (and the line is skipped).</summary>
    public static List<Entry> Parse(string text, out List<string> errors)
    {
        var list = new List<Entry>();
        errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            var why = comment >= 0 ? line[(comment + 2)..].Trim() : "";
            var body = (comment >= 0 ? line[..comment] : line).Trim();
            if (body == "") continue;
            var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2 || !parts[0].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')) { errors.Add($"line {number}: '{body}' is not a convar and a value"); continue; }
            if (!seen.Add(parts[0])) { errors.Add($"line {number}: {parts[0]} is listed twice"); continue; }
            list.Add(new Entry(parts[0], parts.Length > 1 ? parts[1].Trim('"') : null, why));
        }
        return list;
    }

    /// <summary>What to put back now: every listed convar whose live value is not the one it must keep - its own value from the file,
    /// else the baseline - unless a player chose it on the fun page. A convar the game does not have (value null), or one with no
    /// baseline yet, is left alone.</summary>
    public static List<(Entry Entry, string Now, string Want)> Due(IEnumerable<Entry> list, Func<string, string?> value, Func<string, string?> baseline, Func<string, bool> playerChose)
    {
        var due = new List<(Entry, string, string)>();
        foreach (var e in list)
        {
            if (playerChose(e.ConVar) || value(e.ConVar) is not { } now || (e.Value ?? baseline(e.ConVar)) is not { } want) continue;
            if (!Fun.Same(now, want)) due.Add((e, now, want));
        }
        return due;
    }

    /// <summary>What a blocked setting is called on the top line: players' words, not the convar's.</summary>
    public static string Label(string convar) => convar.ToLowerInvariant() switch
    {
        "game_type" or "game_mode" => "the game mode",
        "sv_cheats" => "cheats",
        "weapon_accuracy_nospread" => "no spread",
        "weapon_air_spread_scale" => "accurate jumping",
        "sv_infinite_ammo" => "the ammo",
        "mp_damage_headshot_only" => "headshots only",
        "mp_damage_scale_ct_body" or "mp_damage_scale_ct_head" or "mp_damage_scale_t_body" or "mp_damage_scale_t_head" => "the damage",
        "mp_teammates_are_enemies" => "free-for-all",
        "mp_free_armor" => "the armour",
        "sv_hegrenade_radius_multiplier" => "the grenades' blast",
        "sv_disable_radar" => "the radar",
        "mp_ignore_round_win_conditions" or "mp_timelimit" or "mp_match_end_restart" or "mp_match_end_changelevel" => "how a match ends",
        "mp_respawn_on_death_ct" or "mp_respawn_on_death_t" => "respawning",
        "mp_warmup_pausetimer" => "an endless warmup",
        "sv_allow_votes" => "the F1/F2 votes",
        "bot_quota" => "bots",
        "mp_weapons_allow_map_placed" or "mp_death_drop_gun" => "guns on the floor",
        "mp_death_drop_grenade" => "the grenade drops",
        "mp_buy_anywhere" or "mp_buytime" => "buying",
        _ => convar,
    };

    /// <summary>The top line: what the map tried to change, and that it was undone. At most 240 characters.</summary>
    public static string Notice(List<string> labels)
    {
        var list = labels.Count switch { 0 => "a setting", 1 => labels[0], _ => string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels[^1] };
        var line = $"This map tried to change {list} - Undone: the server keeps its own rules.";
        return line.Length <= 240 ? line : $"This map tried to change {labels.Count} settings - Undone: the server keeps its own rules.";
    }

    /// <summary>The movement settings bhop and surf maps need: never on the list, checked by --selftest.</summary>
    public static readonly string[] Movement =
    [
        "sv_gravity", "sv_airaccelerate", "sv_accelerate", "sv_friction", "sv_stopspeed", "sv_maxspeed", "sv_maxvelocity", "sv_enablebunnyhopping",
        "sv_autobunnyhopping", "sv_staminajumpcost", "sv_staminalandcost", "sv_staminamax", "sv_staminarecoveryrate", "sv_air_max_wishspeed",
        "sv_accelerate_use_weapon_speed", "sv_legacy_jump", "sv_jump_impulse", "sv_jump_precision_enable", "sv_ladder_scale_speed", "sv_falldamage_scale",
        "sv_wateraccelerate", "sv_waterfriction", "sv_wishspeed", "sv_subtick_movement_view_angles", "sv_standable_normal", "sv_walkable_normal", "sv_bounce",
    ];
}
