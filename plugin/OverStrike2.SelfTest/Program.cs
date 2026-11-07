using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OverStrike2;

// The Over Strike 2 plugin's self-test: the halves of it that need no game server, driven against fakes and a handed clock -
//
//   - when the vote freezes everybody (FreezePlan.cs): only on this map's fresh word from the bridge, freed when the bridge goes quiet or one
//     vote has frozen everybody for too long, and everybody at the same tick;
//   - whom the spawn cloak hides (CloakPlan.cs, SpawnCloakPlan): the window open on the spawn, for as long as it stays open and again after a
//     respawn with it open, and never again in a life that left it, attacked or saw a vote freeze start or end; and what the cloak puts back
//     (CloakBook): every entity that leaves a hidden player, as it looked before - never as it looked hidden, also when it goes from one hidden
//     player to another;
//   - the plugin's settings (PluginSetup.cs, core/Settings.cs): the settings file or its text in OVERSTRIKE2_SETTINGS_CONTENT, none of the
//     variables they replaced, no load without them - and the line that says where they came from, with no value in it;
//   - the block file (bridge/Shared/Modes.cs, Modes.Blocked): every game mode's value in one file, and the plugin's own file held to it;
//   - which game mode the settings module acts in (ModePlan.cs): at a map start, at os_mode, and at a load while a map is on, which ends where a
//     plugin loaded before that map is; and an operator's switch (ModePlan.Again, ModeHandover): a map CS2 brought another mode back under is
//     loaded once more in the told one, the way the bridge loads its own, and a reload keeps the plan rather than the convars the block holds;
//   - the fun page's switches (FunSwitches.cs): a player's change and who made it, the effects that are no convar, what the block leaves to a
//     player, and the undo that puts a map's own value back; and a reload (FunHandover): the instance that goes undoes every change, the next
//     one says so;
//   - the game-changers block when a map turns cheats on (GameChangerBlock.cs, CheatsRevert.cs): the engine's revert of every cheat-flagged
//     setting is part of the block's one undo, said once, naming cheats;
//   - catch-up's card or CS2's centre panel (HudVote.CatchUpCard): the card for any game but one known to hold an older content or one that may hold
//     no HUD, and the chat line either way;
//   - whose time on the Players page shows (JoinTimes.cs): an account's own, through a map change, never the player's before in a slot;
//   - the settings module's question to the running build (ConVarNames.cs), with the engine faked: what it may add to convars.txt, and that
//     anything wrong leaves convars.txt alone;
//   - the Steam Web API's one door (SteamWebApi.cs): one request at a time, 2 s apart, each saying who asks;
//   - the card for a typed Workshop id (WorkshopCard.ServerRefusal): the server's own refusals before the Workshop is asked, the HUD's own
//     item (the settings' hud.addon) among them.
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

const int Hud_LongPollSeconds = 25; // Hud.LongPollSeconds: Hud.cs is not in this project

// ---- the vote's freeze (FreezePlan.cs): on the bridge's fresh word on this map, never past a lost bridge or a stuck answer ----
{
    var t0 = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
    var map = t0.AddMinutes(-5); // this map started five minutes before
    var plan = new FreezePlan();
    Check(!plan.Tick(0, false, t0, map, t0) && !plan.Frozen && plan.Reason == FreezePlan.Why.Over, "freeze: no vote, nobody frozen");
    Check(!plan.Tick(7, false, t0, map, t0) && !plan.Frozen, "freeze: a vote whose state says no freeze freezes nobody");
    var on = plan.Tick(7, true, t0, map, t0);
    Check(on && plan.Frozen && plan.Reason == FreezePlan.Why.Vote && plan.Round == 7 && plan.Since == t0, "freeze: a vote that freezes, on a fresh answer: everybody frozen, once");
    Check(!plan.Tick(7, true, t0.AddSeconds(1), map, t0.AddSeconds(1)) && plan.Frozen, "freeze: ...and it stays, with nothing more to log");
    var gone = plan.Tick(7, false, t0.AddSeconds(30), map, t0.AddSeconds(30));
    Check(gone && !plan.Frozen && plan.Reason == FreezePlan.Why.Over && plan.FreeBecause() == "the vote is over", "freeze: the vote's end frees everybody at once, once");

    plan = new FreezePlan();
    Check(!plan.Tick(8, true, map.AddSeconds(-1), map, t0) && !plan.Frozen && plan.Reason == FreezePlan.Why.OldMap,
        "freeze: the old map's last word (an answer from before this map started) freezes nobody");
    Check(!plan.Tick(8, true, DateTime.MinValue, DateTime.MinValue, t0) && !plan.Frozen && plan.Reason == FreezePlan.Why.OldMap,
        "freeze: no answer at all freezes nobody either");
    Check(plan.Tick(8, true, t0, map, t0) && plan.Frozen, "freeze: the first answer on this map that freezes, freezes");

    plan = new FreezePlan();
    plan.Tick(9, true, t0, DateTime.MinValue, t0); // loaded mid-map: any answer is this map's
    Check(plan.Frozen, "freeze: loaded mid-map, the first answer counts");
    Check(!plan.Tick(9, true, t0, DateTime.MinValue, t0.AddSeconds(FreezePlan.StaleSeconds)) && plan.Frozen, $"freeze: {FreezePlan.StaleSeconds:0} s without an answer is not yet stale");
    var stale = plan.Tick(9, true, t0, DateTime.MinValue, t0.AddSeconds(FreezePlan.StaleSeconds + 0.5));
    Check(stale && !plan.Frozen && plan.Reason == FreezePlan.Why.Stale && plan.FreeBecause().Contains("has not answered"), "freeze: a bridge that stops answering frees everybody - nobody stays frozen behind a lost bridge");
    var back = plan.Tick(9, true, t0.AddSeconds(20), DateTime.MinValue, t0.AddSeconds(20));
    Check(back && plan.Frozen && plan.Since == t0, "freeze: ...and its next answer freezes everybody again if the vote is still on, on the vote's own clock");

    plan = new FreezePlan();
    var when = t0;
    for (var s = 0; s <= FreezePlan.MaxSeconds; s += 1) { when = t0.AddSeconds(s); plan.Tick(10, true, when, map, when); }
    Check(plan.Frozen, $"freeze: {FreezePlan.MaxSeconds:0} s of one vote, still frozen");
    var capped = plan.Tick(10, true, when.AddSeconds(1), map, when.AddSeconds(1));
    Check(capped && !plan.Frozen && plan.Reason == FreezePlan.Why.TooLong && plan.FreeBecause().Contains("past its end"), "freeze: a word without an end time, frozen longer than the most one vote may: everybody free");
    Check(!plan.Tick(10, true, when.AddSeconds(5), map, when.AddSeconds(5)) && !plan.Frozen, "freeze: ...and that round never freezes anybody again");
    Check(plan.Tick(11, true, when.AddSeconds(6), map, when.AddSeconds(6)) && plan.Frozen && plan.Since == when.AddSeconds(6), "freeze: the next round freezes on its own clock");
    // The vote's own end time on the bridge's clock: the 60 s limit holds if the bridge dies or hangs, at the moment the windows close too (HudScreen.VoteLocked).
    var ending = new FreezePlan();
    var ends = 60_000L;
    bool Tick(long atMs) => ending.Tick(20, true, t0.AddMilliseconds(atMs), map, t0.AddMilliseconds(atMs), ends, atMs);
    Tick(1_000);
    Check(ending.Frozen, "freeze: a vote with an end time freezes everybody");
    Tick(ends + (long)(FreezePlan.GraceSeconds * 1000));
    Check(ending.Frozen, "freeze: ...through its end and the grace, the bridge's word on its way");
    Check(Tick(ends + (long)(FreezePlan.GraceSeconds * 1000) + 1) && !ending.Frozen && ending.Reason == FreezePlan.Why.TooLong, "freeze: ...past them everybody is free, whatever the bridge's last word, at about 70 s of a 60 s vote");
    Tick(ends + 20_000);
    Check(!ending.Frozen && ending.Reason == FreezePlan.Why.TooLong, "freeze: ...and that round stays free");
    // A decided vote counts down, the map changes, and the bridge - which hears of the new map from the server's log, seconds late - still says that round freezes everybody.
    var carried = new FreezePlan();
    var (mapOne, mapTwo) = (t0, t0.AddSeconds(70));
    carried.Tick(77, true, t0.AddSeconds(60), mapOne, t0.AddSeconds(60));
    var frozeOnOne = carried.Frozen;
    carried.Tick(77, true, t0.AddSeconds(71), mapTwo, t0.AddSeconds(71));
    Check(frozeOnOne && !carried.Frozen && carried.Reason == FreezePlan.Why.OldMap, "freeze: a round that froze everybody on the map before never freezes on the new one, though the bridge's fresh answer still says so");
    carried.Tick(78, true, t0.AddSeconds(90), mapTwo, t0.AddSeconds(90));
    Check(carried.Frozen, "freeze: ...a new round on the new map does");
    var healthy = new FreezePlan();
    healthy.Tick(30, true, t0, map, t0, 60_000, 0);
    healthy.Tick(30, true, t0.AddSeconds(59), map, t0.AddSeconds(59), 68_000, 59_000); // decided at 59 s: its end is the map change's moment now
    healthy.Tick(30, true, t0.AddSeconds(67), map, t0.AddSeconds(67), 68_000, 67_000);
    Check(healthy.Frozen, "freeze: a vote decided holds everybody through its countdown: its end time moved to the map change's");
    Check(FreezePlan.MaxSeconds >= 180 + 60 && FreezePlan.StaleSeconds >= Hud_LongPollSeconds + 5, "freeze: a word without an end time has room for the longest vote; staleness allows a long poll's silence");
}

