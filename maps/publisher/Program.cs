// The server's own Counter-Strike 2 maps on the Steam Workshop (app 730), one folder each in maps/<map>/, through the
// Steam client that is running and logged in on this PC - it never sees a password: SteamAPI_Init attaches to that client, exactly as
// the HUD's publisher does (hud/publisher/: no create path by design, and nothing here touches it or its item).
//
// A map folder holds workshop.json - the item's title, description and tags, and the map's name - and, once the item exists,
// workshop_id.txt. create writes workshop_id.txt the moment Steam has made the item, before anything is uploaded, and refuses to run
// while it names an item: a failed upload is retried with update, never with a second item for the same map (Valve frowns on
// near-identical new items; AGENTS.md, the HUD's rule).
//
// What a CS2 map item holds - read off 197 map items in steamapps/workshop/content/730/:
//   <id>.vpk          the compiled addon's game folder (game/csgo_addons/<addon>/) packed: addoninfo.txt (180 of the 197), the map
//                     itself as maps/<map>.vpk (a VPK of its own, stored as one file), and whatever else the compile left there -
//                     cfg/maps/<map>.cfg, materials/, models/, sounds/, soundevents/, postprocess/, panorama/ ... - but never the
//                     tools' readonly_tools_asset_info.bin. The Workshop Manager writes it split (<id>_dir.vpk + <id>_000.vpk ...)
//                     since 2025-02 and as one VPK with its data embedded before that; CS2 loads both (79 of the 197 are the single
//                     form, Glass War #1001 among them). This writes the single form - the HUD publisher's VPK 2 byte for byte, which
//                     CS2 clients have loaded for three HUD releases - and reads it back before anything goes out.
//   publish_data.txt  title, source_folder, publish_time, as the Workshop Manager writes it. No addoninfo.txt beside the VPK (only
//                     the HUD's items carry one there).
// Tags, from GetPublishedFileDetails of the 4,277 Arms Race items (2026-10-24): Cs2 and Map on every map, then its game modes -
// Armsrace (on all of them: the Workshop's Arms Race filter is requiredtags[]=Armsrace), Deathmatch, Classic, Custom, Wingman,
// Demolition, Hostage.
//
//   mappublisher pack <map dir> <addon dir> <out dir> <id>           the content folder only, checked by reading it back (no Steam)
//   mappublisher create <map dir> <addon dir> <staging dir> <preview.jpg> --user-approved-create
//                                                                    a NEW public item: everything is checked and packed first, then
//                                                                    CreateItem, workshop_id.txt, and the title, description, tags,
//                                                                    Public, content and preview. REFUSED without the flag, and
//                                                                    REFUSED, flag or not, while workshop_id.txt names an item
//   mappublisher update <map dir> <addon dir> <staging dir> <preview.jpg | -> <change note> --user-approved-update
//                                                                    a new version of the item workshop_id.txt names (- keeps the
//                                                                    preview); the visibility is left alone. REFUSED without the flag,
//                                                                    a note or an id
//   mappublisher describe <map dir> --user-approved-update           the texts only: title, description and tags from workshop.json,
//                                                                    no content and no preview - the map players download stays as
//                                                                    it is. REFUSED without the flag or an id
//   mappublisher query <id>                                          the item as its owner's client sees it
//   mappublisher list                                                every item this account published for app 730
//   mappublisher visibility <id> <0|1|2|3>                           public, friends only, private, unlisted - no new version (a new
//                                                                    item is private until an upload says Public)
//   mappublisher public <id>                                         shown to everybody? An anonymous visit to its Workshop page (the
//                                                                    Web API calls an item awaiting moderation public); exit 0 only
//                                                                    when it is, 1 when not, 2 with no verdict (429, no answer)
//   mappublisher watch <id> [seconds] [minutes]                      the moderation watcher: that visit every <seconds> (60) until the
//                                                                    page shows the item, for at most <minutes> (240); exit 0 the
//                                                                    moment everybody can see it, 4 if the Web API says banned
//
// publish.ps1 beside this file builds it, copies CS2's own steam_api64.dll next to it (its flat API exports SteamUGC_v021) and fills
// in the paths for a map folder.
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

static class Program
{
    const uint App = 730;
    const int FileTypeCommunity = 0;   // k_EWorkshopFileTypeCommunity: an ordinary item anybody can subscribe to, as every map is
    const int VisibilityPublic = 0;    // k_ERemoteStoragePublishedFileVisibilityPublic
    const string IdFileName = "workshop_id.txt", ItemFileName = "workshop.json";
    const string CreateFlag = "--user-approved-create", UpdateFlag = "--user-approved-update";
    const long MaxPreviewBytes = 1_000_000; // Steam refuses a bigger preview; checked before an item is created

