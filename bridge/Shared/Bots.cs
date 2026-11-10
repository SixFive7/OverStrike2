// The Bots page: a sub-page of FUN where anybody puts bots in the game, for everybody - how many, on which side, how good, what they may
// fight with, how they talk - and a few things no match allows: bots that never shoot, crouch everywhere, walk or stand frozen, copy a player's
// every move, or leave the players alone and fight each other; and presets that set several of those at once. By default there are none: the
// server's cfgs set bot_quota 0 at every map load, as they always have.
//
// Every row is a fun setting (Fun.Setting, Fun.Choice), kept by the plugin's settings module exactly as the Fun page's are (FunSwitches): a
// change lasts until the map changes, the plugin's unload undoes it and the next instance says so, the bridge's reset after 10 minutes with
// nobody on puts it back, the page says who set it, and a player's value is a choice, never a map's attempt - the game-changers block keeps
// bot_quota and bot_quota_mode where the server has them, and leaves a player's alone (FunState.Chosen). os_fun takes them like the Fun page's
// (os_fun bots 4, os_fun botlevel expert); their ids are none of the Fun page's.
//
// A choice that cannot apply in the mode that is on is greyed, with the reason on its row (Unavailable): the weapon rules in Arms Race, whose
// ladder hands every player, bots too, each rung's weapon - a bot that may not use a pistol would leave the ladder's pistol rung holstered and
// knife its way up instead - and a number of bots the mode has no room for (16 players in every mode). The side, difficulty, the name tag and the
// weapon rules are what a bot gets as it joins, so a change of one while bots play has them join again (Rejoins: bot_kick, then the count again).
//
// Two rows do more than their bot convar, which alone does not cover all they promise. Side lifts the team-size limit and the team balance while
// a side is set (mp_limitteams 0, mp_autoteambalance 0, put back with it): the server's mp_limitteams 1 lets a team hold one player more than the
// other, so with Side T against a lone player on CT only two bots of four could join - the live lab saw it - and the balance would move bots
// across at a round start. Bot war's bot_ignore_players covers the bots' sight only - its help text is "Bots will not see non-bot players" - so a
// bot never aims at a player, while its shots at another bot, its grenades and its fire are not covered: while it is on, the plugin's damage hook
// refuses a bot's damage to a player (Spares), as it does for a player the cloak hides.
//
// Shared like Fun.cs: the bridge's --selftest checks it, the plugin's HUD draws the page (HudBots.cs) and its settings module makes the changes.
// Plain BCL only. Every convar here is one this CS2 build has (plugin/OverStrike2/convars.txt; --selftest holds them to it).
public static class Bots
{
    /// <summary>The value of a choice that names the player who makes it: bot_mimic copies one player, named by their entity index (slot + 1).
    /// The page's click puts the clicker's in (For); the console has no player, and cannot make it.</summary>
    public const string ThePlayer = "@player";

    /// <summary>The page's grid: two columns of six rows, each of up to five choices; its presets; the bots' own number limit for a mode with room
    /// for more (16: Arms Race's and Deathmatch's maxplayers).</summary>
    public const int Rows = 12, ChoicesPerRow = 5, PresetCount = 6;

    static (string, string)[] Set(params (string, string)[] sets) => sets;

    static readonly string[] WeaponKinds =
        ["bot_allow_pistols", "bot_allow_shotguns", "bot_allow_sub_machine_guns", "bot_allow_rifles", "bot_allow_machine_guns", "bot_allow_snipers", "bot_allow_grenades"];

    /// <summary>The weapon rule that lets the bots fight with these kinds alone, every other bot_allow_* off: none of them is knives only.</summary>
    static (string, string)[] Only(params string[] kinds) => [.. WeaponKinds.Select(k => (k, kinds.Contains(k) ? "1" : "0"))];

    /// <summary>Every bot on one side: bot_join_team, and the team-size limit and the team balance off, so the side takes every bot asked for and keeps them.</summary>
    static (string, string)[] OnSide(string team) => Set(("bot_join_team", team), ("mp_limitteams", "0"), ("mp_autoteambalance", "0"));

