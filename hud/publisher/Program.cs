// Uploads the gungame_hud addon as a content update of the one HUD Workshop item for Counter-Strike 2 (app 730), through the Steam
// client that is already running and logged in on this PC - it never sees a password: SteamAPI_Init attaches to that client.
// It never creates an item, and it changes nothing else about one: no visibility, no deletion, no subscription (AGENTS.md, "cs2 in-game
// HUD - NEVER create another Workshop item").
//
// What the CS2 Workshop Manager uploads, and so what this uploads (read off items in steamapps/workshop/content/730/):
//   <id>.vpk           the addon's game folder, packed (VPK 2, data embedded, 1 MB chunk MD5s, the 48-byte checksum block,
//                      a 20-byte empty signature record);
//   publish_data.txt   title, source_folder, publish_time;
//   addoninfo.txt      as in the addon folder.
//
//   publisher pack <addon dir> <out dir> <id>                              build the content folder only (no Steam)
//   publisher publish <addon dir> <id file> <staging dir> <preview.jpg> <change note> --user-approved-update
//                                                                          a content update of the item <id file> names - nothing else.
//                                                                          REFUSED without the flag (every update waits for Valve's
//                                                                          moderation, and only the user says go), and REFUSED, flag or
//                                                                          not, when <id file> names no item: there is no create path
//   publisher public <id>                                                   is it shown to everybody? An anonymous visit to its Workshop
//                                                                          page (the Web API calls an item awaiting moderation public),
//                                                                          with an honest User-Agent; exit 0 only when it is
//   publisher query <id>                                                    the item as its owner's client sees it (reads only)
//
// hud/publish.ps1 runs it with hud/workshop_id.txt, which names the one HUD item (3823993566). An empty, missing or unreadable id file
// is refused before Steam is even asked: creating an item is what the rule forbids, so the code cannot do it. The Steam calls, the session
// and the VPK are the ones the maps' publisher uses too (tools/workshop/Workshop.cs), which declares no create path either; the page's
// verdict is the plugin's (bridge/Shared/SteamPage.cs).
// Needs CS2's own steam_api64.dll beside it (publish.ps1 copies it): its flat API exports SteamUGC_v021.
using System.Text;
using static Workshop;