    static int Main(string[] args)
    {
        try
        {
            if (args is ["create", ..] && !args.Contains(CreateFlag))
                throw new Refusal($"create makes a NEW public Workshop item under this account, which Valve's moderation reviews. Only with the owner's go for this map, pass {CreateFlag}.");
            if (args is ["update" or "describe", ..] && !args.Contains(UpdateFlag))
                throw new Refusal($"update uploads a new version, which waits for Valve's moderation. Only with the owner's go for this release, pass {UpdateFlag}.");
            args = args.Where(a => a != CreateFlag && a != UpdateFlag).ToArray();
            return args switch
            {
                ["pack", var map, var addon, var outDir, var id] => PackOnly(map, addon, outDir, ulong.Parse(id)),
                ["create", var map, var addon, var staging, var preview] => Create(map, addon, staging, preview),
                ["update", var map, var addon, var staging, var preview, var note] => Update(map, addon, staging, preview, note),
                ["describe", var map] => Describe(map),
                ["query", var id] => Query(ulong.Parse(id)),
                ["list"] => List(),
                ["visibility", var id, var v] => Visibility(ulong.Parse(id), int.Parse(v)),
                ["public", var id] => Public(ulong.Parse(id)),
                ["watch", var id] => Watch(ulong.Parse(id), 60, 240),
                ["watch", var id, var s] => Watch(ulong.Parse(id), int.Parse(s), 240),
                ["watch", var id, var s, var m] => Watch(ulong.Parse(id), int.Parse(s), int.Parse(m)),
                _ => Usage(),
            };
        }
        catch (Refusal r) { Console.WriteLine("REFUSED: " + r.Message); return 3; }
        catch (Exception e) { Console.WriteLine("FAILED: " + e); return 1; }
    }

    static int Usage()
    {
        Console.WriteLine($"mappublisher pack <map dir> <addon dir> <out dir> <id> | create <map dir> <addon dir> <staging dir> <preview.jpg> {CreateFlag} | " +
            $"update <map dir> <addon dir> <staging dir> <preview.jpg|-> <change note> {UpdateFlag} | describe <map dir> {UpdateFlag} | query <id> | list | visibility <id> <0-3> | public <id> | watch <id> [seconds] [minutes]");
        return 2;
    }

    // ---- the item ----