// ---- the spawn cloak (CloakPlan.cs, SpawnCloakPlan): the window open on the spawn hides its player; leaving it, an attack and a vote
// freeze's start or end spend the cloak for the rest of the life ----
{
    const double R = SpawnCloakPlan.Radius, H = SpawnCloakPlan.Height;
    var plan = new SpawnCloakPlan();
    plan.Spawned(1, 100);
    Check(!plan.Hides(1, 100, 500, 500, 0, true, true) && plan.Describe(1, 500, 500, 0) == "not placed yet",
        "spawn cloak: nothing in the spawn's own tick - the game may still be placing the pawn - and no spawn point yet");
    Check(!plan.Hides(1, 101, 500, 500, 0, false, true), "spawn cloak: the first look a tick later takes the spawn point; the window shut: seen");
    Check(plan.Hides(1, 102, 500, 500, 0, true, true), "spawn cloak: the window open on the spawn: hidden");
    Check(!plan.Hides(1, 103, 500, 500, 0, false, true) && plan.Ready(1), "spawn cloak: the window shut: seen, and the cloak not spent");
    var late = new SpawnCloakPlan();
    late.Spawned(9, 1000); late.Hides(9, 1001, 0, 0, 0, true, true);
    var anHour = 3600 * 64L; // CS2 runs at 64 ticks a second
    Check(late.Hides(9, 1000 + anHour, 0, 0, 0, true, true) && late.Ready(9) && !late.Hides(9, 1001 + anHour, 0, 0, 0, false, true) && late.Hides(9, 1002 + anHour, 0, 0, 0, true, true),
        "spawn cloak: no time limit - an hour after the spawn, the window open on the spot still hides, shut it does not, and nothing is spent");
    var killed = new SpawnCloakPlan();
    killed.Spawned(8, 2000);
    Check(killed.Hides(8, 2001, 100, 100, 0, true, true), "spawn cloak: killed with the window open, part 1: hidden on the first spawn");
    killed.Ended(8);
    Check(!killed.Hides(8, 2100, 100, 100, 0, true, true), "spawn cloak: ...dead with the window still open: no life, nothing to hide");
    killed.Spawned(8, 2200);
    Check(!killed.Hides(8, 2200, 900, -900, 0, true, true) && killed.Ready(8) && killed.Hides(8, 2201, 900, -900, 0, true, true),
        "spawn cloak: ...the respawn with the window still open: hidden from the first look after it, on the new spawn - far from the old one, and nothing spent");
    var shut = new SpawnCloakPlan();
    shut.Spawned(7, 3000);
    Check(!shut.Hides(7, 3001, 0, 0, 0, false, true) && !shut.Hides(7, 3002, 0, 0, 0, false, true) && shut.Ready(7), "spawn cloak: a normal spawn, the window shut: seen, and the cloak still there for a window that opens on the spot");
    var away = new SpawnCloakPlan();
    away.Spawned(6, 4000); away.Hides(6, 4001, 0, 0, 0, false, true);
    Check(!away.Hides(6, 4002, 500, 0, 0, false, true) && !away.Hides(6, 4003, 500, 0, 0, true, true) && away.SpentBecause(6) == SpawnCloakPlan.Left,
        "spawn cloak: the window opened away from the spawn: seen");
    Check(plan.Hides(1, 104, 500, 500, 0, true, true), "spawn cloak: ...the window open again on the spawn: hidden again");
    Check(plan.Hides(1, 105, 545, 545, 0, true, true), "spawn cloak: a step aside (63 units diagonally) is still the spawn");
    Check(plan.Hides(1, 106, 500, 500 + R, 0, true, true), $"spawn cloak: exactly {R:0} units away is still the spawn");
    Check(plan.Hides(1, 107, 500, 500, 57, true, true), "spawn cloak: a jump on the spot (57 units up) is still the spawn");
    Check(plan.Describe(1, 520, 500, 0).StartsWith("on its spawn, 20 units away"), $"spawn cloak: the console says how far: {plan.Describe(1, 520, 500, 0)}");
    Check(!plan.Hides(1, 108, 500 + R + 1, 500, 0, true, true) && plan.SpentBecause(1) == SpawnCloakPlan.Left,
        $"spawn cloak: {R + 1:0} units away is off the spawn: seen, and the life's cloak spent for leaving");
    Check(!plan.Hides(1, 109, 500, 500, 0, true, true) && plan.Describe(1, 500, 500, 0) == $"spent: {SpawnCloakPlan.Left}",
        "spawn cloak: back on the spot with the window open: still seen - a life that left never hides again");

    plan.Spawned(1, 200);
    Check(plan.Ready(1) && plan.Hides(1, 201, -30, 10, 0, true, true), "spawn cloak: a respawn is a new life on a spawn of its own; the window open at the first look: hidden");
    Check(!plan.Hides(1, 202, -30, 10, H + 1, true, true) && plan.SpentBecause(1) == SpawnCloakPlan.Left, $"spawn cloak: {H + 1:0} units up (a lift, a jump pad) is off the spawn too");

    plan.Spawned(1, 300);
    Check(!plan.Hides(1, 301, 0, 0, 0, true, false) && plan.Ready(1), "spawn cloak: not allowed (a freeze): seen, and nothing spent by it");
    Check(plan.Hides(1, 302, 0, 0, 0, true, true), "spawn cloak: ...allowed again: hidden");
    Check(plan.Spend(1, SpawnCloakPlan.Attacked) && !plan.Spend(1, SpawnCloakPlan.Hurt) && plan.SpentBecause(1) == SpawnCloakPlan.Attacked,
        "spawn cloak: an attack spends the life's cloak, once - the first reason stays");
    Check(!plan.Hides(1, 303, 0, 0, 0, true, true), "spawn cloak: an attacker is seen, on the spawn and with the window open");

    plan.Spawned(1, 400);
    plan.Spawned(2, 400);
    Check(plan.Hides(1, 401, 0, 0, 0, true, true) && !plan.Hides(2, 401, 100, 100, 0, false, true), "spawn cloak: one hidden, one not, before a freeze");
    Check(plan.Freeze(true) && !plan.Freeze(true), "spawn cloak: a freeze's start is seen once");
    Check(plan.SpentBecause(1) == SpawnCloakPlan.FreezeStarted && plan.SpentBecause(2) == SpawnCloakPlan.FreezeStarted && !plan.Hides(1, 402, 0, 0, 0, true, true),
        "spawn cloak: a freeze's start spends every life's cloak, hidden or not");
    plan.Spawned(3, 410);
    Check(!plan.Hides(3, 411, 0, 0, 0, true, false) && plan.Ready(3), "spawn cloak: a life that begins during the freeze is not hidden while it lasts");
    Check(plan.Freeze(false) && plan.SpentBecause(3) == SpawnCloakPlan.FreezeEnded && !plan.Hides(3, 412, 0, 0, 0, true, true),
        "spawn cloak: ...and the freeze's end spends that life's cloak too");
    plan.Spawned(3, 420);
    Check(plan.Hides(3, 421, 0, 0, 0, true, true), "spawn cloak: the next life after a freeze hides again");

    var open = new List<int>();
    plan.Unspent(open);
    Check(open.SequenceEqual([3]), $"spawn cloak: only the lives whose cloak is not spent are looked at ({string.Join(",", open)})");
    plan.Ended(3);
    Check(!plan.Ready(3) && plan.SpentBecause(3) == null && !plan.Hides(3, 422, 0, 0, 0, true, true) && plan.Describe(3, 0, 0, 0) == "no life" && !plan.Spend(3, SpawnCloakPlan.Attacked),
        "spawn cloak: a death or a disconnect ends the life: nothing to hide, nothing to spend");
    plan.Spawned(4, 500);
    plan.Freeze(true);
    plan.MapStarted();
    plan.Unspent(open);
    Check(open.Count == 0 && !plan.Freeze(false), "spawn cloak: a new map forgets every life, and starts with no freeze");
    Check(!SpawnCloakPlan.Off(0, 0, 0, R, 0, H) && !SpawnCloakPlan.Off(0, 0, 0, 0, -R, -H) && SpawnCloakPlan.Off(0, 0, 0, R * 0.8, R * 0.8, 0),
        "spawn cloak: the spawn is a cylinder - Radius sideways in every direction, Height up and down");
    Check(R >= 2 * 32 && R <= 4 * 32 && H >= 57, "spawn cloak: a few player widths (the hull is 32 units wide), and room for a jump on the spot");
}

// ---- what the cloak puts back (CloakPlan.cs, CloakBook): every entity that leaves a hidden player, as it looked before the cloak ----
{
    var book = new CloakBook<string>();
    void Hold(uint handle, int slot, string before) { if (!book.Hold(handle, slot)) book.Add(handle, slot, before); }

    book.Begin();
    Hold(10, 1, "pawn seen");
    Hold(11, 1, "gun seen");
    Check(book.End().Count == 0 && book.Count == 2 && book.Of(1) == 2, "cloak book: a hidden player's pawn and gun, each with its look from before");
    book.Begin();
    Check(book.Hold(10, 1) && book.Hold(11, 1) && book.End().Count == 0, "cloak book: held again, nothing is taken again (a hidden look never becomes a before) and nothing goes back");

    book.Begin();
    Hold(10, 1, "pawn hidden");
    Hold(12, 1, "grenade seen");
    var back = book.End().ToList();
    Check(back.Count == 1 && back[0] == (11u, 1, "gun seen") && book.Count == 2, "cloak book: a dropped gun goes back as it looked before; a pickup comes in");
    Check(book.Held(1).Order().SequenceEqual(["grenade seen", "pawn seen"]), "cloak book: the pawn keeps the look it had before the first pass");

    book.Begin();
    Hold(20, 2, "pawn 2 seen");
    Check(book.Hold(12, 2), "cloak book: a grenade another hidden player held is in the book already");
    book.Hold(10, 1);
    Check(book.End().Count == 0 && book.Of(2) == 2 && book.Of(1) == 1, "cloak book: going from one hidden player to another, nothing goes back");

    book.Begin();
    book.Keep(2); // an error while holding player 2: what they had stays as it is
    book.Hold(10, 1);
    Check(book.End().Count == 0 && book.Of(2) == 2, "cloak book: a pass that could not hold a player keeps all of theirs - nothing goes back half-way");

    book.Begin();
    book.Hold(10, 1);
    var looks = book.End().Select(g => g.Before).Order().ToList();
    Check(looks.SequenceEqual(["grenade seen", "pawn 2 seen"]), $"cloak book: a player seen again: all of theirs back as it looked before any cloak ({string.Join(", ", looks)})");

    book.Begin();
    book.Hold(10, 1);
    Check(!book.Hold(10 | 1u << 15, 1), "cloak book: the same index with another serial is another entity");
    book.Add(10 | 1u << 15, 1, "new entity seen");
    Check(book.Of(1) == 2 && book.Held(1).Order().SequenceEqual(["new entity seen", "pawn seen"]), "cloak book: ...and is held besides it");
    Check(book.Clear().Count == 2 && book.Count == 0, "cloak book: the unload puts everything back");
    book.Add(30, 3, "x");
    book.Forget();
    Check(book.Count == 0 && book.End().Count == 0, "cloak book: a new map forgets everything and puts nothing back");
    Check(((CloakWhy.Ghost | CloakWhy.Spawn) & ~CloakWhy.Ghost) == CloakWhy.Spawn, "cloak: one reason ends, the other still hides");
}