static class Program
{
    // The one HUD item's title (3823993566): the server's name. An update sets it again: the same item, never a new one.
    const string Title = "Over Strike 2";
    const string Description =
        "Over Strike 2 - Over the top - On the Huis.\n\n" +
        "The in-game menu of the Over Strike 2 server: Arms Race on thousands of Steam Workshop maps, with Deathmatch and Wingman by vote. You do not need to subscribe: the " +
        "server hands this to your game when you join, and it shows nothing anywhere else. Layout and stylesheet only - no scripts.\n\n" +
        "[h1]The menu[/h1]\n" +
        "The menu opens by itself at every join, with a welcome, and stays until you close it with its X. Hold TAB for the live board beside the scoreboard: the map that is on and " +
        "who picked it, Like and Dislike, Start a vote and the latest messages - with the mouse, for as long as TAB is down. TAB + E opens the full menu, which closes by itself when idle.\n" +
        "[list]\n" +
        "[*][b]Maps[/b]: every map with its picture, size, likes and tags that say why it is worth a look; a Wingman map also says how many a side its spawn points suit, 3v3 and " +
        "the like. Search names, tags and Workshop ids by chat: with the map browser open, press Y and type part of a map name; anywhere, !s and a few words. Sort by place, likes, " +
        "name or size; filter by size, your likes, official maps, Our maps or game mode. Type any Workshop id in chat to play a map that is not on the list: the server downloads it " +
        "first, with the progress on its card. While a vote is on, this tab holds its ballot.\n" +
        "[*][b]Fun[/b]: the fun settings, with Random and Default, and Bots: bots for everybody, with presets from a zombie horde to a sniper duel; every change says who made it.\n" +
        "[*][b]Server[/b]: who is on and which HUD they have; the match length and the two restarts, of the match or the whole server - after the confirming click the button counts its " +
        "10 seconds down, and a click on it cancels; what the server's map sync is doing, what waits for it, and its history.\n" +
        "[*][b]Help[/b]: where everything is, and how it works: every rule of the vote and every chance in a map pick.\n" +
        "[/list]\n\n" +
        "[h1]Chat commands[/h1]\n" +
        "Every action has one, and !help lists them: !play 12, !find words, !next, !random, !up, !down, !vote, !fun. The words you know from other servers work too, and the " +
        "answer teaches: You typed !map 12 - Ran !play 12. A Workshop link pasted in chat opens that map's card.\n\n" +
        "[h1]The vote for the next map[/h1]\n" +
        "The vote is how a map is chosen. Play on a card puts that map to the vote, with your vote on it; while a vote is on, it goes on that ballot. Start a vote calls one " +
        "without a map, and a match's end opens one by itself. Every vote opens everybody's window and freezes everybody - nobody moves, shoots or takes damage - until all have " +
        "voted, 60 seconds at the most, and everybody unfreezes at the same moment.\n" +
        "The ballot holds ten cards a page, and pages more when it needs them: Keep playing this map (Play this map again once the match is over), a slot of its own for the map each " +
        "player adds - it never takes another card's place, adding one votes for it, and adding another later swaps it -, a map of each other game mode (Deathmatch and Wingman), " +
        "a fresh Workshop map, a surprise new to the server, and favourites to fill the page; Random is a button of its own. Every card shows its mode, its tags and its votes, and " +
        "every card but a player's own pick is on the server's disk, so the winner loads at once.\n" +
        "Click a card to vote, another to change it, or type !vote and the number on its picture - the vote's own, the same for everybody; once you have voted your vote " +
        "stays, and moves only when you vote for another card. The ballot stands beside the chat, so you see what you type and every reply, and one clock says when it " +
        "ends. Your own card comes first in your view, " +
        "with a white YOUR VOTE badge; the others sort by their votes as they come in, and you see who still has to vote. A confirmed Previous or risky map counts 10 seconds " +
        "down in its button, and a click cancels it. The most votes win and a tie is drawn; no votes keeps the map mid-match and is Random at a match's end. After a vote, 2 minutes pass before the next can be called. The server is back on Arms Race after a restart " +
        "or 10 minutes with nobody on.\n\n" +
        "[h1]Your likes steer the dice[/h1]\n" +
        "Random maps lean on your likes: each Like doubles a map's odds, up to 5, and each Dislike quarters them. The Workshop's own numbers - rating, players, trend - are worth " +
        "up to 16 times, and a map shown and not chosen steps back for a while. One mark per player per map: the opposite one takes yours back first, and the next one casts it; " +
        "the same one again says you did so already. A Dislike asks why, in a small dialog at the centre while you have the mouse: I did not like the map, too big, ads, too small, broken, " +
        "runs badly. Click one, or type !down big. Only 1 random pick in 100 is a too-big map, and a map marked for ads is never a random pick.\n\n" +
        "[h1]Fun settings[/h1]\n" +
        "Anybody can switch them until the map changes, each with its own word (!gravity low, !vampire on): gravity, super jump, bunny hopping, fall damage, ice floor, Laser " +
        "guns, infinite ammo, headshots only, Vampire, Ghost, big grenades, double damage and free-for-all. Arms Race has five more: friendly fire, Instakill, grenades x10, the " +
        "grenade drops - every player spawns with a grenade, dropped where they die (!drops off) - and catch-up (!catchup off). Random sets them all at once (!fun random), " +
        "Default puts them all back (!fun default).\n\n" +
        "[h1]Bots[/h1]\n" +
        "There are none unless somebody puts them in, from FUN, Bots: how many, on which side, how good, what they may fight with and how they talk - and what no match allows: " +
        "bots that hold their fire, crouch everywhere, stand frozen, copy your every move or leave you alone to fight each other. Presets set several at once: Zombie horde, Sniper " +
        "duel, Target practice, Copycats, Bot war and Pistol party. Like the fun settings they last until the map changes, and say who set them. In Arms Race the bots climb the " +
        "ladder like everybody, and can win.\n\n" +
        "[h1]Catch-up boosts[/h1]\n" +
        "Fall 2 or more kills behind the leader in Arms Race and you spawn with a boost that grows with every kill of the gap, up to 8 and more: more health, lifesteal and " +
        "regeneration; from 3 behind less damage taken and more damage dealt; from 4 a second chance and longer spawn protection; from 7 infinite ammo and a one-hit knife and " +
        "Zeus. A card under the crosshair at spawn says what you got. A leader who runs away takes up to 30% more damage. Catch-up is one of the fun settings: on unless somebody switches " +
        "it off, until the map changes.\n\n" +
        "[h1]Maps that break things[/h1]\n" +
        "A map that crashes the server or players' games, or is listed for advertising, says why on its card and asks for a second click before it goes to the vote; the dice " +
        "never roll it. Items that are not maps and maps gone from the Workshop are refused. What a map changes in the game's settings is put back when it ends, and no map may " +
        "turn on cheats or no spread, hide the radar or switch the mode: you are told on screen.\n\n" +
        "[h1]Our maps[/h1]\n" +
        "gg_glasspire, gg_moonglass and gg_vinceville, generated from code for this server, have their own filter and a tag on their cards.\n\n" +
        "[h1]New maps by themselves[/h1]\n" +
        "Whenever nobody plays, the server finds new Arms Race, Deathmatch and Wingman maps on the Workshop, scans them for adverts, load-tests them on a test copy and lists the " +
        "ones that pass. While the server downloads a map somebody is waiting for, the screen shows the progress.\n\n" +
        "[h1]Your HUD's version[/h1]\n" +
        "The HUD says when yours is older or newer than the server's; older means quit CS2 and start it again, and Steam updates it. Closing the welcome with its X shows the server " +
        "your HUD works, and which version it is.\n\n" +
        "Built on the custom_hud_layout panel CS2 added on 2026-08-24.";
    const string ApprovedFlag = "--user-approved-update";