    static ulong? ReadId(string idFile) =>
        File.Exists(idFile) && ulong.TryParse(File.ReadAllText(idFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0 ? id : null;

    static int Create(string mapDir, string addon, string staging, string preview)
    {
        var idFile = Path.Combine(mapDir, IdFileName);
        if (ReadId(idFile) is { } existing)
            throw new Refusal($"{idFile} names item {existing}: this map has its Workshop item already - a new version is update, never a second item.");
        if (File.Exists(idFile) && File.ReadAllText(idFile).Trim() != "")
            throw new Refusal($"{idFile} holds '{File.ReadAllText(idFile).Trim()}', not an item id - fix it by hand first.");
        var item = Item.Load(mapDir);
        CheckPreview(preview);
        var content = Pack(item, addon, staging, 0); // everything is checked and packed before an item exists; 0.vpk is renamed below
        return Session((ugc, utils, steamId) =>
        {
            Console.WriteLine($"creating a new Workshop item for '{item.Title}' (app {App}) as account {steamId & 0xFFFFFFFF} ({steamId})");
            var made = Wait<CreateItemResult>(utils, Steam.SteamAPI_ISteamUGC_CreateItem(ugc, App, FileTypeCommunity), 3403);
            Console.WriteLine($"CreateItem: result {made.Result} (1 = ok), id {made.Id}, legal agreement needed {made.NeedsLegal != 0}, at {DateTime.Now:HH:mm:ss}");
            if (made.Result != 1 || made.Id == 0) return 1;
            File.WriteAllText(idFile, made.Id.ToString(CultureInfo.InvariantCulture) + "\n");
            Console.WriteLine($"{idFile} names {made.Id} now: from here on this map's uploads are update, never create");
            if (made.NeedsLegal != 0)
                Console.WriteLine("WARNING: Steam says this account must accept the Workshop legal agreement (https://steamcommunity.com/sharedfiles/workshoplegalagreement) - the item stays hidden until it does");
            File.Move(Path.Combine(content, "0.vpk"), Path.Combine(content, $"{made.Id}.vpk"));
            return Upload(ugc, utils, made.Id, item, content, preview, "First release.", VisibilityPublic);
        });
    }

    /// <summary>The texts only - title, description and tags from workshop.json - with no SetItemContent and no SetItemPreview, so no new
    /// content version is made and the map players download stays the one that is approved.</summary>
    static int Describe(string mapDir)
    {
        var idFile = Path.Combine(mapDir, IdFileName);
        if (ReadId(idFile) is not { } id) throw new Refusal($"{idFile} names no item - a map's first upload is create.");
        var item = Item.Load(mapDir);
        return Session((ugc, utils, steamId) =>
        {
            Console.WriteLine($"the texts of {id} ('{item.Title}') as account {steamId & 0xFFFFFFFF}: no content, no preview");
            var h = Steam.SteamAPI_ISteamUGC_StartItemUpdate(ugc, App, id);
            Check("SetItemTitle", Steam.SteamAPI_ISteamUGC_SetItemTitle(ugc, h, Utf8(item.Title)));
            Check("SetItemDescription", Steam.SteamAPI_ISteamUGC_SetItemDescription(ugc, h, Utf8(item.Description)));
            Check($"SetItemTags({string.Join(", ", item.Tags)})", SetTags(ugc, h, item.Tags));
            var s = Wait<SubmitItemUpdateResult>(utils, Steam.SteamAPI_ISteamUGC_SubmitItemUpdate(ugc, h, Utf8("")), 3404);
            Console.WriteLine($"SubmitItemUpdate (texts only): result {s.Result} (1 = ok), id {s.Id}, legal agreement needed {s.NeedsLegal != 0}, done at {DateTime.Now:HH:mm:ss}");
            Console.WriteLine($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
            return s.Result == 1 ? 0 : 1;
        });
    }

    static int Update(string mapDir, string addon, string staging, string preview, string note)
    {
        var idFile = Path.Combine(mapDir, IdFileName);
        if (ReadId(idFile) is not { } id) throw new Refusal($"{idFile} names no item - a map's first upload is create.");
        if (note.Trim() == "") throw new Refusal("a new version needs a change note - Steam shows it in the item's change notes.");
        var item = Item.Load(mapDir);
        var keepPreview = preview == "-";
        if (!keepPreview) CheckPreview(preview);
        var content = Pack(item, addon, staging, id);
        return Session((ugc, utils, steamId) =>
        {
            Console.WriteLine($"a new version of {id} ('{item.Title}') as account {steamId & 0xFFFFFFFF}; the visibility is left as it is");
            return Upload(ugc, utils, id, item, content, keepPreview ? null : preview, note, null);
        });
    }

    /// <summary>One upload: title, description, tags, the visibility when given, the content folder, the preview when given.</summary>
    static int Upload(IntPtr ugc, IntPtr utils, ulong id, Item item, string content, string? preview, string note, int? visibility)
    {
        var h = Steam.SteamAPI_ISteamUGC_StartItemUpdate(ugc, App, id);
        Check("SetItemTitle", Steam.SteamAPI_ISteamUGC_SetItemTitle(ugc, h, Utf8(item.Title)));
        Check("SetItemDescription", Steam.SteamAPI_ISteamUGC_SetItemDescription(ugc, h, Utf8(item.Description)));
        Check($"SetItemTags({string.Join(", ", item.Tags)})", SetTags(ugc, h, item.Tags));
        if (visibility is { } v) Check($"SetItemVisibility({v})", Steam.SteamAPI_ISteamUGC_SetItemVisibility(ugc, h, v));
        Check($"SetItemContent({content})", Steam.SteamAPI_ISteamUGC_SetItemContent(ugc, h, Utf8(content)));
        if (preview != null) Check("SetItemPreview", Steam.SteamAPI_ISteamUGC_SetItemPreview(ugc, h, Utf8(Path.GetFullPath(preview))));
        var started = DateTime.Now;
        var s = Wait<SubmitItemUpdateResult>(utils, Steam.SteamAPI_ISteamUGC_SubmitItemUpdate(ugc, h, Utf8(note)), 3404, () =>
        {
            var status = Steam.SteamAPI_ISteamUGC_GetItemUpdateProgress(ugc, h, out var done, out var total);
            return $"status {status} {done}/{total}"; // 1 config, 2 content, 3 uploading content, 4 uploading preview, 5 committing
        });
        Console.WriteLine($"SubmitItemUpdate: result {s.Result} (1 = ok), id {s.Id}, legal agreement needed {s.NeedsLegal != 0}, " +
            $"{(DateTime.Now - started).TotalSeconds:0} s, done at {DateTime.Now:HH:mm:ss}");
        Console.WriteLine($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
        if (s.Result == 1)
            Console.WriteLine($"next: Valve's moderation reviews it (17-22 minutes for the HUD's items); until a visitor sees it, other players' downloads fail with ERROR 15. Watch it: mappublisher watch {id}");
        return s.Result == 1 ? 0 : 1;
    }

    static bool SetTags(IntPtr ugc, ulong h, string[] tags)
    {
        // SteamParamStringArray_t { const char **m_ppStrings; int32 m_nNumStrings; }: the pointer at 0, the count at 8 on x64.
        var strings = tags.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var array = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, strings.Length));
        var param = Marshal.AllocHGlobal(16);
        try
        {
            for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(param, 0, array);
            Marshal.WriteInt64(param, 8, strings.Length);
            return Steam.SteamAPI_ISteamUGC_SetItemTags(ugc, h, param, false); // bAllowAdminTags = false (a 4th argument older SDKs ignore)
        }
        finally
        {
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
            Marshal.FreeHGlobal(array);
            Marshal.FreeHGlobal(param);
        }
    }

    static void CheckPreview(string preview)
    {
        if (!File.Exists(preview)) throw new Refusal($"no preview at {preview}");
        var b = File.ReadAllBytes(preview);
        if (b.Length > MaxPreviewBytes) throw new Refusal($"{preview} is {b.Length:N0} bytes; Steam takes a preview under 1 MB");
        var jpeg = b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8;
        var png = b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
        if (!jpeg && !png) throw new Refusal($"{preview} is neither a JPEG nor a PNG");
        Console.WriteLine($"preview {preview}: {b.Length:N0} bytes, {(jpeg ? "JPEG " + JpegSize(b) : "PNG")}");
    }

    /// <summary>A JPEG's width x height, from its first start-of-frame marker.</summary>
    static string JpegSize(byte[] b)
    {
        for (var i = 2; i + 9 < b.Length;)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker is 0xD8 or 0x01 or 0xFF or (>= 0xD0 and <= 0xD7)) { i += marker == 0xFF ? 1 : 2; continue; }
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return $"{(b[i + 7] << 8) | b[i + 8]}x{(b[i + 5] << 8) | b[i + 6]}";
            i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        }
        return "(size unread)";
    }