// ---- the plugin's settings (PluginSetup.cs, core/Settings.cs): the settings file or its text in OVERSTRIKE2_SETTINGS_CONTENT, and nothing else ----
{
    const string standard = "/etc/overstrike2/overstrike2.jsonc";
    Func<string, string?> Env(Dictionary<string, string> vars) => name => vars.TryGetValue(name, out var v) ? v : null;
    static string NoFile(string p) => throw new FileNotFoundException(p);
    OverStrike2.Core.Settings Load(Func<string, string?> env, Func<string, string> read) => OverStrike2.Core.Settings.Load(OverStrike2.Core.Part.Plugin, null, env, read);
    var vars = new Dictionary<string, string>
    {
        ["OS2_HUD_ADDON"] = "500", ["CS2_LOG_HTTP_URL"] = "http:\\/\\/env-bridge:8080\\/log\\/envsecret41", ["HUD_ITEM_PAGE"] = "off",
        ["TEST_SECRET"] = "filesecret42",
    };

    var none = Load(Env(vars), NoFile);
    Check(none.Fatal != null && none.Troubled && none.StartLine.StartsWith($"settings: no settings file at {standard} and no OVERSTRIKE2_SETTINGS_CONTENT - hud.addon: missing")
        && none.StartLine.EndsWith(" - not starting: no part runs without its settings"),
        "settings: no settings anywhere - the plugin does not load; the variables the settings replaced (OS2_HUD_ADDON, CS2_LOG_HTTP_URL, HUD_ITEM_PAGE) stand in for nothing");

    const string text = """
        {
          "schema": 1,
          "server": { "host": "not-the-plugins" },   // a key the plugin does not read: left alone
          "bridge": { "url": "http://file-bridge:8081/", "secret": "env:TEST_SECRET" },
          "hud": { "addon": "3823993566", "itemPage": false },
          "steam": { "apiGapSeconds": 5 },
        }
        """;
    var file = Load(Env(vars), p => p == standard ? text : NoFile(p));
    var f = PluginSetup.FromFile(file);
    Check(file.Fatal == null && !file.Troubled && f is { Addon: "3823993566", Bridge: "http://file-bridge:8081", BridgeSecret: "filesecret42",
        BridgeWhy: "http://file-bridge:8081", ItemPage: false, ItemPageSwitch: "hud.itemPage" } && f.ApiGapSeconds == 5,
        "settings: a file that can be used - its values, the secret by reference; CS2_LOG_HTTP_URL and the addon variable do not count");
    Check(file.StartLine == $"settings: the settings file {standard}" && new[] { "filesecret42", "file-bridge", "3823993566", "envsecret41" }.All(x => !file.StartLine.Contains(x)),
        "settings: ...the line names the file and nothing it holds");
    var inline = Load(Env(new() { ["OVERSTRIKE2_SETTINGS_CONTENT"] = """{ "schema": 1, "hud": { "addon": "3823993566" }, "bridge": { "url": "http://inline:8080", "secret": "abc" } }""" }), NoFile);
    Check(inline.Fatal == null && PluginSetup.FromFile(inline) is { Addon: "3823993566", Bridge: "http://inline:8080", BridgeSecret: "abc" } && inline.StartLine == "settings: the environment variable OVERSTRIKE2_SETTINGS_CONTENT",
        "settings: the settings as text in OVERSTRIKE2_SETTINGS_CONTENT count as the file, and the line names the variable, never the text");
    var noSecret = Load(Env(new()), _ => """{ "schema": 1, "hud": { "addon": "3823993566" }, "bridge": { "secret": "env:TEST_SECRET" } }""");
    Check(noSecret.Fatal == null && PluginSetup.FromFile(noSecret) is { Bridge: null, BridgeWhy: "bridge.secret is empty", Addon: "3823993566" } && noSecret.StartLine.EndsWith(" - 1 warning (bridge.secret)"),
        "settings: a file whose secret's variable is not set - no bridge, the HUD's log line says why, the settings line names the key");
    var slow = Load(Env(new()), _ => """{ "schema": 1, "steam": { "apiGapSeconds": 0.5 }, "hud": { "addon": "3823993566", "itemPage": "no" } }""");
    Check(slow.Fatal == null && slow.Troubled && PluginSetup.FromFile(slow) is { ApiGapSeconds: 2, ItemPage: true }
        && slow.StartLine == $"settings: the settings file {standard} - 2 problems (hud.itemPage, steam.apiGapSeconds)",
        "settings: a gap under 2 s is clamped to 2 and a wrong switch keeps its default - problems by key, never their values");
    var broken = Load(Env(vars), _ => "{ not json");
    Check(broken.Fatal != null && broken.Troubled && broken.StartLine.StartsWith($"settings: the settings file {standard} cannot be used (")
        && broken.StartLine.EndsWith(" - not starting: no part runs on settings it cannot use"),
        "settings: a file that cannot be used - the plugin does not load (never the environment's values), and the line says why");
    var noAddon = Load(Env(vars), _ => """{ "schema": 1, "bridge": { "url": "http://x:8080", "secret": "abc" } }""");
    Check(noAddon.Fatal != null && noAddon.StartLine.Contains("hud.addon"), "settings: a file without hud.addon - the plugin does not load: a server without the HUD cannot exist");

    var gapBefore = SteamWebApi.Gap;
    SteamWebApi.Gap = TimeSpan.FromSeconds(1);
    var floored = SteamWebApi.Gap;
    SteamWebApi.Gap = TimeSpan.FromSeconds(5);
    var raised = SteamWebApi.Gap;
    SteamWebApi.Gap = gapBefore;
    Check(floored == TimeSpan.FromSeconds(2) && raised == TimeSpan.FromSeconds(5) && gapBefore == TimeSpan.FromSeconds(2),
        "settings: the Web API's door takes the gap from the settings, never under 2 s (2 s until set)");
}

// ---- the card for a typed or pasted Workshop id (HudScreenClicks.WorkshopCards): the server's own refusals come before the Workshop is asked -
// the catalog's, and our HUD's own item, its id the settings' hud.addon - so the HUD is never offered as a map to download and play ----
{
    static string NoFile(string p) => throw new FileNotFoundException(p);
    var settings = OverStrike2.Core.Settings.Load(OverStrike2.Core.Part.Plugin, null, _ => null,
        p => p == OverStrike2.Core.Settings.DefaultPath ? """{ "schema": 1, "hud": { "addon": "3900000777" }, "bridge": { "secret": "abc" } }""" : NoFile(p));
    var setup = PluginSetup.FromFile(settings);
    var card = new WorkshopCard();
    card.Show(3900000777);
    if (WorkshopCard.ServerRefusal(card.Id, null, setup.Addon) is { } no) card.Refuse(no.Name, no.Why);
    Check(settings.Fatal == null && setup.Addon == "3900000777" && card.At == WorkshopCard.Step.Refused && card.Named == WorkshopCard.HudItemName && card.Why == WorkshopCard.NotAMap,
        "workshop card: our HUD's own item, by the settings' hud.addon, is refused as no map before the Workshop is asked - with no catalog yet too");
    var plugin = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OverStrike2");
    var clicksCs = Path.Combine(plugin, "HudScreenClicks.cs");
    var screenCs = Path.Combine(plugin, "HudScreen.cs");
    Check(File.Exists(clicksCs) && File.Exists(screenCs)
        && Regex.IsMatch(File.ReadAllText(clicksCs), @"case WorkshopCard\.Step\.Checking:\s*if \(WorkshopCard\.ServerRefusal\(card\.Id, catalog, addon\) is \{ \} no\) card\.Refuse\(no\.Name, no\.Why\);")
        && File.ReadAllText(screenCs).Contains("addon = setup.Addon;", StringComparison.Ordinal),
        "workshop card: the plugin asks ServerRefusal first, with the catalog and the HUD's item from the settings (hud.addon), before the Workshop");
}

// ---- the block file (bridge/Shared/Modes.cs): every game mode's value in one file ----
{
    var text = "// a comment\n\ngame_type  1  1  0  // the mode\nsv_cheats  *  0  0  // cheats\r\nbot_quota_mode * normal normal // bots\n"
        + "mp_buytime * 0 // two values only\nbad-name 1 1 1 // x\nsv_cheats 1 1 1 // again\n";
    var ar = Modes.Blocked(text, Modes.ArmsRace, out var arErrors);
    var dm = Modes.Blocked(text, Modes.Deathmatch, out _);
    var wm = Modes.Blocked(text, Modes.Wingman, out _);
    Check(ar.Select(e => (e.ConVar, e.Value)).SequenceEqual([("game_type", "1"), ("sv_cheats", null), ("bot_quota_mode", null)]),
        $"block file: Arms Race's values, * for the server's own: {string.Join(", ", ar.Select(e => $"{e.ConVar} {e.Value ?? "*"}"))}");
    Check(dm.Select(e => e.Value).SequenceEqual(["1", "0", "normal"]) && wm.Select(e => e.Value).SequenceEqual(["0", "0", "normal"]), "block file: each mode its own column");
    Check(ar[0].Why == "the mode" && ar[1].Why == "cheats", "block file: every line's why, after //");
    Check(arErrors.Count == 3 && arErrors.Any(e => e.StartsWith("line 6:") && e.Contains("for each of the 3 modes")) && arErrors.Any(e => e.StartsWith("line 7:"))
        && arErrors.Any(e => e.StartsWith("line 8:") && e.Contains("twice")), $"block file: a line short of a value per mode, a bad name and a name twice are errors, by the file's own line: {string.Join(" | ", arErrors)}");
    Eq(Modes.BlockFile, "blocked.txt", "block file: one file, beside the DLL");
    var committed = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OverStrike2", Modes.BlockFile);
    if (!File.Exists(committed)) Console.WriteLine($"(no {committed}: the plugin's own block file is not checked)");
    else
    {
        var file = File.ReadAllText(committed);
        var lists = Modes.All.ToDictionary(m => m.Id, m => (List: Modes.Blocked(file, m, out var errors), Errors: errors));
        Check(lists.Values.All(l => l.Errors.Count == 0 && l.List.Count == 30), $"the plugin's {Modes.BlockFile}: every mode's 30 values parse ({string.Join(", ", lists.Select(kv => $"{kv.Key} {kv.Value.List.Count}, {kv.Value.Errors.Count} error(s)"))})");
        Check(Modes.All.All(m => lists[m.Id].List.Single(e => e.ConVar == "game_type").Value == $"{m.GameType}" && lists[m.Id].List.Single(e => e.ConVar == "game_mode").Value == $"{m.GameMode}"),
            "...each mode keeps its own game_type and game_mode");
        Check(lists.Where(kv => kv.Key != Modes.ArmsRaceId).All(kv => kv.Value.List.All(e => e.Value != null)), "...and only Arms Race keeps the server's own (*): the boot baseline is an Arms Race map's");
        Check(Modes.All.All(m => lists[m.Id].List.Single(e => e.ConVar == "mp_death_drop_grenade").Value is null or "0" or "1" or "2"),
            "...and every mode's grenade drop a value CS2 keeps (0 to 2): Wingman's 3, Valve's own, was refused and read back 2 on de_inferno");
    }
}

