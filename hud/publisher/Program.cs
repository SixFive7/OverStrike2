// Uploads the gungame_hud addon as a content update of the one HUD Workshop item for Counter-Strike 2 (app 730), through the Steam
// client that is already running and logged in on this PC - it never sees a password: SteamAPI_Init attaches to that client.
// It never creates an item (AGENTS.md, "cs2 in-game HUD - NEVER create another Workshop item"; user decision 2026-10-21).
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
//                                                                          page (the Web API calls an item awaiting moderation public);
//                                                                          exit 0 only when it is
//   publisher query <id>                                                    the item as its owner's client sees it
//   publisher list                                                          every item this account published for app 730
//   publisher visibility <id> <0|1|2|3>                                     public, friends only, private, unlisted - no new revision
//   publisher delete <id>                                                   delete one of the account's items for good
//   publisher unsubscribe <id>                                              unsubscribe the account from an item (anybody's)
//
// hud/publish.ps1 runs it with hud/workshop_id.txt, which names the one HUD item (3823993566). An empty, missing or unreadable id file
// is refused before Steam is even asked: creating an item is what the rule forbids, so the code cannot do it.
// Needs CS2's own steam_api64.dll beside it (publish.ps1 copies it): its flat API exports SteamUGC_v021.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;


static class Program
{
    const uint App = 730;
    // The one HUD item's title (3823993566, since release v3_0_0): "its earlier title" until content v4_0_0, "Over Strike 2" since
    // (the owner's name for the server and its HUD, 2026-11-01). An update sets it again: the same item, never a new one.
    const string Title = "Over Strike 2";
    const string Description =
        "Over Strike 2 - Over the top, on the Huis. The in-game menu of the Over Strike 2 server (Arms Race on 3,694 Workshop maps, Deathmatch and Wingman by vote) - the server's whole " +
        "interface: every map with its picture, size and votes, full-text search on an on-screen keyboard, and any Workshop map by its id; " +
        "the fair queue, the vote at a match's end, votes, game settings, sixteen fun settings anybody can switch (friendly fire, low gravity, Vampire, Laser guns and more), help, rules and how " +
        "it all works - and a chat command for every action (!help). Hold TAB for the live board beside the scoreboard: vote, Next, the queue and the full menu, with the mouse.\n\n" +
        "You do not need to subscribe: the server hands it to your game when you join. It shows nothing anywhere else.\n\n" +
        "Built on the custom_hud_layout panel CS2 added on 2026-08-24. Layout and stylesheet only - no scripts.";
    const string ApprovedFlag = "--user-approved-update";