    // ---- the content folder ----

    static int PackOnly(string mapDir, string addon, string outDir, ulong id)
    {
        Pack(Item.Load(mapDir), addon, outDir, id);
        return 0;
    }

    /// <summary>The content folder Steam uploads: &lt;id&gt;.vpk and publish_data.txt. Only those are ever deleted from it, and anything
    /// else in it is refused (Steam would upload it too). The VPK is read back and checked before it is used.</summary>
    static string Pack(Item item, string addon, string outDir, ulong id)
    {
        addon = Path.GetFullPath(addon);
        if (!Directory.Exists(addon)) throw new Refusal($"no compiled addon at {addon} (the Workshop Tools compile one into game\\csgo_addons\\<addon>)");
        var mapVpk = Path.Combine(addon, "maps", item.Map + ".vpk");
        if (!File.Exists(mapVpk)) throw new Refusal($"{mapVpk} is missing - the compiled map is what the item is for");
        using (var f = File.OpenRead(mapVpk))
        {
            var head = new byte[4];
            if (f.Read(head) != 4 || BitConverter.ToUInt32(head) != Vpk.Signature) throw new Refusal($"{mapVpk} is not a VPK - is the compile finished?");
        }
        if (!File.Exists(Path.Combine(addon, "addoninfo.txt")))
            Console.WriteLine("note: the addon has no addoninfo.txt (180 of 197 cached map items carry one; the map loads without it)");

        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir).Where(f => f.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "publish_data.txt"))
            File.Delete(f);
        if (Directory.GetFileSystemEntries(outDir) is { Length: > 0 } other)
            throw new Refusal($"{outDir} holds {string.Join(", ", other.Select(Path.GetFileName))} - Steam would upload that too; give an empty staging folder");

        var vpk = Path.Combine(outDir, $"{id}.vpk");
        var packed = Vpk.Write(addon, vpk);
        var now = DateTimeOffset.Now;
        File.WriteAllText(Path.Combine(outDir, "publish_data.txt"),
            "\"publish_data\"\n{\n" +
            $"\t\"title\"\t\t\"{item.Title.Replace("\"", "'")}\"\n" +
            $"\t\"source_folder\"\t\t\"{Path.GetFileName(addon.TrimEnd('\\', '/'))}\"\n" +
            $"\t\"publish_time\"\t\t\"{now.ToUnixTimeSeconds()}\"\n" +
            $"\t\"publish_time_readable\"\t\t\"{now.ToString("MM/dd/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture)}\"\n" +
            "}\n", new UTF8Encoding(false));
        var problems = Vpk.Check(vpk, $"maps/{item.Map}.vpk");
        if (problems.Count > 0) throw new InvalidOperationException($"{vpk} does not read back: {string.Join("; ", problems.Take(10))}");
        long total = packed.Sum(p => p.Bytes);
        Console.WriteLine($"packed {packed.Count} files ({total:N0} bytes) from {addon} into {vpk} ({new FileInfo(vpk).Length:N0} bytes), read back: every CRC and MD5 good, maps/{item.Map}.vpk inside");
        foreach (var (path, bytes) in packed.Take(25)) Console.WriteLine($"  {path}  {bytes:N0}");
        if (packed.Count > 25) Console.WriteLine($"  ... and {packed.Count - 25} more");
        Console.WriteLine($"content folder {outDir}: {string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName))}");
        return outDir;
    }

    // ---- Steam ----

    /// <summary>One Steam session as app 730 through the running client; the body gets ISteamUGC, ISteamUtils and the account's SteamID.</summary>
    static int Session(Func<IntPtr, IntPtr, ulong, int> body)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), App.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("SteamAppId", App.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("SteamGameId", App.ToString(CultureInfo.InvariantCulture));
        var err = Marshal.AllocHGlobal(1024);
        try
        {
            if (Steam.SteamAPI_InitFlat(err) is var init && init != 0) { Console.WriteLine($"SteamAPI_InitFlat = {init}: {Marshal.PtrToStringUTF8(err)}"); return 1; }
            try
            {
                var ugc = Steam.SteamAPI_SteamUGC_v021();
                var utils = Steam.SteamAPI_SteamUtils_v010();
                if (ugc == IntPtr.Zero || utils == IntPtr.Zero) { Console.WriteLine("no ISteamUGC/ISteamUtils"); return 1; }
                return body(ugc, utils, Steam.SteamAPI_ISteamUser_GetSteamID(Steam.SteamAPI_SteamUser_v023()));
            }
            finally { Steam.SteamAPI_Shutdown(); }
        }
        finally { Marshal.FreeHGlobal(err); }
    }

    /// <summary>Every item this account has published for CS2, newest first.</summary>
    static unsafe int List() => Session((ugc, utils, steamId) =>
    {
        var account = (uint)(steamId & 0xFFFFFFFF);
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
                    var d = new Details(buf);
                    Console.WriteLine($"  {d.Id}  '{d.Title}'  type {d.Type}  created {Day(d.Created)}  updated {Day(d.Updated)}  visibility {d.Visibility}  banned {d.Banned}  " +
                        $"tags '{d.Tags}'  size {d.TotalBytes:N0}");
                }
            Steam.SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(ugc, q);
            if (done.Returned == 0 || page * 50 >= done.Total) break;
        }
        return 0;
    });

    /// <summary>The item as its owner's client sees it (anonymous web lookups do not show a private or unlisted item).</summary>
    static unsafe int Query(ulong id) => Session((ugc, utils, _) =>
    {
        var q = Steam.SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest(ugc, [id], 1);
        var done = Wait<QueryCompleted>(utils, Steam.SteamAPI_ISteamUGC_SendQueryUGCRequest(ugc, q), 3401);
        Console.WriteLine($"query: result {done.Result}, returned {done.Returned}, cached {done.Cached != 0}");
        var buf = new byte[16384];
        fixed (byte* p = buf)
        {
            var ok = Steam.SteamAPI_ISteamUGC_GetQueryUGCResult(ugc, q, 0, (IntPtr)p);
            var d = new Details(buf);
            Console.WriteLine($"details ok {ok}: id {d.Id}, result {d.Result}, type {d.Type}, creator app {d.CreatorApp}, consumer app {d.ConsumerApp}, title '{d.Title}'");
            Console.WriteLine($"owner {d.Owner}, created {Day(d.Created)}, updated {Day(d.Updated)}, visibility {d.Visibility} (0 public, 3 unlisted), banned {d.Banned}, " +
                $"accepted {d.Accepted}, tags '{d.Tags}'");
            Console.WriteLine($"file '{d.FileName}', file size {d.FileBytes:N0}, preview size {d.PreviewBytes:N0}, total files size {d.TotalBytes:N0}");
        }
        Steam.SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(ugc, q);
        return 0;
    });

    /// <summary>Only the visibility (0 public, 1 friends only, 2 private, 3 unlisted): no content, no new version. A new item is
    /// private until its first upload says Public - this puts that right when a create's upload failed and update finished the job.</summary>
    static int Visibility(ulong id, int visibility) => Session((ugc, utils, _) =>
    {
        var h = Steam.SteamAPI_ISteamUGC_StartItemUpdate(ugc, App, id);
        Check($"SetItemVisibility({visibility})", Steam.SteamAPI_ISteamUGC_SetItemVisibility(ugc, h, visibility));
        var s = Wait<SubmitItemUpdateResult>(utils, Steam.SteamAPI_ISteamUGC_SubmitItemUpdate(ugc, h, Utf8("")), 3404);
        Console.WriteLine($"SubmitItemUpdate (visibility only): result {s.Result} (1 = ok), id {s.Id}");
        return s.Result == 1 ? 0 : 1;
    });

    static string Day(uint t) => t == 0 ? "-" : DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    static void Check(string what, bool ok) { Console.WriteLine($"{what}: {(ok ? "ok" : "REFUSED")}"); if (!ok) throw new InvalidOperationException(what + " refused"); }

    static unsafe T Wait<T>(IntPtr utils, ulong call, int callbackId, Func<string>? progress = null) where T : unmanaged
    {
        if (call == 0) throw new InvalidOperationException("k_uAPICallInvalid");
        var last = DateTime.MinValue;
        for (var start = DateTime.UtcNow; DateTime.UtcNow - start < TimeSpan.FromMinutes(30);)
        {
            Steam.SteamAPI_RunCallbacks();
            if (Steam.SteamAPI_ISteamUtils_IsAPICallCompleted(utils, call, out var failed))
            {
                T result = default;
                if (!Steam.SteamAPI_ISteamUtils_GetAPICallResult(utils, call, (IntPtr)(&result), sizeof(T), callbackId, out failed) || failed)
                    throw new InvalidOperationException($"API call {callbackId} failed");
                return result;
            }
            if (progress != null && DateTime.UtcNow - last > TimeSpan.FromSeconds(5)) { Console.WriteLine($"  {DateTime.Now:HH:mm:ss} {progress()}"); last = DateTime.UtcNow; }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"API call {callbackId} did not complete in 30 minutes");
    }

    // ---- what everybody else sees ----

    static int Public(ulong id)
    {
        var (code, line) = PublicNow(id);
        Console.WriteLine(line);
        return code;
    }

    /// <summary>Whether Steam shows the item to everybody: an anonymous visit to its Workshop page, as a stranger's browser makes it.
    /// Steam's error page means not (yet): it is what everybody but the owner gets while the item or its newest version awaits moderation,
    /// and then other players' games fail its download with ERROR 15. The Web API is no help there - it reports such an item as public.
    /// Since Steam's new Workshop pages (seen 2026-10-31) that error page is an HTTP 500 titled just "Steam Workshop" - for an item held
    /// for moderation exactly as for an id that does not exist; it was a 200 titled "Steam Community :: Error" on 2026-10-07.
    /// 0 = shown to everybody, 1 = not shown, 2 = no verdict (rate limited, no answer, an unexpected page).</summary>
    static (int Code, string Line) PublicNow(ulong id)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            using var response = http.GetAsync($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}").GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.InternalServerError)
                return (1, $"{id}: NOT shown to everybody - Steam's error page for a visitor, HTTP 500 (awaiting moderation, private, or gone)");
            if (!response.IsSuccessStatusCode) return (2, $"{id}: no verdict - HTTP {(int)response.StatusCode} (429 = Steam rate-limits this address)");
            var html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var title = Regex.Match(html, "<title>([^<]*)</title>").Groups[1].Value;
            if (html.Contains("<title>Steam Community :: Error</title>")) return (1, $"{id}: NOT shown to everybody - Steam's Error page for a visitor (awaiting moderation, private, or gone)");
            if (title.StartsWith("Steam Workshop::")) return (0, $"{id}: shown to everybody - '{WebUtility.HtmlDecode(title)}'");
            return (2, $"{id}: no verdict - an unexpected page ('{title}')");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (2, $"{id}: no verdict - {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>The Web API's view (anonymous GetPublishedFileDetails): not the moderation signal, but it tells a ban or a removal.</summary>
    static string ApiState(ulong id, out bool banned)
    {
        banned = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var form = new FormUrlEncodedContent([new("itemcount", "1"), new("publishedfileids[0]", id.ToString(CultureInfo.InvariantCulture))]);
            using var r = http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", form).GetAwaiter().GetResult();
            if (!r.IsSuccessStatusCode) return $"HTTP {(int)r.StatusCode}";
            using var doc = JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var e = doc.RootElement.GetProperty("response").GetProperty("publishedfiledetails")[0];
            string Field(string n) => e.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "-";
            banned = Field("banned") is not ("0" or "-" or "false");
            return $"result {Field("result")}, visibility {Field("visibility")}, banned {Field("banned")}" +
                (Field("ban_reason") is { Length: > 0 } and not "-" and var why ? $" ({why})" : "") + $", file size {Field("file_size")}";
        }
        catch (Exception x) { return $"no answer ({x.GetType().Name}: {x.Message})"; }
    }

    /// <summary>The moderation watcher: the anonymous visit every <paramref name="seconds"/> until the page shows the item, then exit 0.
    /// A 429 or no answer is no verdict and stretches the wait (up to 3 minutes); a ban stops it (exit 4); <paramref name="minutes"/>
    /// without the item shown, exit 1.</summary>
    static int Watch(ulong id, int seconds, int minutes)
    {
        var start = DateTime.Now;
        var deadline = start.AddMinutes(minutes);
        var misses = 0;
        Console.WriteLine($"{start:HH:mm:ss} watching {id}: an anonymous visit to its Workshop page every {seconds} s, for at most {minutes} min");
        while (true)
        {
            var (code, line) = PublicNow(id);
            var api = ApiState(id, out var banned);
            var now = DateTime.Now;
            Console.WriteLine($"{now:HH:mm:ss} +{(now - start).TotalMinutes:0.0} min  {line}  |  Web API: {api}");
            if (code == 0)
            {
                Console.WriteLine($"EVERYBODY CAN SEE IT: {id} shown to an anonymous visitor at {now:HH:mm:ss}, {(now - start).TotalMinutes:0.0} min after the watch began");
                return 0;
            }
            if (banned) { Console.WriteLine($"STOPPED: the Web API says {id} is banned - {api}"); return 4; }
            if (now >= deadline) { Console.WriteLine($"GAVE UP at {now:HH:mm:ss}: {id} was not shown to everybody within {minutes} min"); return 1; }
            misses = code == 2 ? misses + 1 : 0;
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(Math.Max(seconds, 180), seconds * (1 + misses)))); // a 429 stretches the wait, to 3 min at most
        }
    }
}