// ---- the game mode (ModePlan.cs): whose block list and Arms Race-only features are on. A plugin loaded while a map is on (a reload, css_plugins
// load) decides at its load as one loaded before that map decided at its start, so Arms Race's list is never forced onto a Deathmatch or
// Wingman map, nor the grenade drops and catch-up left on there ----
{
    var plugin = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OverStrike2");
    var file = File.Exists(Path.Combine(plugin, Modes.BlockFile)) ? File.ReadAllText(Path.Combine(plugin, Modes.BlockFile)) : null;
    static ModePlan FromStart(Modes.Mode m) // loaded at the server's start: the boot map in Arms Race, then os_mode and the start of a map in m
    {
        var plan = new ModePlan();
        plan.Started("ar_baggage", true, Modes.ArmsRace.GameType, Modes.ArmsRace.GameMode);
        plan.Tell(m);
        plan.Started("aim_pillar", false, m.GameType, m.GameMode);
        return plan;
    }
    foreach (var m in Modes.All)
    {
        var fresh = FromStart(m);
        var load = new ModePlan(); // a reload's new instance, on that map, with nothing handed over
        var before = load.On;
        var switched = load.Started("aim_pillar", false, m.GameType, m.GameMode); // what GameSettings.Load asks while a map is on
        Check(before == Modes.ArmsRace && load.On == m && fresh.On == m && load.LoadedIn == fresh.LoadedIn && load.Told == fresh.Told,
            $"mode: a plugin loaded while a {m.Name} map is on acts in {m.Name}, as one loaded before that map does (on {load.On.Name}, told {load.Told?.Name})");
        Check(switched == (m.ArmsRace ? null : m), $"...and switches the list and the features over from Arms Race, where every load starts, only for another mode ({m.Name})");
        if (file != null)
            Check(Modes.Blocked(file, load.On, out _).SequenceEqual(Modes.Blocked(file, fresh.On, out _)), $"...with the block list of a plugin loaded before that map: {m.Name}'s");
    }
    if (file != null)
        Check(Modes.All.Where(m => !m.ArmsRace).All(m => !Modes.Blocked(file, m, out _).SequenceEqual(Modes.Blocked(file, Modes.ArmsRace, out _))),
            "mode: Deathmatch's and Wingman's block lists are not Arms Race's, which a load kept on every map before");
    var casual = new ModePlan();
    Check(casual.Started("cs_office", true, 0, 0) == null && casual.On == Modes.ArmsRace && casual.LoadedIn == null, "mode: a load on a map in none of our modes (Casual) keeps Arms Race, as a map start does");
    var dm = new ModePlan();
    dm.Started("de_dust2", true, Modes.Deathmatch.GameType, Modes.Deathmatch.GameMode);
    Check(dm.Tell(Modes.Deathmatch) == null && dm.On == Modes.Deathmatch, "mode: after a load on a Deathmatch map, the bridge's os_mode deathmatch finds it on already");
    Check(dm.Tell(Modes.Wingman) == null && dm.On == Modes.Deathmatch && dm.Told == Modes.Wingman && dm.Started("de_inferno", true, Modes.Wingman.GameType, Modes.Wingman.GameMode) == Modes.Wingman,
        "mode: os_mode wingman keeps Deathmatch's list on until the Wingman map starts");
    var back = FromStart(Modes.Deathmatch);
    back.Tell(Modes.Deathmatch);
    Check(back.Started("de_dust2", true, Modes.ArmsRace.GameType, Modes.ArmsRace.GameMode) == Modes.ArmsRace && back.Told == Modes.Deathmatch && back.Again == Modes.Deathmatch,
        "mode: a map told Deathmatch that comes up in Arms Race (a changelevel's saved convars) gets Arms Race's list until it is loaded again, in Deathmatch");
    // The plan's caller: GameSettings.Load asks it before it reads the block list, the way OnMapStart does, and the host lets the modules hear of it.
    var settingsCs = Path.Combine(plugin, "GameSettings.cs");
    var hostCs = Path.Combine(plugin, "OverStrike2.cs");
    Check(File.Exists(settingsCs) && File.Exists(hostCs), $"mode: the settings module and the host are read from their source ({settingsCs})");
    if (File.Exists(settingsCs) && File.Exists(hostCs))
    {
        var code = File.ReadAllText(settingsCs);
        var load = code[code.IndexOf("public void Load(bool hotReload)", StringComparison.Ordinal)..];
        load = load[..load.IndexOf("\n    }\n", StringComparison.Ordinal)];
        var host = File.ReadAllText(hostCs);
        Check(load.IndexOf("modeAtLoad = MapMode(map);", StringComparison.Ordinal) is >= 0 and var at && at < load.IndexOf("block.Read(", StringComparison.Ordinal)
            && load.IndexOf("TakeMode()", StringComparison.Ordinal) is >= 0 and var take && take < at
            && code.Contains("if (MapMode(name) is { } m)", StringComparison.Ordinal), "mode: GameSettings.Load takes the plan handed over, else decides by the map that is on as OnMapStart does, before it reads the block list");
        Check(host.IndexOf("settings.Ready();", StringComparison.Ordinal) > host.IndexOf("settings.ModeApplied +=", StringComparison.Ordinal),
            "mode: the plugin tells its modules a load's mode once they follow ModeApplied");
    }
}

