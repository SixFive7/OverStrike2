using OverStrike2;
using Verdict = OverStrike2.ArmsRaceLoadout.Verdict;

// GunGameNades.SelfTest - asserts the offline half of the grenade-to-win feature: the environment flag parsing, the
// weapon classifier, and the detector that decides who is standing on the last level of the Arms Race ladder. That
// last one is the whole feature and the whole risk: a false positive takes a level-one player's knife away for the
// rest of the match. It is driven here against fake loadouts and a fake clock, never against a live schema read,
// which is the only way any of it could be checked before the DLL reaches the server.
//
// Run with `dotnet run -c Release`; exit code 0 means every case passed.

var failures = 0;
var checks = 0;

void Check(bool ok, string what)
{
    checks++;
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL  {what}");
}

void Eq<T>(T actual, T expected, string what) =>
    Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

// ---- EnvFlag: anything unrecognised keeps the compiled-in default, so a typo cannot silently disable a feature ----

foreach (var on in new[] { "1", "true", "TRUE", "yes", "on", " On " })
    Eq(ArmsRaceLoadout.EnvFlag(on, false), true, $"EnvFlag(\"{on}\")");
foreach (var off in new[] { "0", "false", "FALSE", "no", "off", " Off " })
    Eq(ArmsRaceLoadout.EnvFlag(off, true), false, $"EnvFlag(\"{off}\")");
foreach (var junk in new string?[] { null, "", "   ", "banana", "2", "-1" })
{
    Eq(ArmsRaceLoadout.EnvFlag(junk, true), true, $"EnvFlag({junk ?? "null"}) keeps default true");
    Eq(ArmsRaceLoadout.EnvFlag(junk, false), false, $"EnvFlag({junk ?? "null"}) keeps default false");
}

// ---- Canonical ----

Eq(ArmsRaceLoadout.Canonical("ak47"), "weapon_ak47", "Canonical bare alias");
Eq(ArmsRaceLoadout.Canonical("weapon_ak47"), "weapon_ak47", "Canonical already prefixed");
Eq(ArmsRaceLoadout.Canonical("WEAPON_AK47"), "weapon_ak47", "Canonical upper case");
Eq(ArmsRaceLoadout.Canonical("  knifegg  "), "weapon_knifegg", "Canonical trims");
Eq(ArmsRaceLoadout.Canonical(null), "", "Canonical null");
Eq(ArmsRaceLoadout.Canonical(""), "", "Canonical empty");
Eq(ArmsRaceLoadout.Canonical("   "), "", "Canonical blank");

// ---- The classifier. Getting a gun wrong is the expensive direction, so unknown names must count as guns ----

foreach (var knife in new[]
         {
             "weapon_knife", "weapon_knife_t", "weapon_knife_ct", "weapon_knifegg", "weapon_knife_karambit",
             "weapon_knife_m9_bayonet", "weapon_knife_push", "weapon_bayonet", "weapon_axe", "weapon_fists",
         })
{
    Check(ArmsRaceLoadout.IsKnife(knife), $"IsKnife {knife}");
    Check(!ArmsRaceLoadout.IsGun(knife), $"IsGun rejects {knife}");
}
foreach (var kit in new[]
         {
             "weapon_hegrenade", "weapon_flashbang", "weapon_smokegrenade", "weapon_molotov", "weapon_incgrenade",
             "weapon_decoy", "weapon_c4", "weapon_taser", "weapon_healthshot",
         })
{
    Check(ArmsRaceLoadout.IsEquipment(kit), $"IsEquipment {kit}");
    Check(!ArmsRaceLoadout.IsGun(kit), $"IsGun rejects {kit}");
}
foreach (var gun in new[]
         {
             "weapon_glock", "weapon_usp_silencer", "weapon_deagle", "weapon_nova", "weapon_mac10", "weapon_p90",
             "weapon_ak47", "weapon_m4a1", "weapon_awp", "weapon_negev", "weapon_revolver",
         })
{
    Check(ArmsRaceLoadout.IsGun(gun), $"IsGun {gun}");
    Check(!ArmsRaceLoadout.IsKnife(gun), $"IsKnife rejects {gun}");
}
// The asymmetry, stated as a test: a weapon nobody listed is treated as a gun, which makes the feature not fire
// rather than disarming somebody by mistake.
Check(ArmsRaceLoadout.IsGun("weapon_something_valve_added_last_tuesday"), "an unknown weapon counts as a gun");
Check(!ArmsRaceLoadout.IsGun(""), "an unreadable name is not a gun");

