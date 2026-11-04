using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OverStrike2;
using Verdict = OverStrike2.ArmsRaceLoadout.Verdict;

// GunGameNades.SelfTest - asserts the offline half of the grenade-to-win feature: the environment flag parsing, the
// weapon classifier, and the detector that decides who is standing on the last level of the Arms Race ladder. That
// last one is the whole feature and the whole risk: a false positive takes a level-one player's knife away for the
// rest of the match. It is driven here against fake loadouts and a fake clock, never against a live schema read,
// which is the only way any of it could be checked before the DLL reaches the server.
//
// And the settings module's question to the running build (ConVarNames.cs), with the engine faked: what it may add to
// convars.txt, and that anything wrong leaves convars.txt alone.
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

// ---- ConVarNames: the game convars the running build has, asked of the build itself. What can go wrong on a server is a missing or strange
// file and a strange answer, and each of those must leave exactly the embedded list. ----

static byte[] Image(params string[] strings) => Encoding.Latin1.GetBytes("\0" + string.Join('\0', strings) + "\0");
static HashSet<string> Scan(byte[] image) => ConVarNames.Strings(new MemoryStream(image));
string Names(IEnumerable<string> names) => string.Join(",", names);

// The strings: between two NULs, name characters only, of a fitting length.
{
    var found = Scan(Image("sv_gravity", "mp_roundtime", "CS_WarnFriendlyDamageInterval", "_ok.name-1", "abc",
        "not a name", "x", "1bad", "\x00ffsv_bad", "sv_bad2\x00ff", "sv_bad3 ", "", "sv\nbad"));
    Check(found.SetEquals(["sv_gravity", "mp_roundtime", "CS_WarnFriendlyDamageInterval", "_ok.name-1", "abc"]), $"the name-like strings, got {Names(found)}");
    Eq(Scan(Encoding.Latin1.GetBytes("sv_first\0")).Count, 1, "a string at the very start of the file counts");
    Eq(Scan(Encoding.Latin1.GetBytes("\0sv_open_ended")).Count, 0, "a string the file ends in the middle of does not");
    Eq(Scan([]).Count, 0, "an empty file holds no strings");
    Eq(Scan(new byte[1 << 16]).Count, 0, "nor does a file of NULs");
    Eq(Scan(Image("SV_Foo", "sv_foo")).Count, 1, "names without case, as the engine finds them");
    Eq(Scan(Image(new string('a', ConVarNames.MaxName))).Count, 1, "the longest name");
    Eq(Scan(Image(new string('a', ConVarNames.MaxName + 1))).Count, 0, "one more and the whole string is out: never a cut name");
    Eq(Scan(Image(new string('a', ConVarNames.MinName - 1))).Count, 0, "too short");

    // Read in pieces: a name on the seam, and a stream that hands over one byte at a time.
    var seam = new byte[(1 << 20) + 64];
    "sv_across_the_seam"u8.CopyTo(seam.AsSpan((1 << 20) - 5));
    Check(Scan(seam).SetEquals(["sv_across_the_seam"]), "a name across two pieces of the file");
    var image = Image("sv_gravity", "mp_roundtime", "not a name", "sv_bad2\x00ff");
    Check(ConVarNames.Strings(new Trickle(image)).SetEquals(Scan(image)), "the same strings one byte at a time");

    // Noise: nothing throws, and all that comes out still follows the rule.
    var noise = new byte[1 << 21];
    new Random(27).NextBytes(noise);
    var fromNoise = Scan(noise);
    Check(fromNoise.All(s => s.Length is >= ConVarNames.MinName and <= ConVarNames.MaxName && Regex.IsMatch(s, "^[A-Za-z_][A-Za-z0-9_.-]*$")), "random bytes give only name-like strings");
}

// The list.
{
    Check(ConVarNames.ParseList(new StringReader("a_b\r\n\r\n  c_d  \ne_f")).SequenceEqual(["a_b", "c_d", "e_f"]), "ParseList: one name per line, blanks dropped");
    Eq(ConVarNames.ParseList(new StringReader("")).Count, 0, "ParseList: an empty list");
    Check(ConVarNames.BinaryPath("game", false).Replace('\\', '/') == "game/csgo/bin/linuxsteamrt64/libserver.so", "the game DLL on Linux");
    Check(ConVarNames.BinaryPath("game", true).Replace('\\', '/') == "game/csgo/bin/win64/server.dll", "the game DLL on Windows");
}