// ---- an operator's mode switch (ModePlan.Again, ModeHandover; GameSettings.OnMapStart, LoadAgain, SetMode, Load, Unload): os_mode wingman, game_type 0,
// game_mode 2 and changelevel de_inferno, each its own command. Live on plugin 5.1.0 (2026-11-07), from aim_deagle - a Workshop map the dice had loaded
// from ar_baggage - de_inferno came up in Arms Race: on a changelevel from a Workshop map to an official one CS2 brings back the convars it saved on the
// way from an official map to a Workshop map, game_type and game_mode with them. The plugin kept Arms Race's list, right for the game that ran, while
// its block held game_type and game_mode at Wingman's, as told; a plugin reload then read those and put Wingman's list on the Arms Race game. The same
// commands from de_dust2 came up in Wingman. Now the map is loaded once more in the told mode, and a reload keeps the plan ----
{
    var dir = Path.Combine(Path.GetTempPath(), $"os2-mode-selftest-{Environment.ProcessId}");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, Modes.BlockFile), "game_type  1  1  0  // the game mode\ngame_mode  0  2  2  // the game mode\n");
    var cs2 = new FakeCs2(); // the server's start: Arms Race
    var plan = new ModePlan();
    var block = new GameChangerBlock(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cs2.ConVars, new FunSwitches(cs2.ConVars.Value, cs2.ConVars.Write), _ => { });
    Modes.Mode? Start(string command, string map, bool official) // a map command, and the map's start as GameSettings.OnMapStart takes it
    {
        cs2.Load(command, map, official);
        var switched = plan.Started(map, official, cs2.GameType, cs2.GameMode);
        block.Read(plan.On);
        return switched;
    }
    string ModeNow() => $"{cs2.GameType}/{cs2.GameMode}";

    Start("map", "ar_baggage", true);
    Start("host_workshop_map", "aim_deagle", false); // the dice: CS2 saves Arms Race on the way
    Check(plan.On == Modes.ArmsRace && plan.Again == null && cs2.Saved == "1/0", "operator: a Workshop map loaded from an official one, in Arms Race (CS2 saves the convars on the way there)");
    cs2.ConVars.Values["game_type"] = "0";
    block.Hold("aim_deagle", null, plan.Told);
    Check(ModeNow() == "1/0", "operator: game_type set before os_mode is the block's to undo - it holds the mode told, Arms Race's - so os_mode goes first");
    Check(plan.Tell(Modes.Wingman) == null && plan.On == Modes.ArmsRace && plan.Told == Modes.Wingman, "operator: os_mode wingman on the Workshop map: Wingman's list and features at the next map start");
    (cs2.ConVars.Values["game_type"], cs2.ConVars.Values["game_mode"]) = ("0", "2"); // game_type 0, game_mode 2
    block.Hold("aim_deagle", null, plan.Told);
    Check(ModeNow() == "0/2", "operator: ...then game_type 0 and game_mode 2 stay: the block holds the told mode's values, so it undoes nothing between the commands");

    Check(Start("changelevel", "de_inferno", true) == null && ModeNow() == "1/0" && plan.LoadedIn == Modes.ArmsRace && plan.On == Modes.ArmsRace && plan.Restored,
        $"operator: changelevel de_inferno, a Workshop map to an official one: CS2 brings Arms Race back, and the map keeps Arms Race's list, the game it runs ({ModeNow()})");
    Check(plan.Again == Modes.Wingman && plan.Describe() == "in Arms Race (Wingman told: de_inferno came up in Arms Race right after a Workshop map, and loads once more in Wingman)",
        $"operator: ...and it is to be loaded once more in Wingman, as os_settings says ({plan.Describe()})");
    // The bridge's question after a start it did not ask for (bare os_mode, GameSettings.ModeState): asked now - the race, before the first frame
    // has the told values back in the convars - the answer is the plan's, Wingman told, where the convars still say Arms Race.
    var answer = Modes.StateLine(plan.Told, plan.LoadedIn, plan.On, plan.Describe());
    Check(ModeNow() == "1/0" && Modes.ParseState(answer) == (Modes.Wingman, Modes.ArmsRace)
          && answer.StartsWith("os_mode: told wingman, loaded in armsrace, on armsrace; in Arms Race (Wingman told: ", StringComparison.Ordinal),
        $"operator: bare os_mode before the first frame's write says Wingman told, Arms Race loaded in - the plan's, not the convars' ({answer})");
    block.Hold("de_inferno", null, plan.Told);
    Check(ModeNow() == "0/2" && block.ListFor(plan.Told) == "game_type 0, game_mode 2" && block.ListFor(null) == "game_type 1, game_mode 0",
        "operator: the block holds Wingman's game_type and game_mode meanwhile - the convars name Wingman, not the game played - and os_block shows them as it holds them");

    // A reload now. On 5.1.0 the new instance read those convars and took Wingman for the game; the plan handed over keeps Arms Race's list on.
    var fromConVars = new ModePlan();
    fromConVars.Started("de_inferno", true, 0, 2);
    var t = new DateTime(2026, 11, 7, 5, 55, 26, DateTimeKind.Utc);
    ModeHandover.Put(plan, 21.5, t);
    var taken = ModeHandover.Take("de_inferno", 22.0, t.AddSeconds(4));
    Check(fromConVars.On == Modes.Wingman && taken is { } h && h.On == Modes.ArmsRace && h.Told == Modes.Wingman && h.LoadedIn == Modes.ArmsRace && h.Again == Modes.Wingman && h.Save() == plan.Save(),
        $"operator: a reload meanwhile takes the plan handed over - Arms Race's list on, Wingman told, the second load still due - where the convars would have said Wingman ({taken?.Describe()})");
    ModeHandover.Put(plan, 21.5, t);
    Check(ModeHandover.Take("de_inferno", 22.0, t.AddSeconds(4)) != null && ModeHandover.Take("de_inferno", 22.0, t.AddSeconds(5)) == null, "operator: ...taken once");
    ModeHandover.Put(plan, 21.5, t);
    Check(ModeHandover.Take("de_dust2", 22.0, t.AddSeconds(4)) == null, "operator: ...never on another map");
    ModeHandover.Put(plan, 21.5, t);
    Check(ModeHandover.Take("de_inferno", 3.0, t.AddSeconds(30)) == null, "operator: ...nor on the same map loaded again since (its clock started anew)");
    ModeHandover.Put(plan, 21.5, t);
    Check(ModeHandover.Take("de_inferno", 900.0, t + ModeHandover.Fresh + TimeSpan.FromSeconds(1)) == null && ModePlan.Parse("de_inferno 1 1") == null && ModePlan.Parse(new ModePlan().Save()) is { Map: "", On.Id: Modes.ArmsRaceId },
        $"operator: ...nor more than {ModeHandover.Fresh.TotalMinutes:0} minutes later; a broken plan is none");

    // The bridge's word after the start (SyncMode: os_mode with the mode its probe read - the block's Wingman) changes nothing; a stale one calls it off.
    var stale = ModePlan.Parse(plan.Save())!;
    Check(plan.Tell(Modes.Wingman) == null && plan.Again == Modes.Wingman && stale.Tell(Modes.ArmsRace) == null && stale.On == Modes.ArmsRace && stale.Again == null && stale.Loading() == null,
        "operator: the bridge's os_mode wingman after the start leaves the second load due; an os_mode armsrace calls it off, and the map stays in Arms Race, its list and its convars");

    // The time comes (ModePlan.AgainAfterSeconds): the told mode's convars, then the map command - official to official, which brings nothing back.
    var line = plan.Loading();
    Check(line == "game_type 0;game_mode 2;changelevel de_inferno" && plan.Again == null && plan.LoadingIn == Modes.Wingman && plan.Loading() == null
          && plan.Describe() == "in Arms Race (Wingman told: de_inferno is loading once more, in Wingman)", $"operator: the second load: {line}, once ({plan.Describe()})");
    var command = cs2.Run(line ?? "", official: true);
    Check(command == "changelevel" && plan.Started("de_inferno", true, cs2.GameType, cs2.GameMode) == Modes.Wingman
          && plan.On == Modes.Wingman && plan.LoadedIn == Modes.Wingman && !plan.Restored && plan.Again == null && plan.LoadingIn == null && plan.Describe() == "in Wingman",
        $"operator: ...comes up in Wingman: Wingman's list and features, and no third load ({ModeNow()}, {plan.Describe()})");

    // changelevel and host_workshop_map: the plugin takes them alike, at the map start, by the live convars; CS2 does not. It saves the convars on a
    // host_workshop_map from an official map, as they are then (the mode set right before it included), and brings them back only on a changelevel
    // (or map) from a Workshop map to an official one.
    var cs2b = new FakeCs2();
    var p2 = new ModePlan();
    Modes.Mode? Start2(string command, string map, bool official, Modes.Mode? told = null) // os_mode, game_type and game_mode first, then the map command
    {
        if (told != null) { p2.Tell(told); (cs2b.ConVars.Values["game_type"], cs2b.ConVars.Values["game_mode"]) = (told.GameType.ToString(), told.GameMode.ToString()); }
        cs2b.Load(command, map, official);
        return p2.Started(map, official, cs2b.GameType, cs2b.GameMode);
    }
    Start2("map", "ar_shoots", true);
    Check(Start2("changelevel", "de_inferno", true, Modes.Wingman) == Modes.Wingman && p2.Again == null,
        "operator: the same commands from an official map (live from de_dust2): official to official brings nothing back - Wingman at once, no second load");
    Check(Start2("host_workshop_map", "aim_pillar", false, Modes.Deathmatch) == Modes.Deathmatch && p2.Again == null && cs2b.Saved == "1/2",
        "operator: os_mode deathmatch and host_workshop_map, as the bridge loads every Workshop map: nothing brought back - Deathmatch at once (CS2 saves it on the way)");
    Check(Start2("host_workshop_map", "aim_deagle", false, Modes.ArmsRace) == Modes.ArmsRace && p2.Again == null && cs2b.Saved == "1/2",
        "operator: Workshop map to Workshop map: nothing saved, nothing brought back");
    Check(Start2("changelevel", "de_dust2", true, Modes.Deathmatch) == Modes.Deathmatch && p2.LoadedIn == Modes.Deathmatch && p2.Restored && p2.Again == null,
        "operator: a Workshop map to an official one, told the mode CS2 brings back (Deathmatch, saved on the way out): nothing to load again");
    Check(Start2("host_workshop_map", "aim_pillar", false, Modes.ArmsRace) == Modes.ArmsRace && cs2b.Saved == "1/0" && Start2("changelevel", "de_nuke", true, Modes.Wingman) == null
          && p2.LoadedIn == Modes.ArmsRace && p2.On == Modes.ArmsRace && p2.Again == Modes.Wingman,
        "operator: told another mode than the one brought back: Arms Race's list on the Arms Race game, and a second load due in Wingman - for the bridge's changes too");
    Check(Start2("changelevel", "de_nuke", true, Modes.Wingman) == Modes.Wingman && p2.Again == null && p2.On == Modes.Wingman && !p2.Restored,
        "operator: the bridge's own second load of a change it made (2-4 s after the start, before the plugin's wait ends) lands first: one start after an official map, nothing more to load");
    Directory.Delete(dir, true);

    // The wiring, read from the source: the start arms the wait, which a map change ends; the wait's end asks again and loads; the reply while the
    // plugin's own second load is out keeps the bridge from loading it a third time; a reload hands the plan on.
    var plugin = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OverStrike2");
    var settingsCs = Path.Combine(plugin, "GameSettings.cs");
    var serverLifeCs = Path.Combine(plugin, "..", "..", "bridge", "ServerLife.cs");
    Check(File.Exists(settingsCs) && File.Exists(serverLifeCs), "operator: the settings module and the bridge's map start are read from their source");
    if (File.Exists(settingsCs) && File.Exists(serverLifeCs))
    {
        var code = File.ReadAllText(settingsCs);
        string Body(string head) { var b = code[code.IndexOf(head, StringComparison.Ordinal)..]; return b[..b.IndexOf("\n    }\n", StringComparison.Ordinal)]; }
        var start = Body("void OnMapStart(string name)");
        var arm = Body("void ArmAgain(string name, string why)");
        var loadAgain = Body("void LoadAgain(string name)");
        var setMode = Body("public string SetMode(Modes.Mode m)");
        var unload = Body("public void Unload()");
        var hold = Body("void HoldTold(Modes.Mode m)");
        Check(start.IndexOf("MapMode(name)", StringComparison.Ordinal) is >= 0 and var decided && decided < start.IndexOf("if (modes.Again is { } again)", StringComparison.Ordinal)
              && start.Contains("ArmAgain(name, ", StringComparison.Ordinal)
              && arm.Contains("plugin.AddTimer((float)ModePlan.AgainAfterSeconds, () => LoadAgain(name), TimerFlags.STOP_ON_MAPCHANGE);", StringComparison.Ordinal),
            "operator: a map start that finds the second load due arms the wait, which any map change ends - the bridge's own second load included");
        Check(start.Contains("Server.NextFrame(() => HoldTold(again));", StringComparison.Ordinal)
              && hold.Contains("keeper.Write(\"game_type\", m.GameType.ToString());", StringComparison.Ordinal) && hold.Contains("keeper.Write(\"game_mode\", m.GameMode.ToString());", StringComparison.Ordinal),
            "operator: ...and puts the told mode's game_type and game_mode back on the map's first frame, before the bridge's probe of a start it did not ask for reads them");
        Check(loadAgain.IndexOf("modes.Loading()", StringComparison.Ordinal) is >= 0 and var asked && asked < loadAgain.IndexOf("Server.ExecuteCommand(line);", StringComparison.Ordinal)
              && loadAgain.Contains("post(Messages.News, $\"{name} loads once more, in {mode.Name}\");", StringComparison.Ordinal),
            "operator: the wait's end asks the plan again before it loads, and says so in the bridge's words");
        Check(setMode.Contains("kept; its block list and features at the next map start\"", StringComparison.Ordinal)
              && setMode.Contains("is loading once more, in it\"", StringComparison.Ordinal) && !setMode[setMode.IndexOf("modes.LoadingIn", StringComparison.Ordinal)..].Split('\n')[0].Contains("at the next map start", StringComparison.Ordinal),
            "operator: os_mode's reply keeps the words the bridge loads a map again on, but not while the plugin's own second load is out");
        Check(unload.IndexOf("ModeHandover.Put(modes, Server.CurrentTime, ", StringComparison.Ordinal) is >= 0 and var put && put < unload.IndexOf("fun.Undo(", StringComparison.Ordinal)
              && code.Contains("ModeHandover.Take(map, Server.CurrentTime, ", StringComparison.Ordinal),
            "operator: the unload hands the plan on before anything can stop it, and the load takes it");
        var life = File.ReadAllText(serverLifeCs);
        Check(life.Contains("After(1500, SyncMode);", StringComparison.Ordinal) && ModePlan.AgainAfterSeconds >= 1.5 + 5,
            $"operator: the plugin waits {ModePlan.AgainAfterSeconds} s, well past the bridge's own second load of a change it made (1.5 s after the start it sees, and a few RCON answers)");
        var hostCs = Path.Combine(plugin, "OverStrike2.cs");
        var host = File.Exists(hostCs) ? File.ReadAllText(hostCs) : "";
        var at = host.IndexOf("public void OnModeCommand(", StringComparison.Ordinal);
        var modeCmd = at >= 0 ? host[at..] : "";
        modeCmd = modeCmd.Contains("\n    }\n", StringComparison.Ordinal) ? modeCmd[..modeCmd.IndexOf("\n    }\n", StringComparison.Ordinal)] : "";
        var attributes = at >= 0 ? host[Math.Max(0, at - 400)..at] : "";
        Check(attributes.Contains("[CommandHelper(minArgs: 0, ", StringComparison.Ordinal)
              && modeCmd.IndexOf("if (id == \"\") { command.ReplyToCommand(settings == null ? \"os_mode: no settings module\" : settings.ModeState()); return; }", StringComparison.Ordinal) is >= 0 and var bare
              && bare < modeCmd.IndexOf("settings.SetMode(mode)", StringComparison.Ordinal)
              && code.Contains("public string ModeState() => Modes.StateLine(modes.Told, modes.LoadedIn, modes.On, modes.Describe());", StringComparison.Ordinal),
            "operator: bare os_mode reaches the plugin (no argument needed) and answers from the plan before anything could change it - the bridge's question after a start it did not ask for");
    }
}