Check(ArmsRaceLoadout.HasGun(["weapon_ak47", "weapon_knife_t", "weapon_hegrenade"]), "HasGun with a rifle");
Check(!ArmsRaceLoadout.HasGun(["weapon_knife_t", "weapon_hegrenade"]), "HasGun with only knife and grenade");
Check(!ArmsRaceLoadout.HasGun(["weapon_knifegg"]), "HasGun with only the golden knife");
Check(!ArmsRaceLoadout.HasGun([]), "HasGun with empty hands");

// ---- Classify: every branch of the detector, against loadouts rather than a schema ----

const float settled = 30f; // Comfortably past SpawnGrace.
const float confirmed = 30f; // Comfortably past ConfirmDelay.

Eq(ArmsRaceLoadout.Classify(["weapon_glock", "weapon_knife_t"], true, settled, 0f), Verdict.HoldingAGun,
    "level one with a glock is holding a gun");
Eq(ArmsRaceLoadout.Classify(["weapon_awp", "weapon_knife_t", "weapon_hegrenade"], true, settled, 0f), Verdict.HoldingAGun,
    "the top gun is still a gun");
Eq(ArmsRaceLoadout.Classify(["weapon_knife_t", "weapon_hegrenade"], true, settled, confirmed), Verdict.LastLevel,
    "knife and grenade only, seen with a gun before, settled and confirmed");
Eq(ArmsRaceLoadout.Classify(["weapon_knifegg"], true, settled, confirmed), Verdict.LastLevel,
    "the golden knife alone is the last level too");
Eq(ArmsRaceLoadout.Classify([], true, settled, confirmed), Verdict.LastLevel,
    "a disarmed last-level player who threw their grenade stays the last level");

// The three ways the detector refuses, which are the three ways it could have disarmed the wrong player.
Eq(ArmsRaceLoadout.Classify(["weapon_knife_t"], false, settled, confirmed), Verdict.TooSoonToTell,
    "never seen with a gun: leave them alone");
Eq(ArmsRaceLoadout.Classify(["weapon_knife_t"], true, 0.1f, confirmed), Verdict.TooSoonToTell,
    "a tenth of a second after spawning: the engine has not equipped them yet");
Eq(ArmsRaceLoadout.Classify(["weapon_knife_t"], true, ArmsRaceLoadout.SpawnGrace - 0.01f, confirmed), Verdict.TooSoonToTell,
    "just inside the spawn grace");
Eq(ArmsRaceLoadout.Classify(["weapon_knife_t"], true, settled, ArmsRaceLoadout.ConfirmDelay - 0.01f), Verdict.TooSoonToTell,
    "gunless, but not yet for long enough");
Eq(ArmsRaceLoadout.Classify(["weapon_knife_t"], true, ArmsRaceLoadout.SpawnGrace, ArmsRaceLoadout.ConfirmDelay), Verdict.LastLevel,
    "exactly on both thresholds");

// ---- A whole match, swept twice a second against fake loadouts and a fake clock ----
//
// This is the case that would have caught the ladder read throwing on the live server: the detector is exercised
// end to end here with no schema involved at all, so if it needs something that cannot be read, it cannot be
// written this way in the first place.

// One player's timeline, mirroring what the plugin does per sweep. Returns the sweep numbers at which the verdict
// first became LastLevel, plus the verdict history, so both the timing and the absence of false positives are checked.
List<int> Walk(IReadOnlyList<string[]> loadoutPerSweep, IReadOnlyList<int> respawnSweeps, float interval = 0.5f)
{
    var triggered = new List<int>();
    var everHeldAGun = false;
    float? gunlessSince = null;
    var spawnedAt = 0f;
    var known = false;
    for (var sweep = 0; sweep < loadoutPerSweep.Count; sweep++)
    {
        var now = sweep * interval;
        if (respawnSweeps.Contains(sweep)) spawnedAt = now;
        var carried = loadoutPerSweep[sweep];
        if (ArmsRaceLoadout.HasGun(carried)) { everHeldAGun = true; gunlessSince = null; }
        else gunlessSince ??= now;
        var verdict = ArmsRaceLoadout.Classify(carried, everHeldAGun, now - spawnedAt, gunlessSince is null ? 0f : now - gunlessSince.Value);
        if (verdict == Verdict.HoldingAGun) { known = false; continue; }
        if (verdict == Verdict.TooSoonToTell && !known) continue;
        if (!known) { known = true; triggered.Add(sweep); }
    }
    return triggered;
}