// What the engine's answers make of the strings.
const ulong game = ConVarNames.GameDll, cheat = 1ul << 14, release = 1ul << 19;
var engine = new Dictionary<string, ConVarNames.Answer>(StringComparer.OrdinalIgnoreCase)
{
    ["sv_listed"] = new(game, 0),
    ["sv_new"] = new(game | release, 7),
    ["mp_new"] = new(game | cheat, 3),
    ["sv_string_new"] = new(game, 9),
    ["sv_angle_new"] = new(game, ConVarNames.LastKnownType),
    ["sv_of_the_engine"] = new(release, 0),
    ["sv_dev_only"] = new(game | 1ul << 1, 0),
    ["sv_hidden"] = new(game | 1ul << 4, 0),
    ["sv_reference"] = new(game | 1ul << 10, 0),
    ["sv_newer_type"] = new(game, ConVarNames.LastKnownType + 1),
    ["sv_no_type"] = new(game, -1),
};
ConVarNames.Answer? Engine(string name) => engine.TryGetValue(name, out var a) ? a : null;
{
    string[] four = ["sv_listed", "mp_listed", "bot_listed", "ai_listed"];
    var strings = Scan(Image(engine.Keys.Concat(four).Concat(["not_a_convar", "MP_LISTED", "A10_7CHandleI11CBaseEntityE"]).ToArray()));
    var r = ConVarNames.Discover(four, strings, Engine);
    Eq(r.Refused, null, "a normal answer is used");
    Eq(Names(r.New), "mp_new,sv_angle_new,sv_new,sv_string_new", "new: visible game convars of a known type the list lacks, sorted");
    Eq(r.OddType, 2, "a game convar of a type this plugin does not know is left out and counted");
    Eq(r.Seen, 4, "the list's names in the file, without case");
    Eq(r.Listed, 4, "the list's size");
    Check(r.Describe(5).Contains("4 game convars only this build has: mp_new, sv_angle_new, sv_new, sv_string_new") && r.Describe(5).Contains("2 of an unknown type left out"), $"the line says what it found: {r.Describe(5)}");

    var same = ConVarNames.Discover(["sv_listed"], Scan(Image("sv_listed", "not_a_convar")), Engine);
    Eq(same.New.Count, 0, "nothing new when the build holds nothing new");
    Check(same.Describe(5).Contains("0 game convars only this build has") && !same.Describe(5).Contains(": sv"), $"and the line says so: {same.Describe(5)}");

    Eq(Names(ConVarNames.Discover([], strings, Engine).New), "mp_new,sv_angle_new,sv_listed,sv_new,sv_string_new", "no list at all: everything from the build");
}

// Refused: the list alone stays, and it says why. The engine's answers are never asked for or used then.
{
    var some = Enumerable.Range(0, 10).Select(i => $"sv_listed_{i}").ToList();
    var asked = 0;
    ConVarNames.Answer? AlwaysGame(string _) { asked++; return new(game, 0); }

    var empty = ConVarNames.Discover(some, Scan([]), AlwaysGame);
    Check(empty.Refused != null && empty.New.Count == 0 && asked == 0, "no strings in the file: refused, nobody asked");
    Check(empty.Describe(5).StartsWith("convars.txt only (10 names)"), $"and the line says convars.txt only: {empty.Describe(5)}");

    var wrong = ConVarNames.Discover(some, Scan(Image("sv_listed_0", "sv_listed_1", "sv_listed_2", "sv_listed_3", "something_else")), AlwaysGame);
    Check(wrong.Refused != null && wrong.New.Count == 0 && asked == 0, "not the file it should be (4 of 10 names): refused, nobody asked");

    var half = ConVarNames.Discover(some, Scan(Image("sv_listed_0", "sv_listed_1", "sv_listed_2", "sv_listed_3", "sv_listed_4", "something_new")), AlwaysGame);
    Check(half.Refused == null && half.New.SequenceEqual(["something_new"]), "half of the names is enough: a big update may drop many");

    var two = new[] { "sv_a", "sv_b" };
    var enough = ConVarNames.Discover(two, Scan(Image("sv_a", "sv_b", "new_1", "new_2")), AlwaysGame);
    Check(enough.Refused == null && enough.New.Count == 2, "as many new game convars as the list has names is believable");
    var tooMany = ConVarNames.Discover(two, Scan(Image("sv_a", "sv_b", "new_1", "new_2", "new_3")), AlwaysGame);
    Check(tooMany.Refused != null && tooMany.New.Count == 0, "more new game convars than the list has names is not: an engine calling everything a convar");
}