/// <summary>The server's site in a Workshop description: the placeholder host overstrike.example.com stands for SITE_URL's host and
/// is filled in at upload time, so the real address stays out of the repository. An upload is refused while it is unfilled.</summary>
static class Site
{
    public const string Placeholder = "overstrike.example.com";

    public static string Fill(string text)
    {
        var host = (Environment.GetEnvironmentVariable("SITE_URL") ?? "").Trim().Split("://")[^1].TrimEnd('/');
        return host.Length > 0 ? text.Replace(Placeholder, host) : text;
    }
}

sealed class Refusal(string message) : Exception(message);

/// <summary>What the Workshop shows for one map: workshop.json in its folder, checked against Steam's limits before anything is sent.</summary>
sealed record Item(string Title, string Description, string[] Tags, string Map)
{
    public static Item Load(string mapDir)
    {
        var path = Path.Combine(mapDir, "workshop.json");
        if (!File.Exists(path)) throw new Refusal($"no {path} (title, description, tags, map)");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var tags = root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray() : [];
        var item = new Item(Str("title"), Site.Fill(Str("description")), tags,
            Str("map") is { Length: > 0 } m ? m : Path.GetFileName(Path.GetFullPath(mapDir).TrimEnd('\\', '/')));
        var why = item.Title.Trim() == "" ? "no title"
            : Encoding.UTF8.GetByteCount(item.Title) > 128 ? "a title over 128 bytes"
            : Encoding.UTF8.GetByteCount(item.Description) > 7999 ? "a description over 8,000 bytes"
            : item.Description.Contains(Site.Placeholder) ? $"a description naming the placeholder site {Site.Placeholder} (set SITE_URL)"
            : item.Tags.Length == 0 ? "no tags"
            : item.Tags.FirstOrDefault(x => x.Trim() == "" || x.Contains(',') || x.Length > 255) is { } bad ? $"a bad tag '{bad}'"
            : item.Tags.Sum(x => Encoding.UTF8.GetByteCount(x) + 1) > 1024 ? "tags over 1,024 bytes together"
            : !Regex.IsMatch(item.Map, "^[a-z0-9_]+$") ? $"a map name '{item.Map}' that is not lower-case letters, digits and _"
            : null;
        if (why != null) throw new Refusal($"{path} has {why}");
        Console.WriteLine($"item: '{item.Title}', map {item.Map}, tags {string.Join(", ", item.Tags)}, description {item.Description.Length} characters");
        return item;
    }
}