// ---- the block's word on game_type and game_mode (GameChangerBlock.ModeMoved, ModePlan.Saved and CanBringBack): put back without a word to the
// players when no map changed them - an os_mode told another mode, or CS2 brought back the mode it saved before the Workshop maps - and said as
// ever when a map did. Live on 2026-11-07, an operator's switch told everybody "This map tried to change the game mode": on aim_deagle after
// os_mode wingman (game_type 1 -> 0, game_mode 0 -> 2), and on gg_vinceville when changelevel de_inferno brought Arms Race's back (game_mode 0 -> 2) ----
{
    var dir = Path.Combine(Path.GetTempPath(), $"os2-modeword-selftest-{Environment.ProcessId}");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, Modes.BlockFile), "game_type  1  1  0  // the game mode\ngame_mode  0  2  2  // the game mode\nsv_infinite_ammo  *  2  0  // the ammo\n");
    const string modeNotice = "This map tried to change the game mode - Undone: the server keeps its own rules.";
    (FakeConVars Cvars, GameChangerBlock Block, List<string> Notices, ListLogger Log, ModePlan Plan) Server((string Map, bool Official)[] maps, (int Type, int Mode) mode)
    {
        var cvars = new FakeConVars();
        (cvars.Values["game_type"], cvars.Values["game_mode"], cvars.Values["sv_infinite_ammo"]) = (mode.Type.ToString(), mode.Mode.ToString(), "2");
        cvars.Baselines["sv_infinite_ammo"] = "2";
        var notices = new List<string>();
        var logged = new ListLogger();
        var block = new GameChangerBlock(dir, logged, cvars, new FunSwitches(cvars.Value, cvars.Write), notices.Add);
        var plan = new ModePlan();
        foreach (var (name, official) in maps) plan.Started(name, official, mode.Type, mode.Mode);
        block.Read(plan.On);
        block.Hold(maps[^1].Map, null, plan.Told, plan.CanBringBack); // a look that finds all as it must be
        return (cvars, block, notices, logged, plan);
    }
    string ModeOf(FakeConVars c) => $"{c.Values["game_type"]}/{c.Values["game_mode"]}";

    // An os_mode on an official map: the convars are Arms Race's, where the last look left them; only the mode told moved.
    var (cv, block, notices, logged, plan) = Server([("ar_baggage", true), ("de_dust2", true)], (1, 0));
    Check(plan.CanBringBack == null && notices.Count == 0, "word: an official map after an official one: CS2 brings nothing back");
    plan.Tell(Modes.Wingman);
    block.Hold("de_dust2", null, plan.Told, plan.CanBringBack);
    Check(ModeOf(cv) == "0/2" && notices.Count == 0 && logged.Lines.Any(l => l.Contains("put back on de_dust2: game_type 1 -> 0 - os_mode told Wingman")) && logged.Lines.Any(l => l.Contains("game_mode 0 -> 2 - os_mode told Wingman")),
        $"word: os_mode wingman, then the block's look: game_type 1 -> 0 and game_mode 0 -> 2 put back, logged, not said ({string.Join(" | ", notices)})");
    (cv, block, notices, logged, plan) = Server([("ar_baggage", true), ("de_dust2", true)], (1, 0));
    plan.Tell(Modes.Wingman);
    cv.Values["game_type"] = "0"; // the operator's game_type 0 before the look
    block.Hold("de_dust2", null, plan.Told, plan.CanBringBack);
    Check(ModeOf(cv) == "0/2" && notices.Count == 0, "word: ...also when the operator's game_type 0 came before the look: game_mode alone, not said");
    cv.Values["game_mode"] = "0"; // later, the map's own
    block.Hold("de_dust2", null, plan.Told, plan.CanBringBack);
    Check(ModeOf(cv) == "0/2" && notices.SequenceEqual([modeNotice]), $"word: ...and a change of the map's own after it on the same map is still said ({string.Join(" | ", notices)})");

    // CS2's own: on a Workshop map loaded from an official one, CS2 saved Arms Race; a changelevel from it to an official map brings that back,
    // a second before the old map ends (gg_vinceville) - or under the official map, after its start (R3).
    (cv, block, notices, logged, plan) = Server([("ar_baggage", true), ("gg_vinceville", false)], (1, 0));
    Check(plan.Saved == (1, 0) && plan.CanBringBack == (1, 0), "word: CS2 saved Arms Race on the way to the Workshop map, and brings it back with a change to an official map");
    plan.Tell(Modes.Wingman);
    block.Hold("gg_vinceville", null, plan.Told, plan.CanBringBack);
    (cv.Values["game_type"], cv.Values["game_mode"]) = ("1", "0"); // changelevel de_inferno: CS2 brings back what it saved
    block.Hold("gg_vinceville", null, plan.Told, plan.CanBringBack);
    Check(ModeOf(cv) == "0/2" && notices.Count == 0 && logged.Lines.Any(l => l.Contains("put back on gg_vinceville: game_mode 0 -> 2 - CS2 brought back the mode it saved before the Workshop maps")),
        $"word: CS2's values a second before the old map ends: put back, logged, not said ({string.Join(" | ", notices)})");
    plan.Started("de_inferno", true, 1, 0); // R3: they land after the start instead
    (cv.Values["game_type"], cv.Values["game_mode"]) = ("1", "0");
    block.Hold("de_inferno", null, plan.Told, plan.CanBringBack);
    Check(plan.Restored && plan.CanBringBack == (1, 0) && ModeOf(cv) == "0/2" && notices.Count == 0, "word: ...and under the official map, after its start: not said either");
    plan.Started("de_inferno", true, 0, 2); // the second load: official to official
    Check(plan.CanBringBack == null && plan.Saved == (1, 0), "word: after the second load nothing more is brought back; what CS2 saved stays until the next official map to a Workshop map");
    (cv.Values["game_type"], cv.Values["game_mode"]) = ("1", "0"); // now a map's own, with no map change and no os_mode
    block.Hold("de_inferno", null, plan.Told, plan.CanBringBack);
    Check(ModeOf(cv) == "0/2" && notices.SequenceEqual([modeNotice]), $"word: the same values set by a map, with no os_mode and nothing CS2 brings back: said ({string.Join(" | ", notices)})");

    // A map's own attempt on a Workshop map, told Wingman there: its values are not what CS2 saved (Arms Race's), so it is said.
    (cv, block, notices, logged, plan) = Server([("ar_baggage", true), ("aim_deagle", false)], (1, 0));
    plan.Tell(Modes.Wingman);
    block.Hold("aim_deagle", null, plan.Told, plan.CanBringBack);
    Check(ModeOf(cv) == "0/2" && notices.Count == 0, "word: os_mode wingman on a Workshop map (live on aim_deagle): game_type 1 -> 0, game_mode 0 -> 2, not said");
    cv.Values["game_type"] = "1"; // the map's own: Deathmatch's pair, not the saved Arms Race
    block.Hold("aim_deagle", null, plan.Told, plan.CanBringBack);
    cv.Values["sv_infinite_ammo"] = "1";
    block.Hold("aim_deagle", null, plan.Told, plan.CanBringBack);
    Check(ModeOf(cv) == "0/2" && notices.SequenceEqual([modeNotice, "This map tried to change the ammo - Undone: the server keeps its own rules."]),
        $"word: a map's own change of the mode there is said, and the rest of the list as ever ({string.Join(" | ", notices)})");

    // What CS2 saved goes with the plan to the next instance, and an older plan's text (without it) is none.
    var withSaved = ModePlan.Parse(plan.Save());
    Check(withSaved is { Saved: (1, 0) } && withSaved.CanBringBack == (1, 0) && ModePlan.Parse("aim_deagle 0 0 - wingman armsrace armsrace") == null,
        $"word: what CS2 saved is handed over with the plan; a plan's text from before it is none ({plan.Save()})");
    Directory.Delete(dir, true);
}

// ---- the fun page's switches (FunSwitches.cs): a player's change and who made it, the effects that are no convar - kept like convars - what the
// block leaves to a player, and the undo, which puts back what a convar was before the first change on this map, a map's own value included ----
{
    var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sv_gravity"] = "800", ["sv_jump_impulse"] = "301.993",
        ["sv_airaccelerate"] = "20", // this map's own: fs_highsky runs 20, the server 12
        ["sv_enablebunnyhopping"] = "0", ["sv_autobunnyhopping"] = "0", ["sv_staminajumpcost"] = "0.08", ["sv_staminalandcost"] = "0.05", ["sv_staminamax"] = "80",
        ["weapon_accuracy_nospread"] = "0", ["weapon_air_spread_scale"] = "1",
    };
    var refuse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var switches = new FunSwitches(name => live.GetValueOrDefault(name), (name, value) => { if (refuse.Contains(name)) return false; live[name] = value; return true; });
    var told = new List<string>();
    switches.EffectChanged += (effect, on) => told.Add($"{effect} {(on ? "on" : "off")}");
    var at = new DateTime(2026, 11, 7, 21, 4, 11);
    static Fun.Setting S(string id) => Fun.Find(id)!;
    static Fun.Choice C(string id, string choice) => Fun.Find(S(id), choice)!;

    Check(switches.EffectOn(Fun.Drops) && switches.EffectOn(Fun.CatchUp) && !switches.EffectOn(Fun.Vampire) && !switches.EffectOn(Fun.Ghost) && !switches.EffectOn(Fun.NoRecoil),
        "fun: the grenade drops and catch-up start on, Vampire, Ghost and no recoil off");
    var line = switches.Apply(S("gravity"), C("gravity", "moon"), "Remy", at);
    Check(line == "gravity moon by Remy: sv_gravity 200" && live["sv_gravity"] == "200" && switches.SetBy("gravity") == "Remy" && switches.Chosen("sv_gravity")
        && switches.Before["sv_gravity"] == "800" && switches.Last == "21:04:11 gravity moon by Remy: sv_gravity 200",
        $"fun: a change is made, who made it kept, the value from before remembered ({line})");
    switches.Apply(S("bhop"), C("bhop", "turbo"), "Cara", at);
    Check(live["sv_airaccelerate"] == "100" && live["sv_enablebunnyhopping"] == "1" && live["sv_staminamax"] == "0", "fun: Turbo bunny hopping sets its five convars and the air acceleration");
    switches.Apply(S("vampire"), C("vampire", "on"), "Remy", at);
    switches.Apply(S("drops"), C("drops", "off"), "Bob", at);
    Check(told.SequenceEqual([$"{Fun.Vampire} on", $"{Fun.Drops} off"]) && switches.EffectOn(Fun.Vampire) && !switches.EffectOn(Fun.Drops) && switches.SetBy("drops") == "Bob",
        $"fun: an effect is kept like a convar, and its change told once ({string.Join(", ", told)})");
    switches.Apply(S("aim"), C("aim", "on"), "Dan", at);
    var onTheList = new[] { new BlockList.Entry("weapon_accuracy_nospread", "0", "no spread"), new BlockList.Entry("weapon_air_spread_scale", "1", "accurate jumping") };
    Check(BlockList.Due(onTheList, switches.Value, _ => null, switches.Chosen).Count == 0 && switches.EffectOn(Fun.NoRecoil) && live["weapon_accuracy_nospread"] == "1",
        "fun: Laser guns' blocked convars hold a player's choice, so the block leaves them alone; its no-recoil half is an effect");
    refuse.Add("sv_jump_impulse");
    var no = switches.Apply(S("jump"), C("jump", "high"), "Eve", at);
    Check(no.EndsWith("; refused sv_jump_impulse") && switches.SetBy("jump") == null && live["sv_jump_impulse"] == "301.993"
        && !switches.Chosen("sv_jump_impulse") && !switches.Before.ContainsKey("sv_jump_impulse"),
        $"fun: a change the game refuses names nobody as its setter, and is no player's choice - the block still watches the convar ({no})");
    switches.Apply(S("gravity"), C("gravity", "normal"), "Remy", at);
    Check(live["sv_gravity"] == "800" && switches.SetBy("gravity") == null && !switches.Chosen("sv_gravity"), "fun: Normal puts the value from before back, and nobody set it");
    Check(switches.Describe() == "bhop turbo, aim on, vampire on, drops off" && switches.Showing().StartsWith("gravity normal, jump normal, bhop turbo,"),
        $"fun: what the console says of them ({switches.Describe()})");
    told.Clear();
    var undone = switches.Undo("the end of the map", at);
    Check(live["sv_airaccelerate"] == "20" && live["sv_enablebunnyhopping"] == "0" && live["weapon_accuracy_nospread"] == "0" && live["weapon_air_spread_scale"] == "1",
        "fun: the undo puts every changed convar back as it was before the first change - the map's own air acceleration, 20, not the server's 12");
    Check(!switches.EffectOn(Fun.Vampire) && switches.EffectOn(Fun.Drops) && !switches.EffectOn(Fun.NoRecoil) && told.Count == 3 && Fun.Settings.All(s => switches.SetBy(s.Id) == null)
        && !switches.Chosen("sv_airaccelerate"), $"fun: ...every effect back at its default, each change told, nobody's name left ({string.Join(", ", told)})");
    Check(undone.Contains("sv_airaccelerate -> 20") && undone.Contains($"{Fun.Vampire} -> 0") && switches.Last.StartsWith("21:04:11 the end of the map: undone ") && switches.Undo("again", at).Count == 0,
        $"fun: ...what it undid, said, and nothing a second time ({string.Join(", ", undone)})");
}

