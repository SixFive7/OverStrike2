// Why a map is broken (the owner, 2026-11-01, #8 and #11): every broken map says why, on its HUD card, in the confirm before anyone loads
// it, and on the site - "may restart the server" only where that is true. broken.json's entries carry a short "reason" for the maps that
// break something else (adverts, a player's HUD); a crasher without one keeps "crashed N of N test loads and may restart the server".
//
// And the maps that crash the players' games rather than the server (#6): 3 or more players timing out within 60 s of entering a map,
// while the server keeps answering (their disconnects reach the log, so it is up), mark that map broken by itself - kept in state.json
// with its reason, never rolled, a pick asks to confirm with that reason.
// #2167 on 2026-10-29 is the case: all six players timed out about 20 s after entering it, the server stayed
// up, and the server-crash counter never saw it. The map that is on when it happens makes way for the dice, since every rejoin would
// load it again. The mark is data: deleting the entry from state.json (bridge stopped) or a fix in its Workshop item lifts nothing by
// itself; broken.json is where a map is listed for good.
using System.Text.Json;

static partial class Program
{
    /// <summary>Crash key -> the reason broken.json gives (empty: none given).</summary>
    static Dictionary<string, string> BrokenReasons = new();

    const int PlayerCrashPlayers = 3;           // this many players...
    const double PlayerCrashSeconds = 60;       // ...timing out within this long of entering the map
    const int ReasonMax = 45;                   // what a reason may take: the note's 140 less a 34-character label and the words around it (the card shows ~38)

    /// <summary>Crash key -> reason, out of broken.json's "maps" (or "broken") array: an object's "reason", keyed like ParseBroken.</summary>
    static Dictionary<string, string> ParseReasons(string json)
    {
        var reasons = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("maps", out var m) ? m
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("broken", out var b) ? b : default;
        if (list.ValueKind != JsonValueKind.Array) return reasons;
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("reason", out var r) || r.ValueKind != JsonValueKind.String) continue;
            var key = e.TryGetProperty("id", out var i) && i.ValueKind is JsonValueKind.Number or JsonValueKind.String ? JsonText(i)
                : e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? JsonText(n) : "";
            var why = Hud.Clean(r.GetString() ?? "", ReasonMax).Trim();
            if (key != "" && why != "") reasons[key] = why;
        }
        return reasons;
    }

    /// <summary>What the card, the confirm and the site say about a broken map: the player-crash mark, broken.json's reason, or the crash
    /// count; empty when there is nothing to say beyond "on the broken list".</summary>
    static string ReasonOf(string key) =>
        S.PlayerCrashes.TryGetValue(key, out var auto) ? auto : BrokenReasons.GetValueOrDefault(key) ?? "";

    // ---- #6: maps that crash the players' games ----

    static readonly Dictionary<string, DateTime> enteredOnThisMap = new(); // SteamID -> when they entered the game on the map that is on
    static readonly HashSet<string> timedOutOnThisMap = new();             // SteamIDs that timed out within the window on this map

    /// <summary>A disconnect reason that says the client stopped answering (NETWORK_DISCONNECT_TIMEDOUT and the like).</summary>
    static bool IsTimeout(string reason) =>
        reason.Contains("TIMEDOUT", StringComparison.OrdinalIgnoreCase) || reason.Contains("TIMED_OUT", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase);

    static void PlayerCrashMapStarted() { enteredOnThisMap.Clear(); timedOutOnThisMap.Clear(); }

    static void PlayerEnteredMap(string steamId, DateTime now) => enteredOnThisMap[steamId] = now;

    /// <summary>A disconnect: one that timed out within PlayerCrashSeconds of entering this map counts; the third such player marks the map.</summary>
    static void PlayerLeftMap(string steamId, string reason, DateTime now)
    {
        if (!IsTimeout(reason) || !enteredOnThisMap.TryGetValue(steamId, out var at) || (now - at).TotalSeconds > PlayerCrashSeconds) return;
        if (!timedOutOnThisMap.Add(steamId) || timedOutOnThisMap.Count < PlayerCrashPlayers || S.Map is not { } on) return;
        var key = Key(on);
        if (S.PlayerCrashes.ContainsKey(key) || Broken.ContainsKey(key)) return; // known already
        var why = $"crashed {timedOutOnThisMap.Count} players' games within {PlayerCrashSeconds:0} s";
        S.PlayerCrashes[key] = Hud.Clean(why, ReasonMax);
        Save();
        CatalogVersion++;
        Log($"player crashes: {Label(on)} ({key}) at {now.ToLocalTime():yyyy-MM-dd HH:mm} - {timedOutOnThisMap.Count} players timed out within {PlayerCrashSeconds:0} s of entering it while the server kept answering - marked broken (state.json playerCrashes)");
        // Everybody who rejoins would load it again: the dice, as for a map that has just gone from the Workshop.
        if (Pending == null && !AnnouncePending && RandomPick(true, 1) is { } map)
            StartChange(new Change(map, "the dice", []), $"{Clip(Label(on), 40)} {why} - it is marked broken. Rolling the dice: {Label(map)}{Detail(map)}{RollNote}");
        else Announce($"{Clip(Label(on), 40)} {why} - it is marked broken; pick another map");
    }
}