// 16 rungs, four sweeps spent on each, the knife-only loadout from rung 16 onwards. Only the last rung may trigger,
// and only once the spawn grace and the confirm delay have both passed.
{
    var loadouts = new List<string[]>();
    string[] guns =
    [
        "weapon_glock", "weapon_usp_silencer", "weapon_p250", "weapon_deagle", "weapon_nova", "weapon_mag7",
        "weapon_mac10", "weapon_mp9", "weapon_ump45", "weapon_p90", "weapon_galilar", "weapon_famas",
        "weapon_ak47", "weapon_m4a1", "weapon_awp",
    ];
    foreach (var gun in guns)
        for (var i = 0; i < 4; i++) loadouts.Add([gun, "weapon_knife_t", "weapon_hegrenade"]);
    for (var i = 0; i < 8; i++) loadouts.Add(["weapon_knife_t", "weapon_hegrenade"]);
    var triggered = Walk(loadouts, respawnSweeps: [0]);
    Eq(triggered.Count, 1, "a full ladder walk triggers exactly once");
    // Rung 16 starts at sweep 60; ConfirmDelay is 1 s at 0.5 s a sweep, so sweep 62 is the first that may fire.
    Eq(triggered.Count == 1 ? triggered[0] : -1, 62, "and only after the confirm delay on the last rung");
}

// A level-one player who respawns over and over. The engine equips them within a sweep or two every time, which is
// exactly the window that must never be read as the last level.
{
    var loadouts = new List<string[]>();
    var respawns = new List<int>();
    for (var life = 0; life < 6; life++)
    {
        respawns.Add(loadouts.Count);
        loadouts.Add(["weapon_knife_t"]);                                  // Spawned, not yet equipped.
        loadouts.Add(["weapon_knife_t"]);                                  // Still not equipped: half a second gone.
        for (var i = 0; i < 6; i++) loadouts.Add(["weapon_glock", "weapon_knife_t"]);
    }
    Eq(Walk(loadouts, respawns).Count, 0, "six lives on level one never look like the last level");
}

// The pathological spawn: the engine takes a full second and a half to hand the gun over, twice as long as observed.
{
    var loadouts = new List<string[]>();
    for (var i = 0; i < 3; i++) loadouts.Add(["weapon_knife_t"]);
    for (var i = 0; i < 10; i++) loadouts.Add(["weapon_ak47", "weapon_knife_t"]);
    Eq(Walk(loadouts, respawnSweeps: [0]).Count, 0, "a slow weapon hand-out is still not the last level");
}

// A last-level player who dies and respawns: the plugin keeps them on its list, so the knife the engine hands back
// on every spawn keeps being taken. Modelled by the `known` flag surviving the spawn grace.
{
    var loadouts = new List<string[]>();
    for (var i = 0; i < 8; i++) loadouts.Add(["weapon_awp", "weapon_knife_t"]);
    for (var i = 0; i < 6; i++) loadouts.Add(["weapon_knife_t", "weapon_hegrenade"]); // Reaches the last level here.
    var respawns = new List<int> { 0, 14 };
    for (var i = 0; i < 6; i++) loadouts.Add(["weapon_knife_t"]);                     // Died and respawned, still last level.
    var triggered = Walk(loadouts, respawns);
    Eq(triggered.Count, 1, "a last-level player who respawns is not re-triggered");
    Eq(triggered.Count == 1 ? triggered[0] : -1, 10, "and was found on the sweep the confirm delay allowed");
}

// Demotion: a last-level player handed a gun again drops off the list, and may trigger again later.
{
    var loadouts = new List<string[]>();
    for (var i = 0; i < 6; i++) loadouts.Add(["weapon_awp", "weapon_knife_t"]);
    for (var i = 0; i < 6; i++) loadouts.Add(["weapon_knife_t", "weapon_hegrenade"]); // Last level.
    for (var i = 0; i < 6; i++) loadouts.Add(["weapon_awp", "weapon_knife_t"]);       // Demoted.
    for (var i = 0; i < 6; i++) loadouts.Add(["weapon_knife_t", "weapon_hegrenade"]); // Last level again.
    Eq(Walk(loadouts, respawnSweeps: [0]).Count, 2, "demotion then promotion triggers twice");
}

Console.WriteLine(failures == 0
    ? $"Over Strike 2 plugin self-test: {checks} checks passed."
    : $"Over Strike 2 plugin self-test: {failures} of {checks} checks FAILED.");
return failures == 0 ? 0 : 1;