// The whole question never throws, and a failure anywhere leaves the list alone.
{
    var list = new[] { "sv_listed" };
    var image = Image("sv_listed", "sv_new");
    var ok = ConVarNames.Ask(() => new MemoryStream(image), list, Engine);
    Check(ok.Refused == null && ok.New.SequenceEqual(["sv_new"]), "Ask: the normal way");

    var missing = ConVarNames.Ask(() => throw new FileNotFoundException("no such file"), list, Engine);
    Check(missing.Refused == "no such file" && missing.New.Count == 0 && missing.Listed == 1, "Ask: the file is not there");
    var unreadable = ConVarNames.Ask(() => new Failing(image), list, Engine);
    Check(unreadable.Refused == "the disk went away" && unreadable.New.Count == 0, "Ask: the file breaks off while being read");
    var thrown = ConVarNames.Ask(() => new MemoryStream(image), list, _ => throw new InvalidOperationException("native error"));
    Check(thrown.Refused == "native error" && thrown.New.Count == 0, "Ask: the engine call fails");
    Check(thrown.Describe(5).StartsWith("convars.txt only"), "and the line says convars.txt only");
}

// The real list: every name must be one the scan would read, or a good file would look like a wrong one.
var listed = new List<string>();
using (var resource = typeof(ConVarNames).Assembly.GetManifestResourceStream("convars.txt"))
{
    Check(resource != null, "convars.txt is embedded");
    if (resource != null) { using var reader = new StreamReader(resource); listed = ConVarNames.ParseList(reader); }
}
Check(listed.Count > 1000, $"convars.txt holds the game convars, got {listed.Count}");
Eq(listed.Distinct(StringComparer.OrdinalIgnoreCase).Count(), listed.Count, "convars.txt names each convar once");
Check(listed.SequenceEqual(listed.Order(StringComparer.Ordinal)), "convars.txt is sorted");
{
    var strings = Scan(Image(listed.ToArray()));
    Eq(strings.Count, listed.Count, "every name of convars.txt is a name the scan reads");
    var r = ConVarNames.Discover(listed, strings, Engine);
    Check(r.Refused == null && r.New.Count == 0 && r.Seen == listed.Count, "a file holding just the list: all seen, nothing new");
}