/// <summary>SteamUGCDetails_t (VALVE_CALLBACK_PACK_LARGE), the fields this tool prints.</summary>
sealed class Details(byte[] b)
{
    string Str(int off, int max) { var e = Array.IndexOf(b, (byte)0, off, max); return Encoding.UTF8.GetString(b, off, e >= 0 ? e - off : max); }
    public ulong Id => BitConverter.ToUInt64(b, 0);
    public int Result => BitConverter.ToInt32(b, 8);
    public int Type => BitConverter.ToInt32(b, 12);
    public uint CreatorApp => BitConverter.ToUInt32(b, 16);
    public uint ConsumerApp => BitConverter.ToUInt32(b, 20);
    public string Title => Str(24, 129);
    public ulong Owner => BitConverter.ToUInt64(b, 8160);
    public uint Created => BitConverter.ToUInt32(b, 8168);
    public uint Updated => BitConverter.ToUInt32(b, 8172);
    public int Visibility => BitConverter.ToInt32(b, 8180);
    public byte Banned => b[8184];
    public byte Accepted => b[8185];
    public string Tags => Str(8187, 1025);
    public string FileName => Str(9232, 260);
    public int FileBytes => BitConverter.ToInt32(b, 9492);
    public int PreviewBytes => BitConverter.ToInt32(b, 9496);
    public ulong TotalBytes => BitConverter.ToUInt64(b, 9776);
}