    static int Main(string[] args)
    {
        try
        {
            // Never another HUD item, and a new version of the one item only with the user's go for that release (AGENTS.md): every
            // version waits in Valve's moderation queue. Only an explicit, user-approved run may upload, and only as an update.
            if (args is ["publish", ..] && !args.Contains(ApprovedFlag))
            {
                Console.WriteLine($"REFUSED: publishing uploads a new version of the HUD item, which waits for Valve's moderation. AGENTS.md, " +
                    $"\"cs2 in-game HUD - NEVER create another Workshop item\": ask the user first; only with their go for this release pass {ApprovedFlag}.");
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
                ["visibility", var id, var v] => Visibility(ulong.Parse(id), int.Parse(v)),
                ["list"] => List(),
                ["delete", var id] => Delete(ulong.Parse(id)),
                ["unsubscribe", var id] => Unsubscribe(ulong.Parse(id)),
                _ => Usage(),
            };
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + e); return 1; }
    }

    static int Usage() { Console.WriteLine($"publisher pack <addon dir> <out dir> <id> | publish <addon dir> <id file> <staging dir> <preview.jpg> <change note> {ApprovedFlag} | public <id> | query <id> | list | visibility <id> <0-3> | delete <id> | unsubscribe <id>"); return 2; }

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

    /// <summary>Whether Steam shows the item to everybody: an anonymous visit to its Workshop page, as a stranger's browser makes it. Steam's
    /// Error page means not yet (it is what everybody but the owner gets while the item or its latest version awaits moderation); the
    /// Web API is no help, it reports such an item as public. 0 = shown to everybody, 1 = not shown, 2 = no verdict (rate limited, error).
    /// Since Steam's new Workshop pages (2026-10-31) that page is also an HTTP 500 titled just "Steam Workshop" - for an item held for
    /// moderation exactly as for an id that does not exist (#19, 2026-11-01; the plugin's HudCheck.PagePublic reads it the same way).</summary>
    static int PublicNow(ulong id)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
        using var response = http.GetAsync(url).GetAwaiter().GetResult();
        var status = (int)response.StatusCode;
        var html = response.IsSuccessStatusCode || status == 500 ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult() : "";
        var title = System.Text.RegularExpressions.Regex.Match(html, "<title>([^<]*)</title>").Groups[1].Value;
        if (status == 500 && html.Contains("<title>Steam Workshop</title>")) { Console.WriteLine($"{id}: NOT shown to everybody - Steam's new error page for a visitor, HTTP 500 'Steam Workshop' (waiting for moderation, or private, or gone)"); return 1; }
        if (!response.IsSuccessStatusCode) { Console.WriteLine($"{id}: no verdict - HTTP {status}{(title != "" ? $" '{title}'" : "")} (429 = Steam rate-limits this address; try again later)"); return 2; }
        if (html.Contains("<title>Steam Community :: Error</title>")) { Console.WriteLine($"{id}: NOT shown to everybody - Steam's Error page for a visitor (waiting for moderation, or private, or gone)"); return 1; }
        if (title.StartsWith("Steam Workshop::")) { Console.WriteLine($"{id}: shown to everybody - '{title}'"); return 0; }
        Console.WriteLine($"{id}: no verdict - an unexpected page ('{title}')");
        return 2;
    }

    // ---- the content folder ----

    static int Pack(string addon, string outDir, ulong id)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir)) File.Delete(f);
        var vpk = Path.Combine(outDir, $"{id}.vpk");
        var n = Vpk.Write(addon, vpk);
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
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), App.ToString());
        Environment.SetEnvironmentVariable("SteamAppId", App.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", App.ToString());

        var err = Marshal.AllocHGlobal(1024);
        var init = Steam.SteamAPI_InitFlat(err);
        if (init != 0) { Console.WriteLine($"SteamAPI_InitFlat = {init}: {Marshal.PtrToStringUTF8(err)}"); return 1; }
        try
        {
            var user = Steam.SteamAPI_SteamUser_v023();
            var steamId = Steam.SteamAPI_ISteamUser_GetSteamID(user);
            var utils = Steam.SteamAPI_SteamUtils_v010();
            var ugc = Steam.SteamAPI_SteamUGC_v021();
            Console.WriteLine($"Steam: logged in as {steamId} (account {steamId & 0xFFFFFFFF}), app {Steam.SteamAPI_ISteamUtils_GetAppID(utils)}");
            if (ugc == IntPtr.Zero || utils == IntPtr.Zero) { Console.WriteLine("no ISteamUGC/ISteamUtils"); return 1; }
            Console.WriteLine($"item {id} ({idFile}): a content update of it - the publisher never creates an item");

            var content = Path.GetFullPath(staging);
            Pack(addon, content, id);

            var h = Steam.SteamAPI_ISteamUGC_StartItemUpdate(ugc, App, id);
            Check("SetItemTitle", Steam.SteamAPI_ISteamUGC_SetItemTitle(ugc, h, Utf8(Title)));
            Check("SetItemDescription", Steam.SteamAPI_ISteamUGC_SetItemDescription(ugc, h, Utf8(Description)));
            Check("SetItemContent", Steam.SteamAPI_ISteamUGC_SetItemContent(ugc, h, Utf8(Path.GetFullPath(content))));
            Check("SetItemPreview", Steam.SteamAPI_ISteamUGC_SetItemPreview(ugc, h, Utf8(Path.GetFullPath(preview))));
            // An update leaves the visibility alone (the item is Public: on 2026-10-07 other players' games refused an Unlisted item with
            // ERROR 15 in the download prompt, while the owner's downloaded it).
            var submit = Steam.SteamAPI_ISteamUGC_SubmitItemUpdate(ugc, h, Utf8(note));
            var s = Wait<SubmitItemUpdateResult>(utils, submit, 3404, () =>
            {
                var status = Steam.SteamAPI_ISteamUGC_GetItemUpdateProgress(ugc, h, out var done, out var total);
                return $"status {status} {done}/{total}";
            });
            Console.WriteLine($"SubmitItemUpdate: result {s.Result}, id {s.Id}, legal agreement needed {s.NeedsLegal != 0}");
            Console.WriteLine($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
            return s.Result == 1 ? 0 : 1;
        }
        finally { Steam.SteamAPI_Shutdown(); Marshal.FreeHGlobal(err); }
    }

    /// <summary>Only the visibility (0 public, 1 friends only, 2 private, 3 unlisted): no content, no new revision for players to fetch.</summary>
    static int Visibility(ulong id, int visibility)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), App.ToString());
        Environment.SetEnvironmentVariable("SteamAppId", App.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", App.ToString());
        var err = Marshal.AllocHGlobal(1024);
        if (Steam.SteamAPI_InitFlat(err) is var init && init != 0) { Console.WriteLine($"SteamAPI_InitFlat = {init}: {Marshal.PtrToStringUTF8(err)}"); return 1; }
        try
        {
            var ugc = Steam.SteamAPI_SteamUGC_v021();
            var utils = Steam.SteamAPI_SteamUtils_v010();
            var h = Steam.SteamAPI_ISteamUGC_StartItemUpdate(ugc, App, id);
            Check($"SetItemVisibility({visibility})", Steam.SteamAPI_ISteamUGC_SetItemVisibility(ugc, h, visibility));
            var s = Wait<SubmitItemUpdateResult>(utils, Steam.SteamAPI_ISteamUGC_SubmitItemUpdate(ugc, h, Utf8("")), 3404);
            Console.WriteLine($"SubmitItemUpdate (visibility only): result {s.Result}, id {s.Id}");
            return s.Result == 1 ? 0 : 1;
        }
        finally { Steam.SteamAPI_Shutdown(); Marshal.FreeHGlobal(err); }
    }

    /// <summary>Runs one Steam session for a maintenance command; the account and its interfaces are handed to the body.</summary>
    static int Session(Func<IntPtr, IntPtr, uint, int> body)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), App.ToString());
        Environment.SetEnvironmentVariable("SteamAppId", App.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", App.ToString());
        var err = Marshal.AllocHGlobal(1024);
        if (Steam.SteamAPI_InitFlat(err) is var init && init != 0) { Console.WriteLine($"SteamAPI_InitFlat = {init}: {Marshal.PtrToStringUTF8(err)}"); return 1; }
        try
        {
            var account = (uint)(Steam.SteamAPI_ISteamUser_GetSteamID(Steam.SteamAPI_SteamUser_v023()) & 0xFFFFFFFF);
            return body(Steam.SteamAPI_SteamUGC_v021(), Steam.SteamAPI_SteamUtils_v010(), account);
        }
        finally { Steam.SteamAPI_Shutdown(); Marshal.FreeHGlobal(err); }
    }

    /// <summary>Every item this account has published for CS2 (private ones too: the owner asks), newest first, with whether the
    /// account is subscribed to it.</summary>
    static unsafe int List() => Session((ugc, utils, account) =>
    {
        Console.WriteLine($"account {account}: items published for app {App}");
        var buf = new byte[16384];
        for (uint page = 1; page <= 20; page++)
        {
            // k_EUserUGCList_Published, k_EUGCMatchingUGCType_All (~0, valid for user queries), k_EUserUGCListSortOrder_CreationOrderDesc
            var q = Steam.SteamAPI_ISteamUGC_CreateQueryUserUGCRequest(ugc, account, 0, -1, 0, App, App, page);
            var done = Wait<QueryCompleted>(utils, Steam.SteamAPI_ISteamUGC_SendQueryUGCRequest(ugc, q), 3401);
            Console.WriteLine($"page {page}: result {done.Result}, returned {done.Returned} of {done.Total}");
            fixed (byte* p = buf)
                for (uint i = 0; i < done.Returned; i++)
                {
                    Array.Clear(buf);
                    if (!Steam.SteamAPI_ISteamUGC_GetQueryUGCResult(ugc, q, i, (IntPtr)p)) continue;
                    string Str(int off, int max) { var e = Array.IndexOf(buf, (byte)0, off, max); return Encoding.UTF8.GetString(buf, off, e >= 0 ? e - off : max); }
                    var id = BitConverter.ToUInt64(buf, 0);
                    string Day(uint t) => DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    Console.WriteLine($"  {id}  '{Str(24, 129)}'  type {BitConverter.ToInt32(buf, 12)}  created {Day(BitConverter.ToUInt32(buf, 8168))}  updated {Day(BitConverter.ToUInt32(buf, 8172))}  " +
                        $"visibility {BitConverter.ToInt32(buf, 8180)}  size {BitConverter.ToUInt64(buf, 9776)}  state {Steam.SteamAPI_ISteamUGC_GetItemState(ugc, id)}");
                }
            Steam.SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(ugc, q);
            if (done.Returned == 0 || page * 50 >= done.Total) break;
        }
        return 0;
    });

    /// <summary>Deletes one of the account's own items for good (ISteamUGC::DeleteItem).</summary>
    static int Delete(ulong id) => Session((ugc, utils, _) =>
    {
        var r = Wait<FileResult>(utils, Steam.SteamAPI_ISteamUGC_DeleteItem(ugc, id), 3417); // DeleteItemResult_t
        Console.WriteLine($"DeleteItem {id}: result {r.Result} (1 = ok), id {r.Id}");
        return r.Result == 1 ? 0 : 1;
    });

    /// <summary>Unsubscribes the account from an item (anybody's), with its item state before and after (1 = subscribed).</summary>
    static int Unsubscribe(ulong id) => Session((ugc, utils, _) =>
    {
        Console.WriteLine($"item state before: {Steam.SteamAPI_ISteamUGC_GetItemState(ugc, id)}");
        var r = Wait<FileResult>(utils, Steam.SteamAPI_ISteamUGC_UnsubscribeItem(ugc, id), 1315); // RemoteStorageUnsubscribePublishedFileResult_t
        Console.WriteLine($"UnsubscribeItem {id}: result {r.Result} (1 = ok); item state after: {Steam.SteamAPI_ISteamUGC_GetItemState(ugc, id)}");
        return r.Result == 1 ? 0 : 1;
    });

    /// <summary>The item as its owner's client sees it (anonymous web lookups do not show unlisted items).</summary>
    static unsafe int Query(ulong id)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), App.ToString());
        var err = Marshal.AllocHGlobal(1024);
        if (Steam.SteamAPI_InitFlat(err) is var init && init != 0) { Console.WriteLine($"SteamAPI_InitFlat = {init}: {Marshal.PtrToStringUTF8(err)}"); return 1; }
        try
        {
            var ugc = Steam.SteamAPI_SteamUGC_v021();
            var utils = Steam.SteamAPI_SteamUtils_v010();
            var ids = new[] { id };
            var q = Steam.SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest(ugc, ids, 1);
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
        }
        finally { Steam.SteamAPI_Shutdown(); Marshal.FreeHGlobal(err); }
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    static void Check(string what, bool ok) { Console.WriteLine($"{what}: {(ok ? "ok" : "REFUSED")}"); if (!ok) throw new InvalidOperationException(what + " refused"); }

    static unsafe T Wait<T>(IntPtr utils, ulong call, int callbackId, Func<string>? progress = null) where T : unmanaged
    {
        if (call == 0) throw new InvalidOperationException("k_uAPICallInvalid");
        var last = DateTime.MinValue;
        for (var start = DateTime.UtcNow; DateTime.UtcNow - start < TimeSpan.FromMinutes(10);)
        {
            Steam.SteamAPI_RunCallbacks();
            if (Steam.SteamAPI_ISteamUtils_IsAPICallCompleted(utils, call, out var failed))
            {
                T result = default;
                if (!Steam.SteamAPI_ISteamUtils_GetAPICallResult(utils, call, (IntPtr)(&result), sizeof(T), callbackId, out failed) || failed)
                    throw new InvalidOperationException($"API call {callbackId} failed");
                return result;
            }
            if (progress != null && DateTime.UtcNow - last > TimeSpan.FromSeconds(2)) { Console.WriteLine("  " + progress()); last = DateTime.UtcNow; }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"API call {callbackId} did not complete in 10 minutes");
    }
}