// ---- a reload ends every fun change (FunHandover.cs, GameSettings.Unload and Ready): the instance that goes knows what each convar was before and
// undoes them all - Vampire's health and the rest of the effects with them - and hands the next instance word of it; that one tells everybody, so
// nobody takes a blocked choice put back for the map's doing ----
{
    var t = new DateTime(2026, 11, 7, 20, 0, 0, DateTimeKind.Utc);
    FunHandover.Put("gg_glasspire", t, ["sv_gravity -> 800", $"{Fun.Vampire} -> 0"]);
    var heard = FunHandover.Take("gg_glasspire", t.AddSeconds(2));
    Check(heard is ["sv_gravity -> 800", var v] && v == $"{Fun.Vampire} -> 0" && FunHandover.Take("gg_glasspire", t.AddSeconds(3)) == null,
        "reload: the next instance on the same map hears what the one before undid, once");
    FunHandover.Put("gg_glasspire", t, ["sv_gravity -> 800"]);
    Check(FunHandover.Take("de_dust2", t.AddSeconds(2)) == null && FunHandover.Take("gg_glasspire", t.AddSeconds(3)) == null,
        "reload: ...on another map it says nothing - a map change puts every fun setting back anyway - and the word is gone");
    FunHandover.Put("gg_glasspire", t, ["sv_gravity -> 800"]);
    Check(FunHandover.Take("gg_glasspire", t + FunHandover.Fresh + TimeSpan.FromSeconds(1)) == null, $"reload: ...nor a load more than {FunHandover.Fresh.TotalMinutes:0} minutes later");
    Check(M.Plain(Fun.DefaultLine(FunHandover.Who)) == "A plugin reload put every fun setting back to normal" && Messages.FunDefault is { Surface: Surface.Top, On: Who.Everybody, Chat: Who.Everybody },
        "reload: the message is Default's, on the top line and in chat for everybody, naming the reload as who put them back");
    var plugin = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OverStrike2");
    var settingsCs = Path.Combine(plugin, "GameSettings.cs");
    var hostCs = Path.Combine(plugin, "OverStrike2.cs");
    Check(File.Exists(settingsCs) && File.Exists(hostCs), "reload: the settings module and the host are read from their source");
    if (File.Exists(settingsCs) && File.Exists(hostCs))
    {
        var code = File.ReadAllText(settingsCs);
        var unload = code[code.IndexOf("public void Unload()", StringComparison.Ordinal)..];
        unload = unload[..unload.IndexOf("\n    }\n", StringComparison.Ordinal)];
        var host = File.ReadAllText(hostCs);
        var hostUnload = host[host.IndexOf("public override void Unload(", StringComparison.Ordinal)..];
        hostUnload = hostUnload[..hostUnload.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Check(unload.IndexOf("Ending = true;", StringComparison.Ordinal) is >= 0 and var ending && ending < unload.IndexOf("fun.Undo(", StringComparison.Ordinal)
            && unload.IndexOf("fun.Undo(", StringComparison.Ordinal) < unload.IndexOf("FunHandover.Put(", StringComparison.Ordinal)
            && code.Contains("undoneByReload = FunHandover.Take(map, ", StringComparison.Ordinal),
            "reload: the unload undoes every change, handing nothing out, before it hands word on; the load takes the word");
        Check(hostUnload.IndexOf("settings?.Unload();", StringComparison.Ordinal) is >= 0 and var first && first < hostUnload.IndexOf("effects?.Unload();", StringComparison.Ordinal)
            && hostUnload.IndexOf("effects?.Unload();", StringComparison.Ordinal) < hostUnload.IndexOf("cloak?.Unload();", StringComparison.Ordinal)
            && host.Contains("else effects?.Switched(effect, on);", StringComparison.Ordinal),
            "reload: the plugin's unload starts with the settings module's undo, whose Vampire switch the effects then cut at once, before the cloak lets go");
    }
}

// ---- a map turns cheats on (GameChangerBlock.cs, CheatsRevert.cs): the block turns them off, and the engine reverts every cheat-flagged setting with
// it - sv_infinite_ammo to 0, under Arms Race's 2. The block puts those back as part of that one undo, so the players read one notice, naming cheats,
// and a later change of the map's own is still said; a player's cheat-flagged fun choice comes back as they chose it. Live on ar_shoots, RCON sv_cheats 1
// put cheats back and then "This map tried to change the ammo", which replaced the notice of cheats within a second ----
{
    var dir = Path.Combine(Path.GetTempPath(), $"os2-block-selftest-{Environment.ProcessId}");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, Modes.BlockFile), "sv_cheats  *  0  0  // cheats\nsv_infinite_ammo  *  2  0  // the ammo\nmp_damage_headshot_only  *  0  0  // headshots only\n");
    var t = new DateTime(2026, 11, 7, 3, 30, 5, DateTimeKind.Utc);
    (FakeConVars Engine, FunSwitches Fun, GameChangerBlock Block, List<string> Notices, Func<DateTime, DateTime> At) Server(bool revertLater)
    {
        var cvars = new FakeConVars { RevertLater = revertLater };
        foreach (var (name, value) in new[] { ("sv_cheats", "false"), ("sv_infinite_ammo", "2"), ("mp_damage_headshot_only", "false") }) cvars.Values[name] = cvars.Baselines[name] = value;
        cvars.CheatDefaults["sv_infinite_ammo"] = "0"; // cheat-flagged, the engine's own default 0; Arms Race's cfg sets 2
        var notices = new List<string>();
        var now = t;
        var funs = new FunSwitches(cvars.Value, cvars.Write);
        var block = new GameChangerBlock(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cvars, funs, notices.Add, () => now);
        block.Read(Modes.ArmsRace);
        return (cvars, funs, block, notices, at => now = at);
    }
    const string Cheats = "This map tried to change cheats - Undone: the server keeps its own rules.";

    var (cvars, _, block, notices, at) = Server(revertLater: false);
    cvars.Values["sv_cheats"] = "true"; // RCON sv_cheats 1, as a map's point_servercommand would
    block.Hold("ar_shoots", null, Modes.ArmsRace);
    Check(cvars.Values["sv_cheats"] == "false" && cvars.Values["sv_infinite_ammo"] == "2" && notices.SequenceEqual([Cheats]),
        $"cheats: turned off, the ammo the engine reverted with them put back in the same look, and one notice, naming cheats ({string.Join(" | ", notices)})");
    at(t.AddSeconds(1));
    block.Hold("ar_shoots", null, Modes.ArmsRace);
    Check(notices.Count == 1, "cheats: ...and the next look has nothing to say");

    (cvars, _, block, notices, at) = Server(revertLater: true);
    cvars.Values["sv_cheats"] = "true";
    block.Hold("ar_shoots", null, Modes.ArmsRace);
    cvars.Frame(); // the engine's revert lands after the block's write
    Check(cvars.Values["sv_infinite_ammo"] == "0", "cheats: (the fake engine's revert, a frame after the write)");
    at(t.AddSeconds(1));
    block.Hold("ar_shoots", null, Modes.ArmsRace);
    Check(cvars.Values["sv_infinite_ammo"] == "2" && notices.SequenceEqual([Cheats]),
        $"cheats: a revert that lands after the write is put back by the next look, part of the same undo - still one notice, naming cheats ({string.Join(" | ", notices)})");
    at(t.AddSeconds(10));
    cvars.Values["sv_infinite_ammo"] = "1"; // the map's own change, later
    block.Hold("ar_shoots", null, Modes.ArmsRace);
    Check(cvars.Values["sv_infinite_ammo"] == "2" && notices.Count == 2 && notices[1] == "This map tried to change the ammo - Undone: the server keeps its own rules.",
        "cheats: a change of the map's own after the revert is still undone and said: the revert counted as no try of the map's");

    var (cvars2, funs2, block2, notices2, _) = Server(revertLater: false);
    funs2.Apply(Fun.Find("ammo")!, Fun.Find(Fun.Find("ammo")!, "on")!, "Remy", t); // Infinite ammo on: sv_infinite_ammo 1, a player's choice
    cvars2.Values["sv_cheats"] = "true";
    block2.Hold("ar_shoots", null, Modes.ArmsRace);
    Check(cvars2.Values["sv_cheats"] == "false" && cvars2.Values["sv_infinite_ammo"] == "1" && funs2.Chosen("sv_infinite_ammo") && notices2.SequenceEqual([Cheats]),
        $"cheats: a player's Infinite ammo, which the engine's revert took, comes back as they chose it ({cvars2.Values["sv_infinite_ammo"]}; {string.Join(" | ", notices2)})");

    // A value the game will not take (live: Wingman's mp_death_drop_grenade 3, kept at 2): said once as refused, not tried again on that map, and no
    // "again - 1 times" line after it, as there was.
    var refusedLog = new ListLogger();
    var clamped = new FakeConVars();
    (clamped.Values["mp_death_drop_grenade"], clamped.Baselines["mp_death_drop_grenade"], clamped.Max["mp_death_drop_grenade"]) = ("2", "2", 2);
    var wingDir = Path.Combine(dir, "refused");
    Directory.CreateDirectory(wingDir);
    File.WriteAllText(Path.Combine(wingDir, Modes.BlockFile), "mp_death_drop_grenade  *  0  3  // the grenade drops\n");
    var refusedNotices = new List<string>();
    var wingNow = t;
    var wing = new GameChangerBlock(wingDir, refusedLog, clamped, new FunSwitches(clamped.Value, clamped.Write), refusedNotices.Add, () => wingNow);
    wing.Read(Modes.Wingman);
    wing.Hold("de_inferno", null, Modes.Wingman);
    wingNow = t.AddSeconds(61);
    wing.Hold("de_inferno", null, Modes.Wingman);
    Check(refusedLog.Lines.Count(l => l.Contains("could not block mp_death_drop_grenade on de_inferno: 2 -> 3 refused, reads 2")) == 1 && !refusedLog.Lines.Any(l => l.Contains("again"))
          && refusedNotices.Count == 0 && clamped.Values["mp_death_drop_grenade"] == "2",
        $"block: a value the game refuses is said once and tried no more on that map, with no line that it was blocked again ({string.Join(" | ", refusedLog.Lines)})");

    var later = new CheatsRevert();
    later.CheatsOff(t, []);
    Check(later.Covers("sv_infinite_ammo", true, t.AddSeconds(2)) && !later.Covers("sv_infinite_ammo", true, t + CheatsRevert.Window + TimeSpan.FromSeconds(1))
          && !later.Covers("mp_damage_headshot_only", false, t.AddSeconds(1)) && !later.Covers("sv_cheats", true, t.AddSeconds(1)) && !new CheatsRevert().Open(t),
        "cheats: only cheat-flagged settings, only within the revert's window after the block turned cheats off");
    Directory.Delete(dir, true);
}