// Steam callback structs, VALVE_CALLBACK_PACK_LARGE (8) on Windows; GetAPICallResult wants their exact sizes.
[StructLayout(LayoutKind.Explicit, Size = 280)]
struct QueryCompleted { [FieldOffset(0)] public ulong Handle; [FieldOffset(8)] public int Result; [FieldOffset(12)] public uint Returned; [FieldOffset(16)] public uint Total; [FieldOffset(20)] public byte Cached; }

[StructLayout(LayoutKind.Explicit, Size = 24)]
struct CreateItemResult { [FieldOffset(0)] public int Result; [FieldOffset(8)] public ulong Id; [FieldOffset(16)] public byte NeedsLegal; }

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
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_CreateItem(IntPtr ugc, uint consumerApp, int fileType);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_StartItemUpdate(IntPtr ugc, uint app, ulong id);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemTitle(IntPtr ugc, ulong h, byte[] s);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemDescription(IntPtr ugc, ulong h, byte[] s);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemTags(IntPtr ugc, ulong h, IntPtr tags, [MarshalAs(UnmanagedType.I1)] bool allowAdminTags);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemContent(IntPtr ugc, ulong h, byte[] s);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemPreview(IntPtr ugc, ulong h, byte[] s);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_SetItemVisibility(IntPtr ugc, ulong h, int visibility);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_SubmitItemUpdate(IntPtr ugc, ulong h, byte[] note);
    [DllImport(Dll)] public static extern int SteamAPI_ISteamUGC_GetItemUpdateProgress(IntPtr ugc, ulong h, out ulong done, out ulong total);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest(IntPtr ugc, ulong[] ids, uint n);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_CreateQueryUserUGCRequest(IntPtr ugc, uint account, int list, int matching, int sort, uint creatorApp, uint consumerApp, uint page);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_SendQueryUGCRequest(IntPtr ugc, ulong q);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_GetQueryUGCResult(IntPtr ugc, ulong q, uint index, IntPtr details);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(IntPtr ugc, ulong q);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr utils, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr utils, ulong call, IntPtr buf, int size, int expected, [MarshalAs(UnmanagedType.I1)] out bool failed);
}

/// <summary>A VPK 2 with its data embedded, laid out as the HUD publisher writes it (and the Workshop Manager did until 2025-02): tree,
/// data, 1 MB chunk MD5s (archive index 0x80000000), the 48-byte checksum block (tree, chunk table, whole file) and a 20-byte empty
/// signature record. Unlike the HUD's, nested VPKs are packed (a map is maps/&lt;map&gt;.vpk); the tools' readonly_tools_asset_info.bin
/// and VPKs lying in the addon's top folder are not.</summary>
static class Vpk
{
    public const uint Signature = 0x55AA1234;