// Steam callback structs, VALVE_CALLBACK_PACK_LARGE (8) on Windows.
[StructLayout(LayoutKind.Explicit, Size = 280)]
struct QueryCompleted { [FieldOffset(0)] public ulong Handle; [FieldOffset(8)] public int Result; [FieldOffset(12)] public uint Returned; [FieldOffset(16)] public uint Total; [FieldOffset(20)] public byte Cached; }

// DeleteItemResult_t and RemoteStorageUnsubscribePublishedFileResult_t: an EResult and the item's id.
[StructLayout(LayoutKind.Explicit, Size = 16)]
struct FileResult { [FieldOffset(0)] public int Result; [FieldOffset(8)] public ulong Id; }

[StructLayout(LayoutKind.Explicit, Size = 16)]
struct SubmitItemUpdateResult { [FieldOffset(0)] public int Result; [FieldOffset(4)] public byte NeedsLegal; [FieldOffset(8)] public ulong Id; }

static class Steam
{
    const string Dll = "steam_api64";
    [DllImport(Dll)] public static extern int SteamAPI_InitFlat(IntPtr errMsg);
    [DllImport(Dll)] public static extern void SteamAPI_Shutdown();
    [DllImport(Dll)] public static extern void SteamAPI_RunCallbacks();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUGC_v021();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUtils_v010();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUser_v023();
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr user);
    [DllImport(Dll)] public static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr utils);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_StartItemUpdate(IntPtr ugc, uint app, ulong id);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemTitle(IntPtr ugc, ulong h, byte[] s);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemDescription(IntPtr ugc, ulong h, byte[] s);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemContent(IntPtr ugc, ulong h, byte[] s);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemPreview(IntPtr ugc, ulong h, byte[] s);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemVisibility(IntPtr ugc, ulong h, int visibility);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_SubmitItemUpdate(IntPtr ugc, ulong h, byte[] note);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest(IntPtr ugc, ulong[] ids, uint n);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_CreateQueryUserUGCRequest(IntPtr ugc, uint account, int list, int matching, int sort, uint creatorApp, uint consumerApp, uint page);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_DeleteItem(IntPtr ugc, ulong id);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_UnsubscribeItem(IntPtr ugc, ulong id);
    [DllImport(Dll)] public static extern uint SteamAPI_ISteamUGC_GetItemState(IntPtr ugc, ulong id);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_SendQueryUGCRequest(IntPtr ugc, ulong q);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_GetQueryUGCResult(IntPtr ugc, ulong q, uint index, IntPtr details);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(IntPtr ugc, ulong q);
    [DllImport(Dll)] public static extern int SteamAPI_ISteamUGC_GetItemUpdateProgress(IntPtr ugc, ulong h, out ulong done, out ulong total);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr utils, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr utils, ulong call, IntPtr buf, int size, int expected, [MarshalAs(UnmanagedType.I1)] out bool failed);
}

