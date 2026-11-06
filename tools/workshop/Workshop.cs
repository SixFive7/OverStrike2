// What our two Workshop publishers share (hud\publisher, maps\publisher), compiled into each as source: the Steamworks UGC calls through CS2's own
// steam_api64.dll (attached to the Steam client that runs and is logged in on this PC: no password is ever seen), a session as app 730, waiting for
// a call's result, and the VPK the Workshop Manager uploads. Nothing here creates an item: the maps publisher declares CreateItem and the other
// calls only it makes in its own half of the partial class Steam, so the HUD publisher has no create path at all (AGENTS.md, "cs2 in-game HUD -
// NEVER create another Workshop item").
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

static class Workshop
{
    /// <summary>Counter-Strike 2, the app the items belong to.</summary>
    public const uint App = 730;

    /// <summary>One Steam session as app 730 through the running client; the body gets ISteamUGC, ISteamUtils and the account's SteamID. 1 when Steam
    /// cannot be reached.</summary>
    public static int Session(Func<IntPtr, IntPtr, ulong, int> body)
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

    /// <summary>A string as the flat API takes it: UTF-8, NUL-terminated.</summary>
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>One setter's answer, said; a refusal stops the upload.</summary>
    public static void Check(string what, bool ok) { Console.WriteLine($"{what}: {(ok ? "ok" : "REFUSED")}"); if (!ok) throw new InvalidOperationException(what + " refused"); }

    /// <summary>A call's result, once Steam has it: callbacks run every 100 ms, the progress said every 5 s, for 30 minutes at the most.</summary>
    public static unsafe T Wait<T>(IntPtr utils, ulong call, int callbackId, Func<string>? progress = null) where T : unmanaged
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
}

// Steam callback structs, VALVE_CALLBACK_PACK_LARGE (8) on Windows.
[StructLayout(LayoutKind.Explicit, Size = 280)]
struct QueryCompleted { [FieldOffset(0)] public ulong Handle; [FieldOffset(8)] public int Result; [FieldOffset(12)] public uint Returned; [FieldOffset(16)] public uint Total; [FieldOffset(20)] public byte Cached; }

[StructLayout(LayoutKind.Explicit, Size = 16)]
struct SubmitItemUpdateResult { [FieldOffset(0)] public int Result; [FieldOffset(4)] public byte NeedsLegal; [FieldOffset(8)] public ulong Id; }

/// <summary>The flat API's calls both publishers make: a session, an update of an item that exists, a query of one. A publisher that may create an item
/// declares those calls in its own half.</summary>
static partial class Steam
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
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_SubmitItemUpdate(IntPtr ugc, ulong h, byte[] note);
    [DllImport(Dll)] public static extern int SteamAPI_ISteamUGC_GetItemUpdateProgress(IntPtr ugc, ulong h, out ulong done, out ulong total);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest(IntPtr ugc, ulong[] ids, uint n);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUGC_SendQueryUGCRequest(IntPtr ugc, ulong q);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_GetQueryUGCResult(IntPtr ugc, ulong q, uint index, IntPtr details);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(IntPtr ugc, ulong q);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr utils, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport(Dll)][return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr utils, ulong call, IntPtr buf, int size, int expected, [MarshalAs(UnmanagedType.I1)] out bool failed);
}

/// <summary>A VPK 2 with its data embedded, laid out like the Workshop Manager's: tree, data, 1 MB chunk MD5s (archive index 0x80000000), the 48-byte
/// checksum block (tree, chunk table, whole file) and a 20-byte empty signature record. Each publisher says which files of its addon folder go in.</summary>
static partial class Vpk
{
    public const uint Signature = 0x55AA1234;

    /// <summary>Packs every file under root that include lets through (its path relative to root, with '/'), in ordinal order; returns them with their sizes.</summary>
    public static List<(string Path, long Bytes)> Write(string root, string outPath, Func<string, bool> include)
    {
        root = Path.GetFullPath(root);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(include)
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

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var x in bytes) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