    static bool Packed(string rel) =>
        !Path.GetFileName(rel).Equals("readonly_tools_asset_info.bin", StringComparison.OrdinalIgnoreCase)
        && !(rel.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) && !rel.Contains('/'));

    public static List<(string Path, long Bytes)> Write(string root, string outPath)
    {
        root = Path.GetFullPath(root);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(Packed)
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        var data = new MemoryStream();
        var packed = new List<(string, long)>();
        var byExt = new SortedDictionary<string, SortedDictionary<string, List<(string Name, uint Crc, uint Off, uint Len)>>>(StringComparer.Ordinal);
        foreach (var rel in files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, rel));
            if (data.Length + bytes.Length > uint.MaxValue) throw new InvalidOperationException("over 4 GB: a single-file VPK cannot hold it");
            var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') is { Length: > 0 } d ? d : " ";
            var file = Path.GetFileName(rel);
            var dot = file.LastIndexOf('.');
            var (name, ext) = dot > 0 ? (file[..dot], file[(dot + 1)..]) : (file, " ");
            if (!byExt.TryGetValue(ext, out var paths)) byExt[ext] = paths = new(StringComparer.Ordinal);
            if (!paths.TryGetValue(dir, out var list)) paths[dir] = list = new();
            list.Add((name, Crc32(bytes), (uint)data.Position, (uint)bytes.Length));
            data.Write(bytes);
            packed.Add((rel, bytes.Length));
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
        using var outFile = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        void Out(ReadOnlySpan<byte> s) { outFile.Write(s); md5.AppendData(s); }
        foreach (var v in new uint[] { Signature, 2, (uint)treeBytes.Length, (uint)dataBytes.Length, (uint)chunkBytes.Length, 48, 20 })
            Out(BitConverter.GetBytes(v));
        Out(treeBytes);
        Out(dataBytes);
        Out(chunkBytes);
        Out(MD5.HashData(treeBytes));
        Out(MD5.HashData(chunkBytes));
        outFile.Write(md5.GetHashAndReset());                    // everything so far: header, tree, data, chunks, first 32 checksum bytes
        outFile.Write(BitConverter.GetBytes(Signature));
        outFile.Write(BitConverter.GetBytes(1u));
        outFile.Write(new byte[12]);
        return packed;
    }

    /// <summary>Reads a VPK this class wrote and lists what is wrong with it: the sizes, every file's CRC, every chunk MD5, the three
    /// checksums, and that <paramref name="mustHave"/> is inside and is itself a VPK.</summary>
    public static List<string> Check(string path, string mustHave)
    {
        var problems = new List<string>();
        var b = File.ReadAllBytes(path);
        uint U32(int o) => BitConverter.ToUInt32(b, o);
        if (b.Length < 28 || U32(0) != Signature || U32(4) != 2) return ["not a VPK 2"];
        int tree = (int)U32(8), data = (int)U32(12), chunks = (int)U32(16), sums = (int)U32(20), sig = (int)U32(24);
        int treeStart = 28, dataStart = treeStart + tree, chunkStart = dataStart + data, sumStart = chunkStart + chunks;
        if (sums != 48 || (long)sumStart + sums + sig != b.Length) return [$"sizes do not add up: {sumStart + sums + sig} != {b.Length}"];
        var entries = new Dictionary<string, (int Off, int Len)>(StringComparer.OrdinalIgnoreCase);
        var o = treeStart;
        string Str() { var e = Array.IndexOf(b, (byte)0, o); var s = Encoding.UTF8.GetString(b, o, e - o); o = e + 1; return s; }
        for (var ext = Str(); ext != ""; ext = Str())
            for (var dir = Str(); dir != ""; dir = Str())
                for (var name = Str(); name != ""; name = Str())
                {
                    var crc = U32(o);
                    var (preload, arch, off, len, end) = (BitConverter.ToUInt16(b, o + 4), BitConverter.ToUInt16(b, o + 6), (int)U32(o + 8), (int)U32(o + 12), BitConverter.ToUInt16(b, o + 16));
                    o += 18 + preload;
                    var full = (dir.Trim() == "" ? "" : dir + "/") + name + (ext.Trim() == "" ? "" : "." + ext);
                    if (preload != 0 || arch != 0x7FFF || end != 0xFFFF) { problems.Add($"{full}: not stored as written"); continue; }
                    if (off < 0 || len < 0 || (long)off + len > data) { problems.Add($"{full}: outside the data"); continue; }
                    if (Crc32(b.AsSpan(dataStart + off, len)) != crc) problems.Add($"{full}: CRC differs");
                    entries[full] = (off, len);
                }
        if (o != dataStart) problems.Add($"the tree ends at {o}, not at {dataStart}");
        for (var c = chunkStart; c + 28 <= chunkStart + chunks; c += 28)
        {
            var (off, len) = ((int)U32(c + 4), (int)U32(c + 8));
            if (U32(c) != 0x80000000 || (long)off + len > data || !MD5.HashData(b.AsSpan(dataStart + off, len)).AsSpan().SequenceEqual(b.AsSpan(c + 12, 16)))
                problems.Add($"chunk at {off}: MD5 differs");
        }
        if (!MD5.HashData(b.AsSpan(treeStart, tree)).AsSpan().SequenceEqual(b.AsSpan(sumStart, 16))) problems.Add("tree MD5 differs");
        if (!MD5.HashData(b.AsSpan(chunkStart, chunks)).AsSpan().SequenceEqual(b.AsSpan(sumStart + 16, 16))) problems.Add("chunk table MD5 differs");
        if (!MD5.HashData(b.AsSpan(0, sumStart + 32)).AsSpan().SequenceEqual(b.AsSpan(sumStart + 32, 16))) problems.Add("whole-file MD5 differs");
        if (!entries.TryGetValue(mustHave, out var map)) problems.Add($"{mustHave} is not inside");
        else if (map.Len < 4 || BitConverter.ToUInt32(b, dataStart + map.Off) != Signature) problems.Add($"{mustHave} inside is not a VPK");
        return problems;
    }

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var x in bytes) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