    static (string, string)[] Count(int n, string mode) => Set(("bot_quota", n.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("bot_quota_mode", mode));

    /// <summary>The page, in order. The first choice of each is Normal: it sets nothing, so it shows the server's own. Bots and Fill to both set bot_quota and
    /// bot_quota_mode - so many bots beside the players, or bots up to so many players in all - so one of them shows a number at a time.</summary>
    public static readonly Fun.Setting[] Settings =
    [
        // the bots: how many, which side, how good, with what, how they talk
        new("bots", "Bots", "How many bots play beside the players. Off: none, as ever",
            [new("off", "Off", []), new("1", "1", Count(1, "normal")), new("2", "2", Count(2, "normal")), new("4", "4", Count(4, "normal")), new("8", "8", Count(8, "normal"))],
            Set(("bot_quota", "0"), ("bot_quota_mode", "fill"))), // Arms Race's own fill; Deathmatch's and Wingman's server cfgs set normal
        new("botfill", "Fill to", "Bots fill the game up to this many players, and make room as players join",
            [new("off", "Off", []), new("4", "4", Count(4, "fill")), new("8", "8", Count(8, "fill")), new("12", "12", Count(12, "fill")), new("16", "16", Count(16, "fill"))],
            Set(("bot_quota", "0"), ("bot_quota_mode", "fill"))),
        new("botteam", "Side", "Which side every bot joins - the teams may be uneven while one is set",
            [new("any", "Any", []), new("t", "T", OnSide("T")), new("ct", "CT", OnSide("CT"))],
            Set(("bot_join_team", "any"), ("mp_limitteams", "1"), ("mp_autoteambalance", "1"))), // the server cfgs' 1 and 1 (Arms Race's and Wingman's)
        new("botlevel", "Difficulty", "How well the bots aim, react and think",
            [new("normal", "Normal", []), new("easy", "Easy", Set(("bot_difficulty", "0"))), new("hard", "Hard", Set(("bot_difficulty", "2"))), new("expert", "Expert", Set(("bot_difficulty", "3")))],
            Set(("bot_difficulty", "1"))),
        new("botweapons", "Weapons", "What the bots may fight with",
            [new("any", "Any", []), new("knives", "Knives", Only()), new("pistols", "Pistols", Only("bot_allow_pistols")),
             new("shotguns", "Shotguns", Only("bot_allow_shotguns")), new("snipers", "Snipers", Only("bot_allow_snipers"))],
            [.. WeaponKinds.Select(k => (k, "1"))]),
        new("botchatter", "Chatter", "What the bots say on the radio: everything, the radio calls alone, or nothing",
            [new("normal", "Normal", []), new("radio", "Radio", Set(("bot_chatter", "radio"))), new("quiet", "Quiet", Set(("bot_chatter", "off")))],
            Set(("bot_chatter", "normal"))),

        // what no match allows
        new("botfire", "Hold fire", "The bots never pull the trigger: target practice that moves",
            [new("off", "Off", []), new("on", "On", Set(("bot_dont_shoot", "1")))],
            Set(("bot_dont_shoot", "0"))),
        new("botcrouch", "Crouch", "The bots crouch wherever they go",
            [new("off", "Off", []), new("on", "On", Set(("bot_crouch", "1")))],
            Set(("bot_crouch", "0"))),
        new("botmove", "Movement", "Walk: the bots only walk, quiet and slow. Frozen: they stay where they stand",
            [new("normal", "Normal", []), new("walk", "Walk", Set(("bot_walk", "1"))), new("frozen", "Frozen", Set(("bot_freeze", "1")))],
            Set(("bot_walk", "0"), ("bot_freeze", "0"))),
        new("botmimic", "Copycats", "The bots copy every move of the player who switched it on",
            [new("off", "Off", []), new("on", "On", Set(("bot_mimic", ThePlayer)))],
            Set(("bot_mimic", "0"))),
        new("botwar", "Bot war", "The bots fight each other and leave the players alone, stray shots included",
            [new("off", "Off", []), new("on", "On", Set(("bot_ignore_players", "1")))],
            Set(("bot_ignore_players", "0"))),
        new("bottag", "Name tag", "A word before every bot's name",
            [new("none", "None", []), new("bot", "Bot", Set(("bot_prefix", "BOT"))), new("zombie", "Zombie", Set(("bot_prefix", "ZOMBIE")))],
            Set(("bot_prefix", ""))),
    ];

    /// <summary>Whether Bot war is on: the bots see no player (bot_ignore_players), as the live values have it.</summary>
    public static bool War(Func<string, string?> value) => Find("botwar") is { } s && Fun.Showing(s, value).Id == "on";

    /// <summary>Why the plugin's damage hook refuses a hit (BotGuard): None, it lands.</summary>
    public enum Spare { None, BotWar, Cloak }

    /// <summary>The plugin's damage hook's one rule (BotGuard): a bot's damage to a player is refused while Bot war is on - bot_ignore_players covers the bots'
    /// sight only, not their shots at each other, their grenades or their fire - or while the cloak shields the player (CloakShield: hidden, the cloak hiding
    /// them from people and not from the bots, and no attack of theirs in that hide). A player's damage lands on everybody, hidden or not, and a bot's on a
    /// bot. botWar and shielded are asked only for a bot's damage to a player, Bot war first.</summary>
    public static Spare Spares(bool attackerIsBot, bool victimIsBot, Func<bool> botWar, Func<bool> shielded) =>
        !attackerIsBot || victimIsBot ? Spare.None : botWar() ? Spare.BotWar : shielded() ? Spare.Cloak : Spare.None;

    /// <summary>The bot settings a bot takes as it joins - the side it joins (bot_join_team), its difficulty, its weapon rules, its name: a change of one while
    /// bots play has them join again (bot_kick, then the count again), or the bots already in would keep the old one.</summary>
    public static bool Rejoins(Fun.Setting s) => s.Id is "botteam" or "botlevel" or "botweapons" or "bottag";

    /// <summary>The two rows that set how many bots play: both write bot_quota and bot_quota_mode.</summary>
    public static bool IsCount(Fun.Setting s) => s.Id is "bots" or "botfill";

    public static Fun.Setting? Find(string id) => Settings.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The number of bots a count choice asks for, or 0 (Off).</summary>
    public static int CountOf(Fun.Choice c) => c.Sets.FirstOrDefault(x => x.ConVar == "bot_quota") is { ConVar: not null } q && int.TryParse(q.Value, out var n) ? n : 0;

    /// <summary>How many bots play now by the two count rows, or 0: bot_quota beside bot_quota_mode normal, or up to it in all with fill. What the console and
    /// the presets' words say, and whether a change of a joining setting has bots to send round again.</summary>
    public static int Quota(Func<string, string?> value) => int.TryParse(value("bot_quota"), out var n) ? Math.Max(0, n) : 0;

    /// <summary>Why this choice cannot be made in this mode, or null when it can - the row's reason while it is greyed. Arms Race's ladder hands every player
    /// each rung's weapon, bots too: a weapon rule would have the bots refuse the ladder's own guns. A mode has room for so many players (Valve's maxplayers),
    /// and a player is on: more bots than that, or a fill past it, would never all join. Normal (the first choice) can always be made.</summary>
    public static string? Unavailable(Fun.Setting s, Fun.Choice c, Modes.Mode mode)
    {
        if (c == s.Normal) return null;
        if (s.Id == "botweapons" && mode.ArmsRace) return "Arms Race's ladder hands out the weapons, to the bots too";
        if (s.Id == "bots" && CountOf(c) > mode.MaxPlayers - 1) return $"{mode.Name} has room for {mode.MaxPlayers} players, one of them yours";
        if (s.Id == "botfill" && CountOf(c) > mode.MaxPlayers) return $"{mode.Name} has room for {mode.MaxPlayers} players";
        return null;
    }

    /// <summary>The choice that names its player, made for this one: bot_mimic gets their entity index (slot + 1). Every other choice as it is.</summary>
    public static Fun.Choice For(Fun.Choice c, int slot) =>
        c.Sets.Any(x => x.Value == ThePlayer) ? c with { Sets = [.. c.Sets.Select(x => x.Value == ThePlayer ? (x.ConVar, (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) : x)] } : c;

    /// <summary>A choice still waiting for its player (ThePlayer): the console's os_fun botmimic on, which no player made.</summary>
    public static bool NeedsPlayer(Fun.Choice c) => c.Sets.Any(x => x.Value == ThePlayer);

    // ---- the presets: several settings at once ----

    /// <summary>A preset: its id (the button's and the console's), its title, one line of what it is, and the choices it makes - every other bot setting goes back
    /// to Normal with it, so a preset is the same game whatever came before.</summary>
    public sealed record Preset(string Id, string Title, string Words, (string Setting, string Choice)[] Picks);

    public static readonly Preset[] Presets =
    [
        new("zombies", "Zombie horde", "Eight expert zombies with knives, and not a word out of them",
            [("bots", "8"), ("botweapons", "knives"), ("botlevel", "expert"), ("bottag", "zombie"), ("botchatter", "quiet")]),
        new("duel", "Sniper duel", "One expert bot with a sniper rifle: one on one, best eyes win",
            [("bots", "1"), ("botweapons", "snipers"), ("botlevel", "expert")]),
        new("targets", "Target practice", "Eight bots that stand where they are and never shoot back",
            [("bots", "8"), ("botfire", "on"), ("botmove", "frozen"), ("botlevel", "easy")]),
        new("copycats", "Copycats", "Four bots that copy your every move, and hold their fire",
            [("bots", "4"), ("botmimic", "on"), ("botfire", "on")]),
        new("war", "Bot war", "Eight bots that fight each other: watch, or join in",
            [("bots", "8"), ("botwar", "on"), ("botlevel", "hard"), ("bottag", "bot")]),
        new("pistols", "Pistol party", "Four bots with pistols and nothing else",
            [("bots", "4"), ("botweapons", "pistols")]),
    ];

    public static Preset? FindPreset(string id) => Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a preset comes to in this mode: every bot setting with the choice it makes - its own picks, Normal for the rest - leaving out what this mode
    /// cannot make (a weapon rule in Arms Race); null when its bots do not fit the mode at all (more than its room for 16 players). Ordered as Fun.InSafeOrder orders
    /// Random's changes, and with the count last, so the bots join with the rest already set and need not join again.</summary>
    public static List<(Fun.Setting Setting, Fun.Choice Choice)>? Plan(Preset p, Modes.Mode mode)
    {
        var picks = new List<(Fun.Setting, Fun.Choice)>();
        foreach (var s in Settings)
        {
            var id = p.Picks.FirstOrDefault(x => x.Setting == s.Id).Choice;
            var c = id == null ? s.Normal : Fun.Find(s, id)!;
            if (Unavailable(s, c, mode) is not null)
            {
                if (IsCount(s)) return null; // its bots do not fit: no preset without them
                c = s.Normal;                // a weapon rule in Arms Race: the rest of the preset stands
            }
            picks.Add((s, c));
        }
        return [.. picks.OrderBy(x => x.Item2 == x.Item1.Normal ? 0 : IsCount(x.Item1) ? 2 : 1)]; // a count's Off first: it puts bot_quota back
    }

    /// <summary>The preset that shows now: the one whose every pick, and Normal for the rest, the live values show (a preset's mimic is anybody's); null for none.</summary>
    public static Preset? Showing(Func<string, string?> value, Modes.Mode mode) =>
        Presets.FirstOrDefault(p => Plan(p, mode) is { } plan && plan.All(x => Fun.Showing(x.Setting, value) == x.Choice));

    /// <summary>Why a preset cannot be made in this mode, or null: its bots do not fit.</summary>
    public static string? Unavailable(Preset p, Modes.Mode mode) =>
        Plan(p, mode) != null ? null : p.Picks.Select(x => Find(x.Setting) is { } s && Fun.Find(s, x.Choice) is { } c ? Unavailable(s, c, mode) : null).FirstOrDefault(r => r != null);

    // ---- the words: the page's top line and its buttons, the messages ----

    /// <summary>The Bots page's two buttons, and their words in chat: Kick all bots (every bot leaves, the two counts back to Off) and Default (every bot setting back to normal).</summary>
    public const string KickWord = "kick", DefaultWord = "default";

    /// <summary>The line at the top of the Bots page.</summary>
    public const string Note = "Bots for everybody, set by anybody, until the map changes. Off unless somebody puts them in.";

    /// <summary>A preset's message (Messages.BotsPreset), its parts marked for chat: "Remy started Zombie horde: bots 8, weapons knives, difficulty expert - Until the map changes";
    /// what this mode left out said after it.</summary>
    public static string PresetLine(string who, Preset p, List<(Fun.Setting Setting, Fun.Choice Choice)> made, Modes.Mode mode)
    {
        var parts = made.Where(x => x.Choice != x.Setting.Normal).Select(x => $"{M.A(x.Setting.Title.ToLowerInvariant())} {M.V(x.Choice.Label.ToLowerInvariant())}");
        var left = p.Picks.Where(x => !made.Any(m => m.Setting.Id == x.Setting && m.Choice.Id == x.Choice)).Select(x => Find(x.Setting)?.Title.ToLowerInvariant()).Where(t => t != null).ToList();
        return $"{M.N(who)} started {M.A(p.Title)}: {string.Join(", ", parts)}{(left.Count > 0 ? $" ({string.Join(", ", left)} not in {mode.Name})" : "")} - {Fun.UntilMapChange}";
    }

    /// <summary>The message after Kick all bots or Default (Messages.BotsOff).</summary>
    public static string OffLine(string who, string which) =>
        which == KickWord ? $"{M.N(who)} kicked every bot" : $"{M.N(who)} put every bot setting back to normal";
}