    static int Main(string[] args)
    {
        try
        {
            // Never another HUD item, and a new version of the one item only with the owner's go, standing or per release (AGENTS.md): every
            // version waits in Valve's moderation queue. Only an explicit, approved run may upload, and only as an update.
            if (args is ["publish", ..] && !args.Contains(ApprovedFlag))
            {
                Console.WriteLine($"REFUSED: publishing uploads a new version of the HUD item, which waits for Valve's moderation. AGENTS.md, " +
                    $"\"cs2 in-game HUD - NEVER create another Workshop item\": only with the owner's go, standing or per release, pass {ApprovedFlag}.");
                return 3;
            }
            args = args.Where(a => a != ApprovedFlag).ToArray();
            return args switch
            {
                ["pack", var addon, var outDir, var id] => Pack(addon, outDir, ulong.Parse(id)),
                ["public", var id] => PublicNow(ulong.Parse(id)),
                ["publish", _, _, _, _] => NoNote(),
                ["publish", var addon, var idFile, var staging, var preview, var note] => Publish(addon, idFile, staging, preview, note),
                ["query", var id] => Query(ulong.Parse(id)),
                _ => Usage(),
            };
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + e); return 1; }
    }

    static int Usage() { Console.WriteLine($"publisher pack <addon dir> <out dir> <id> | publish <addon dir> <id file> <staging dir> <preview.jpg> <change note> {ApprovedFlag} | public <id> | query <id>"); return 2; }

    static int NoNote() { Console.WriteLine("REFUSED: a content update needs a change note - Steam shows it in the item's change notes."); return 3; }

    /// <summary>The item an update goes to: the id the file names, or null with the refusal said. Checked before Steam is touched. There is no
    /// create path: an empty, missing or unreadable id file is refused, because a new HUD item is what AGENTS.md forbids.</summary>
    static ulong? ItemToUpdate(string idFile)
    {
        var text = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : "";
        if (ulong.TryParse(text, out var id) && id != 0) return id;
        Console.WriteLine($"REFUSED: {idFile} names no item ({(File.Exists(idFile) ? $"it holds '{text}'" : "it does not exist")}). The publisher never creates a " +
            "Workshop item - AGENTS.md, \"cs2 in-game HUD - NEVER create another Workshop item\": a release is a content update of the one HUD item, " +
            "3823993566, whose id belongs in that file.");
        return null;
    }

    /// <summary>Whether Steam shows the item to everybody: an anonymous visit to its Workshop page, saying who asks, read as the plugin reads it
    /// (SteamPage.Shown): Steam's error page for a visitor is what everybody but the item's owner gets while the item or its latest version awaits
    /// moderation, and the Web API is no help - it reports such an item as public. 0 = shown to everybody, 1 = not shown, 2 = no verdict (rate
    /// limited, an error, an unexpected page).</summary>
    static int PublicNow(ulong id)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OverStrike2-publisher/1 (the Over Strike 2 HUD's publisher; hud/publisher)");
        using var response = http.GetAsync($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}").GetAwaiter().GetResult();
        var status = (int)response.StatusCode;
        var html = response.IsSuccessStatusCode || status == 500 ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult() : null;
        var title = SteamPage.Title(html);
        switch (SteamPage.Shown(status, html))
        {
            case true:
                Console.WriteLine($"{id}: shown to everybody - '{title}'");
                return 0;
            case false:
                Console.WriteLine($"{id}: NOT shown to everybody - Steam's error page for a visitor, HTTP {status} '{title}' (waiting for moderation, or private, or gone)");
                return 1;
            default:
                Console.WriteLine($"{id}: no verdict - HTTP {status}{(title != "" ? $" '{title}'" : "")} (429 = Steam rate-limits this address; try again later)");
                return 2;
        }
    }

    // ---- the content folder ----

    static int Pack(string addon, string outDir, ulong id)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir)) File.Delete(f);
        var vpk = Path.Combine(outDir, $"{id}.vpk");
        var n = Vpk.Write(addon, vpk, rel => !rel.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)).Count; // the whole addon folder but a VPK lying in it
        var now = DateTimeOffset.Now;
        File.WriteAllText(Path.Combine(outDir, "publish_data.txt"),
            "\"publish_data\"\n{\n" +
            $"\t\"title\"\t\t\"{Title}\"\n" +
            $"\t\"source_folder\"\t\t\"{Path.GetFileName(Path.GetFullPath(addon).TrimEnd('\\', '/'))}\"\n" +
            $"\t\"publish_time\"\t\t\"{now.ToUnixTimeSeconds()}\"\n" +
            $"\t\"publish_time_readable\"\t\t\"{now.ToString("MM/dd/yyyy hh:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture)}\"\n" +
            "}\n", new UTF8Encoding(false));
        var info = Path.Combine(addon, "addoninfo.txt");
        if (File.Exists(info)) File.Copy(info, Path.Combine(outDir, "addoninfo.txt"), true);
        Console.WriteLine($"packed {n} files into {vpk} ({new FileInfo(vpk).Length} bytes); content folder {outDir}: {string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName))}");
        return 0;
    }

    // ---- Steam ----

    static int Publish(string addon, string idFile, string staging, string preview, string note)
    {
        if (ItemToUpdate(idFile) is not { } id) return 3; // before Steam is touched: there is nothing to create
        return Session((ugc, utils, steamId) =>
        {
            Console.WriteLine($"Steam: logged in as {steamId} (account {steamId & 0xFFFFFFFF}), app {Steam.SteamAPI_ISteamUtils_GetAppID(utils)}");
            Console.WriteLine($"item {id} ({idFile}): a content update of it - the publisher never creates an item");

            var content = Path.GetFullPath(staging);
            Pack(addon, content, id);

            var h = Steam.SteamAPI_ISteamUGC_StartItemUpdate(ugc, App, id);
            Check("SetItemTitle", Steam.SteamAPI_ISteamUGC_SetItemTitle(ugc, h, Utf8(Title)));
            Check("SetItemDescription", Steam.SteamAPI_ISteamUGC_SetItemDescription(ugc, h, Utf8(Description)));
            Check("SetItemContent", Steam.SteamAPI_ISteamUGC_SetItemContent(ugc, h, Utf8(Path.GetFullPath(content))));
            Check("SetItemPreview", Steam.SteamAPI_ISteamUGC_SetItemPreview(ugc, h, Utf8(Path.GetFullPath(preview))));
            // An update leaves the visibility alone: the item is Public, and other players' games refuse an Unlisted item with ERROR 15 in
            // the download prompt while its owner's downloads it.
            var submit = Steam.SteamAPI_ISteamUGC_SubmitItemUpdate(ugc, h, Utf8(note));
            var s = Wait<SubmitItemUpdateResult>(utils, submit, 3404, () =>
            {
                var status = Steam.SteamAPI_ISteamUGC_GetItemUpdateProgress(ugc, h, out var done, out var total);
                return $"status {status} {done}/{total}";
            });
            Console.WriteLine($"SubmitItemUpdate: result {s.Result}, id {s.Id}, legal agreement needed {s.NeedsLegal != 0}");
            Console.WriteLine($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
            return s.Result == 1 ? 0 : 1;
        });
    }

    /// <summary>The item as its owner's client sees it (anonymous web lookups do not show unlisted items).</summary>
    static unsafe int Query(ulong id) => Session((ugc, utils, _) =>
    {
        var q = Steam.SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest(ugc, [id], 1);
        var done = Wait<QueryCompleted>(utils, Steam.SteamAPI_ISteamUGC_SendQueryUGCRequest(ugc, q), 3401);
        Console.WriteLine($"query: result {done.Result}, returned {done.Returned}, cached {done.Cached != 0}");
        var buf = new byte[16384];
        fixed (byte* p = buf)
        {
            var ok = Steam.SteamAPI_ISteamUGC_GetQueryUGCResult(ugc, q, 0, (IntPtr)p);
            string Str(int off, int max) { var e = Array.IndexOf(buf, (byte)0, off, max); return Encoding.UTF8.GetString(buf, off, e >= 0 ? e - off : max); }
            Console.WriteLine($"details ok {ok}: id {BitConverter.ToUInt64(buf, 0)}, result {BitConverter.ToInt32(buf, 8)}, type {BitConverter.ToInt32(buf, 12)}, " +
                $"creator app {BitConverter.ToUInt32(buf, 16)}, consumer app {BitConverter.ToUInt32(buf, 20)}, title '{Str(24, 129)}'");
            Console.WriteLine($"owner {BitConverter.ToUInt64(buf, 8160)}, created {BitConverter.ToUInt32(buf, 8168)}, updated {BitConverter.ToUInt32(buf, 8172)}, " +
                $"visibility {BitConverter.ToInt32(buf, 8180)} (3 = unlisted), banned {buf[8184]}, accepted {buf[8185]}, tags '{Str(8187, 1025)}'");
            Console.WriteLine($"file handle {BitConverter.ToUInt64(buf, 9216)}, preview {BitConverter.ToUInt64(buf, 9224)}, file '{Str(9232, 260)}', " +
                $"file size {BitConverter.ToInt32(buf, 9492)}, preview size {BitConverter.ToInt32(buf, 9496)}, total files size {BitConverter.ToUInt64(buf, 9776)}");
        }
        Steam.SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(ugc, q);
        return 0;
    });
}