/// <summary>A VPK 2 with its data embedded, laid out like the Workshop Manager's: tree, data, 1 MB chunk MD5s (archive index
/// 0x80000000), the 48-byte checksum block (tree, chunk table, whole file) and a 20-byte empty signature record.</summary>
static class Vpk
{
    public static int Write(string root, string outPath)
    {
        root = Path.GetFullPath(root);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(r => !r.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        var data = new MemoryStream();
        var byExt = new SortedDictionary<string, SortedDictionary<string, List<(string Name, uint Crc, uint Off, uint Len)>>>(StringComparer.Ordinal);
        foreach (var rel in files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, rel));
            var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') is { Length: > 0 } d ? d : " ";
            var file = Path.GetFileName(rel);
            var dot = file.LastIndexOf('.');
            var (name, ext) = dot > 0 ? (file[..dot], file[(dot + 1)..]) : (file, " ");
            if (!byExt.TryGetValue(ext, out var paths)) byExt[ext] = paths = new(StringComparer.Ordinal);
            if (!paths.TryGetValue(dir, out var list)) paths[dir] = list = new();
            list.Add((name, Crc32(bytes), (uint)data.Position, (uint)bytes.Length));
            data.Write(bytes);
        }
        var tree = new MemoryStream();
        void Str(string s) { tree.Write(Encoding.UTF8.GetBytes(s)); tree.WriteByte(0); }
        foreach (var (ext, paths) in byExt)
        {
            Str(ext);
            foreach (var (dir, list) in paths)
            {
                Str(dir);
                foreach (var (name, crc, off, len) in list)
                {
                    Str(name);
                    tree.Write(BitConverter.GetBytes(crc));
                    tree.Write(BitConverter.GetBytes((ushort)0));      // preload bytes
                    tree.Write(BitConverter.GetBytes((ushort)0x7FFF)); // data embedded in this file
                    tree.Write(BitConverter.GetBytes(off));
                    tree.Write(BitConverter.GetBytes(len));
                    tree.Write(BitConverter.GetBytes((ushort)0xFFFF));
                }
                tree.WriteByte(0);
            }
            tree.WriteByte(0);
        }
        tree.WriteByte(0);