// ---- catch-up's card or CS2's centre panel (HudVote.CatchUpCard, HudScreen.HasCards, the plugin's Show): a game the window's X has not been clicked in
// yet takes the card, as the ballot takes it - live, a v4.7.0 game 14 s after its join got the centre panel, and no chat line. The centre panel is for a
// game known to hold a content before v4.6.0, and for one that may hold no HUD (a joiner while Steam does not hand the item out, not seen clicking); the
// chat line comes either way ----
{
    var now = HudVersion.OfLevel(HudVersion.Level);
    Check(HudVote.CatchUpCard("", false) && HudVote.CatchUpCard(HudVersion.Unknown, false) && HudVote.CatchUpCard(now, false) && HudVote.CatchUpCard(HudVersion.OfLevel(HudVote.CardsLevel), false),
        $"catch-up: the card for a game not seen clicking yet, one whose click named no content, this content ({now}) and the first with the card");
    Check(!HudVote.CatchUpCard(HudVersion.OfLevel(HudVote.CardsLevel - 1), false) && !HudVote.CatchUpCard(HudVersion.Older, false),
        "catch-up: CS2's centre panel for a game known to hold a content before v4.6.0 (its click said which, or that it is older)");
    Check(!HudVote.CatchUpCard("", true) && HudVote.CatchUpCard(now, true) && HudVote.CatchUpCard(HudVersion.Unknown, true),
        "catch-up: ...and for a joiner while Steam does not hand the item out who has not clicked in it - one who has, has a HUD");
    Check(HudVote.CardsFor("") == MapVote.PageCards && HudVote.CatchUpCard("", false), "catch-up: a game not seen yet takes the card as the ballot takes its ten cards");
    string[] boost7 = ["+95% health", "-35% damage taken", "+35% damage", "25% lifesteal", "Regeneration", "A second chance", "+3 s spawn protection", "Infinite ammo", "One-hit knife and Zeus"];
    var rows7 = HudFrame.CatchUpRowsOf(boost7);
    Check(rows7.Length == HudFrame.CatchUpRows && string.Join("  ·  ", rows7) == string.Join("  ·  ", boost7) && rows7[^1] == "Infinite ammo  ·  One-hit knife and Zeus" && rows7.All(r => r.Length <= 50),
        $"catch-up: a 7+ boost's nine items on the card's six rows, the last ones two to a row - its infinite ammo and one-hit knife no longer cut off ({string.Join(" | ", rows7)})");
    string[] boost5 = ["+50% health", "-25% damage taken", "+20% damage", "10% lifesteal", "Regeneration", "A second chance", "+2 s spawn protection"];
    Check(HudFrame.CatchUpRowsOf(boost5) is { Length: 6 } rows5 && rows5[^1] == "A second chance  ·  +2 s spawn protection" && HudFrame.CatchUpRowsOf(boost5[..3]).SequenceEqual(boost5[..3])
          && HudFrame.CatchUpRowsOf(["+20% damage taken (a bounty)"]).Length == 1, "catch-up: a 5+ boost's seven items, its last two on one row; three items, or the bounty's one, a row each");
    var plugin = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OverStrike2");
    var hostCs = Path.Combine(plugin, "OverStrike2.cs");
    var messagesCs = Path.Combine(plugin, "HudScreenMessages.cs");
    Check(File.Exists(hostCs) && File.Exists(messagesCs), "catch-up: the host and the HUD's messages are read from their source");
    if (File.Exists(hostCs) && File.Exists(messagesCs))
    {
        var host = File.ReadAllText(hostCs);
        var show = host[host.IndexOf("catchUp.Show = ", StringComparison.Ordinal)..];
        show = show[..show.IndexOf("};", StringComparison.Ordinal)];
        var messages = File.ReadAllText(messagesCs);
        Check(show.IndexOf("hud.ShowCatchUp(", StringComparison.Ordinal) is >= 0 and var chat && chat < show.IndexOf("if (card) return;", StringComparison.Ordinal)
              && show.IndexOf("PrintToCenterHtml", StringComparison.Ordinal) > show.IndexOf("if (card) return;", StringComparison.Ordinal)
              && messages.Contains("HudVote.CatchUpCard(VersionOf(Account(p)), !addonRequired && !keptMenu.Contains(Account(p)))", StringComparison.Ordinal)
              && messages.Contains("if (card) Player(player.Slot).CatchUp = ", StringComparison.Ordinal),
            "catch-up: the message posts its chat line before it picks the card or the centre panel, and the card is drawn only for a game that shows it");
    }
}

// ---- the Players page's time on (JoinTimes.cs): by account. A map change disconnects everybody, and a player's time goes on through it whatever
// slot they come back in; kept by slot, a player who took a slot somebody else left showed that player's time until their own real join ----
{
    var t0 = new DateTime(2026, 11, 6, 20, 0, 0, DateTimeKind.Utc);
    var joins = new JoinTimes();
    joins.Joined(1001, t0); // a real join (css_guide)
    Check(joins.Since(1001, t0.AddMinutes(30)) == TimeSpan.FromMinutes(30), "time on: a player's time goes on through a map change's reconnect, with nothing said at it");
    Check(joins.Since(1002, t0.AddMinutes(31)) == null, "time on: a player in a slot somebody else left shows none of theirs before their own real join");
    joins.Joined(1002, t0.AddMinutes(32));
    Check(joins.Since(1002, t0.AddMinutes(40)) == TimeSpan.FromMinutes(8) && joins.Since(1001, t0.AddMinutes(40)) == TimeSpan.FromMinutes(40),
        "time on: ...which starts their own, and the player before keeps theirs");
    joins.Joined(1001, t0.AddMinutes(45));
    Check(joins.Since(1001, t0.AddMinutes(46)) == TimeSpan.FromMinutes(1), "time on: a real join again starts over");
    joins.Joined(-1, t0);
    Check(joins.Since(-1, t0.AddMinutes(1)) == null, "time on: a player whose account the game has not told has none");
    var plugin = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OverStrike2");
    var screen = Directory.Exists(plugin) ? Directory.GetFiles(plugin, "HudScreen*.cs").Order(StringComparer.Ordinal).ToList() : [];
    if (screen.Count > 0)
    {
        var code = string.Join("\n", screen.Select(File.ReadAllText)); // the class, in all its files
        Check(code.Contains("joins.Joined(Account(player), ", StringComparison.Ordinal) && Regex.Matches(code, @"joins\.Since\(account, now\)").Count == 1,
            "time on: the HUD takes it by account at css_guide, and hands it so to the frame, whose join banner and Players page read it (the bridge's --selftest runs the frame)");
    }
    else Console.WriteLine("(no HudScreen*.cs beside this project: where the HUD's time on comes from is not checked)");
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

// ---- The Steam Web API's one door (SteamWebApi.cs): three callers at once go one at a time, 2 s apart, each saying who asks ----
{
    var seen = new System.Collections.Concurrent.ConcurrentQueue<(DateTime Start, DateTime End)>();
    var agents = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var inFlight = 0;
    var most = 0;
    OverStrike2.SteamWebApi.UserAgent = OverStrike2.SteamWebApi.AgentOf("9.9.9");
    using var http = new HttpClient(new Recorder(seen, agents, () => { var n = Interlocked.Increment(ref inFlight); most = Math.Max(most, n); }, () => Interlocked.Decrement(ref inFlight)));
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0"); // a caller's own default never speaks for the door
    var calls = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
    {
        using var r = await OverStrike2.SteamWebApi.PostAsync(http, "https://api.steampowered.com/test", new StringContent("x")).ConfigureAwait(false);
    })).ToArray();
    Task.WaitAll(calls);
    var times = seen.OrderBy(t => t.Start).ToList();
    var gaps = times.Zip(times.Skip(1), (a, b) => (b.Start - a.End).TotalSeconds).ToList();
    Check(times.Count == 3 && most == 1, $"the Web API's door: three callers at once, one request at a time (at most {most} in flight)");
    Check(gaps.All(g => g >= OverStrike2.SteamWebApi.Gap.TotalSeconds - 0.05), $"...each at least {OverStrike2.SteamWebApi.Gap.TotalSeconds:0} s after the last ended ({string.Join(", ", gaps.Select(g => g.ToString("0.00")))} s)");
    Check(agents.Count == 3 && agents.All(a => a == "OverStrike2/9.9.9 (the Over Strike 2 CS2 server plugin; plugin/OverStrike2)"),
        $"...each with an honest User-Agent naming the project, never a browser's ({string.Join(" | ", agents.Distinct())})");
}

Console.WriteLine(failures == 0
    ? $"Over Strike 2 plugin self-test: {checks} checks passed."
    : $"Over Strike 2 plugin self-test: {failures} of {checks} checks FAILED.");
return failures == 0 ? 0 : 1;

/// <summary>The game convars as the engine keeps them, for the block's checks: values, the baseline, which are cheat-flagged and their own defaults.
/// sv_cheats going off reverts every cheat-flagged one to its default, with the write or - RevertLater - at the next Frame().</summary>
sealed class FakeConVars : IConVars
{
    public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Baselines = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> CheatDefaults = new(StringComparer.OrdinalIgnoreCase);
    public bool RevertLater;
    public readonly Dictionary<string, int> Max = new(StringComparer.OrdinalIgnoreCase); // the highest value the engine takes: more reads back as this
    bool revertDue;

    public bool Pending => false;
    public string? Value(string name) => Values.GetValueOrDefault(name);
    public string? Baseline(string name) => Baselines.GetValueOrDefault(name);
    public bool Known(string name) => Values.ContainsKey(name);
    public bool Cheat(string name) => CheatDefaults.ContainsKey(name);

    public bool Write(string name, string value)
    {
        var cheatsOff = name.Equals("sv_cheats", StringComparison.OrdinalIgnoreCase) && Fun.Same(Values.GetValueOrDefault(name), "1") && !Fun.Same(value, "1");
        Values[name] = Max.TryGetValue(name, out var max) && int.TryParse(value, out var n) && n > max ? max.ToString(System.Globalization.CultureInfo.InvariantCulture) : value;
        if (cheatsOff) { if (RevertLater) revertDue = true; else Revert(); }
        return Values[name] == value;
    }

    public void Frame()
    {
        if (!revertDue) return;
        revertDue = false;
        Revert();
    }

    void Revert() { foreach (var (name, value) in CheatDefaults) Values[name] = value; }
}

/// <summary>CS2's map commands as measured (../bridge/ModeSwitch.cs, SyncMode): a host_workshop_map from an official map saves the convars as they are
/// then, and a changelevel (or map) from a Workshop map to an official one brings the saved ones back, game_type and game_mode with them; nothing else
/// saves or brings back anything. It starts as the server does, in Arms Race on an official map.</summary>
sealed class FakeCs2
{
    public readonly FakeConVars ConVars = new();
    bool onOfficial = true;
    (string Type, string Mode)? saved;

    public FakeCs2() { ConVars.Values["game_type"] = "1"; ConVars.Values["game_mode"] = "0"; }

    public int GameType => int.Parse(ConVars.Values["game_type"]);
    public int GameMode => int.Parse(ConVars.Values["game_mode"]);

    /// <summary>The game_type/game_mode CS2 holds to bring back, or null.</summary>
    public string? Saved => saved is { } s ? $"{s.Type}/{s.Mode}" : null;

    public void Load(string command, string map, bool official)
    {
        if (command == "host_workshop_map" && onOfficial) saved = (ConVars.Values["game_type"], ConVars.Values["game_mode"]);
        if (command is "changelevel" or "map" && official && !onOfficial && saved is { } s) (ConVars.Values["game_type"], ConVars.Values["game_mode"]) = s;
        onOfficial = official;
    }

    /// <summary>A console line such as the second load's: its convars set in order, then its map command. Returns that command.</summary>
    public string Run(string line, bool official)
    {
        foreach (var part in line.Split(';'))
        {
            var words = part.Trim().Split(' ', 2);
            if (words.Length < 2) continue;
            if (words[0] is "changelevel" or "map" or "host_workshop_map") { Load(words[0], words[1], official); return words[0]; }
            ConVars.Values[words[0]] = words[1];
        }
        return "";
    }
}

/// <summary>A logger that keeps every line it is given, for the checks on what a module said.</summary>
sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
{
    public readonly List<string> Lines = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add($"{level}: {formatter(state, exception)}");
}

/// <summary>A Web API that answers at once, noting when each request started and ended, and who it said was asking.</summary>
sealed class Recorder(System.Collections.Concurrent.ConcurrentQueue<(DateTime, DateTime)> seen, System.Collections.Concurrent.ConcurrentQueue<string> agents, Action enter, Action leave) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        enter();
        agents.Enqueue(request.Headers.UserAgent.ToString());
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