// A real game DLL and a saved cvarlist of the same build, when given (neither is in this repository): OVERSTRIKE2_SERVER_BINARY = the file,
// OVERSTRIKE2_CVARLIST = cvarlist's whole reply. Replays the engine's real answers, for hidden convars too: cvarlist does not show those, so
// they are "not found" here, which the flags test in ConVarNames.Discover gives on a server. After a CS2 update this tells whether the route
// still sees all of it.
var binaryPath = Environment.GetEnvironmentVariable("OVERSTRIKE2_SERVER_BINARY");
var cvarlistPath = Environment.GetEnvironmentVariable("OVERSTRIKE2_CVARLIST");
if (binaryPath == null) Console.WriteLine("(no OVERSTRIKE2_SERVER_BINARY: the check against a real game DLL is skipped)");
else
{
    var clock = Stopwatch.StartNew();
    using var file = File.OpenRead(binaryPath);
    var strings = ConVarNames.Strings(file);
    var read = clock.ElapsedMilliseconds;
    var seen = listed.Count(strings.Contains);
    Console.WriteLine($"game DLL: {strings.Count} strings in {read} ms, {seen} of the {listed.Count} names of convars.txt among them");
    Eq(seen, listed.Count, "every name of convars.txt is a string in the real game DLL");
    if (cvarlistPath == null) Console.WriteLine("(no OVERSTRIKE2_CVARLIST: the replay of a real cvarlist is skipped)");
    else
    {
        var rows = File.ReadLines(cvarlistPath).Select(l => Regex.Match(l, @"^(\S+)\s+: (.*?)\s*: (.*?)\s*: (.*)$")).Where(m => m.Success && m.Groups[2].Value != "cmd")
            .ToDictionary(m => m.Groups[1].Value, m => new ConVarNames.Answer(m.Groups[3].Value.Split(',').Any(f => f.Trim() == "sv") ? game : 0, 0), StringComparer.OrdinalIgnoreCase);
        ConVarNames.Answer? Replay(string name) => rows.TryGetValue(name, out var a) ? a : null;
        var truth = rows.Where(kv => (kv.Value.Flags & game) != 0).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scratch = ConVarNames.Discover([], strings, Replay);
        Console.WriteLine($"replay: {rows.Count} convars in the cvarlist, {truth.Count} of them game convars; with no list at all the route finds {scratch.New.Count}; with convars.txt it adds: {Names(ConVarNames.Discover(listed, strings, Replay).New)}");
        Check(truth.SetEquals(scratch.New), $"with no list at all the route finds exactly cvarlist's game convars, missing {Names(truth.Except(scratch.New).Take(20))}");
        // A list older than the build: every setting it lacks comes back, and nothing else.
        var older = listed.Where((_, i) => i % 100 != 0).ToList();
        var dropped = listed.Except(older).Where(truth.Contains).Order(StringComparer.Ordinal);
        Check(ConVarNames.Discover(older, strings, Replay).New.SequenceEqual(dropped), "a list that lacks some of the build's game convars gets exactly those back");
    }
}

// ---- The Steam Web API's one door (SteamWebApi.cs, the owner's rule of 2026-11-04): three callers at once go one at a time, 2 s apart ----
{
    var seen = new System.Collections.Concurrent.ConcurrentQueue<(DateTime Start, DateTime End)>();
    var inFlight = 0;
    var most = 0;
    using var http = new HttpClient(new Recorder(seen, () => { var n = Interlocked.Increment(ref inFlight); most = Math.Max(most, n); }, () => Interlocked.Decrement(ref inFlight)));
    var calls = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
    {
        using var r = await OverStrike2.SteamWebApi.PostAsync(http, "https://api.steampowered.com/test", new StringContent("x")).ConfigureAwait(false);
    })).ToArray();
    Task.WaitAll(calls);
    var times = seen.OrderBy(t => t.Start).ToList();
    var gaps = times.Zip(times.Skip(1), (a, b) => (b.Start - a.End).TotalSeconds).ToList();
    Check(times.Count == 3 && most == 1, $"the Web API's door: three callers at once, one request at a time (at most {most} in flight)");
    Check(gaps.All(g => g >= OverStrike2.SteamWebApi.Gap.TotalSeconds - 0.05), $"...each at least {OverStrike2.SteamWebApi.Gap.TotalSeconds:0} s after the last ended ({string.Join(", ", gaps.Select(g => g.ToString("0.00")))} s)");
}

Console.WriteLine(failures == 0
    ? $"Over Strike 2 plugin self-test: {checks} checks passed."
    : $"Over Strike 2 plugin self-test: {failures} of {checks} checks FAILED.");
return failures == 0 ? 0 : 1;

/// <summary>A Web API that answers at once, noting when each request started and ended.</summary>
sealed class Recorder(System.Collections.Concurrent.ConcurrentQueue<(DateTime, DateTime)> seen, Action enter, Action leave) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        enter();
        var start = DateTime.UtcNow;
        await Task.Delay(50, cancel).ConfigureAwait(false);
        leave();
        seen.Enqueue((start, DateTime.UtcNow));
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
    }
}

/// <summary>A file that hands over one byte at a time.</summary>
sealed class Trickle(byte[] data) : MemoryStream(data)
{
    public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
}

/// <summary>A file that breaks off after a few bytes.</summary>
sealed class Failing(byte[] data) : MemoryStream(data)
{
    public override int Read(byte[] buffer, int offset, int count) => Position < 4 ? base.Read(buffer, offset, 1) : throw new IOException("the disk went away");
}