        var dataBytes = data.ToArray();
        var chunks = new MemoryStream();
        const int Chunk = 1 << 20;
        for (var off = 0; off < dataBytes.Length; off += Chunk)
        {
            var n = Math.Min(Chunk, dataBytes.Length - off);
            chunks.Write(BitConverter.GetBytes(0x80000000u));
            chunks.Write(BitConverter.GetBytes((uint)off));
            chunks.Write(BitConverter.GetBytes((uint)n));
            chunks.Write(MD5.HashData(dataBytes.AsSpan(off, n)));
        }
        var treeBytes = tree.ToArray();
        var chunkBytes = chunks.ToArray();
        var outStream = new MemoryStream();
        foreach (var v in new uint[] { 0x55AA1234, 2, (uint)treeBytes.Length, (uint)dataBytes.Length, (uint)chunkBytes.Length, 48, 20 })
            outStream.Write(BitConverter.GetBytes(v));
        outStream.Write(treeBytes);
        outStream.Write(dataBytes);
        outStream.Write(chunkBytes);
        outStream.Write(MD5.HashData(treeBytes));
        outStream.Write(MD5.HashData(chunkBytes));
        outStream.Write(MD5.HashData(outStream.ToArray()));                 // everything so far: header, tree, data, chunks, first 32 checksum bytes
        outStream.Write(BitConverter.GetBytes(0x55AA1234u));
        outStream.Write(BitConverter.GetBytes(1u));
        outStream.Write(new byte[12]);
        File.WriteAllBytes(outPath, outStream.ToArray());
        return files.Count;
    }

    static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
