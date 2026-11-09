using System.Buffers.Binary;
using System.Text;

namespace OverStrike2.Sync;

/// <summary>A map's team spawn points, out of its entity lumps (.vents_c): how many a side Wingman takes there (the bridge's Modes.WingmanFits). A side is held at
/// its spawn points in the game ("The Terrorist team is full"), so a Wingman map with 2 a side takes 4 players. Counted as CS2 places players in Wingman: the
/// map's "spawnpoints.2v2" set where it has one (Valve's de_inferno, de_nuke and de_overpass: 4 a side, started disabled - the mode switches them on), else every
/// info_player_terrorist and info_player_counterterrorist that is enabled (no "enabled" false, no "startdisabled"). The entities come in two forms, as
/// ValveResourceFormat's EntityLump reads them (MIT; NOTICE, LICENSE-MIT): KV3 objects (keyValues3Data: values and attributes), and the older blob (m_keyValuesData) whose
/// keys are MurmurHash2 hashes of their lowercase names.</summary>
public static class Spawns
{
    public const string WingmanSet = "spawnpoints.2v2";

    /// <summary>The counts of one map: its T and CT spawn points as Wingman takes them, and how many of each kind the lumps held.</summary>
    public sealed record Count(int T, int Ct, int Entities, bool TwoVersusTwo);

    /// <summary>One lump's entities, each its keys (lowercase) and values. FormatException: a lump it cannot read.</summary>
    public static List<Dictionary<string, object?>> Entities(byte[] resource)
    {
        var data = DataBlock(resource) ?? throw new FormatException("entity lump: no DATA block");
        var root = Kv3.Read(data) as Dictionary<string, object?> ?? throw new FormatException("entity lump: its root is no object");
        var list = new List<Dictionary<string, object?>>();
        if (root.GetValueOrDefault("m_entityKeyValues") is not List<object?> entities) return list;
        foreach (var e in entities.OfType<Dictionary<string, object?>>())
        {
            var entity = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (e.GetValueOrDefault("keyValues3Data") is Dictionary<string, object?> kv3)
            {
                foreach (var part in new[] { "values", "attributes" })
                    if (kv3.GetValueOrDefault(part) is Dictionary<string, object?> values)
                        foreach (var (k, v) in values) entity[k.ToLowerInvariant()] = v;
            }
            else if (e.GetValueOrDefault("m_keyValuesData") is byte[] blob) Legacy(blob, entity);
            if (entity.ContainsKey("classname")) list.Add(entity);
        }
        return list;
    }

    /// <summary>A compiled resource's DATA block, or null.</summary>
    public static byte[]? DataBlock(byte[] resource)
    {
        if (resource.Length < 16) return null;
        var blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(resource.AsSpan(8));
        var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(resource.AsSpan(12));
        long pos = 8 + (long)blockOffset;
        for (var k = 0; k < Math.Min(blockCount, 32u); k++, pos += 12)
        {
            if (pos + 12 > resource.Length) return null;
            if (!resource.AsSpan((int)pos, 4).SequenceEqual("DATA"u8)) continue;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(resource.AsSpan((int)pos + 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(resource.AsSpan((int)pos + 8));
            var start = pos + 4 + offset;
            return start >= 0 && start + size <= resource.Length ? resource.AsSpan((int)start, (int)size).ToArray() : null;
        }
        return null;
    }

    static readonly Dictionary<uint, string> Known = new[] { "classname", "targetname", "enabled", "startdisabled" }.ToDictionary(k => Murmur2(k), k => k);

    /// <summary>The older entity form: version 1, its hashed fields, then its fields that also carry their names, each a type and a value.</summary>
    static void Legacy(byte[] blob, Dictionary<string, object?> entity)
    {
        var at = 0;
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at, 4)); at += 4; return v; }
        string CStr()
        {
            var z = blob.AsSpan(at).IndexOf((byte)0);
            if (z < 0) throw new FormatException("entity: a string without its end");
            var s = Encoding.UTF8.GetString(blob, at, z);
            at += z + 1;
            return s;
        }
        object? Typed()
        {
            var type = U32();
            switch (type)
            {
                case 0x06: { var b = blob[at] != 0; at += 1; return b; }                                                  // Boolean
                case 0x01: { var f = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(at)); at += 4; return (double)f; } // Float
                case 0x22: { var d = BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(at)); at += 8; return d; }          // Float64
                case 0x09: at += 4; return null;                                                                           // Color32
                case 0x05: { var i = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(at)); at += 4; return (long)i; }    // Integer
                case 0x25: { var u = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(at)); at += 4; return (long)u; }   // UInt
                case 0x1a or 0x21: at += 8; return null;                                                                   // Integer64, UInt64
                case 0x03 or 0x27: at += 12; return null;                                                                  // Vector, QAngle
                case 0x1e: return CStr();                                                                                  // CString
                default: throw new FormatException($"entity: field type {type}");
            }
        }
        if (U32() != 1) throw new FormatException("entity: not version 1");
        var hashed = U32();
        var named = U32();
        for (var i = 0; i < hashed; i++)
        {
            var hash = U32();
            var value = Typed();
            if (Known.TryGetValue(hash, out var key)) entity[key] = value;
        }
        for (var i = 0; i < named; i++)
        {
            U32(); // its hash
            var key = CStr().ToLowerInvariant();
            entity[key] = Typed();
        }
    }

    /// <summary>MurmurHash2, 32 bits, Valve's seed: how the older entity form names a key (its lowercase name).</summary>
    public static uint Murmur2(string key, uint seed = 0x31415926)
    {
        var data = Encoding.UTF8.GetBytes(key.ToLowerInvariant());
        const uint m = 0x5bd1e995;
        const int r = 24;
        var h = seed ^ (uint)data.Length;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
            k *= m; k ^= k >> r; k *= m;
            h *= m; h ^= k;
        }
        switch (data.Length - i)
        {
            case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h ^= data[i]; h *= m; break;
        }
        h ^= h >> 13; h *= m; h ^= h >> 15;
        return h;
    }

    static bool Truthy(object? v) => v switch
    {
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        string s => s is "1" or "true" or "True",
        _ => false,
    };

    /// <summary>A map's spawn points as Wingman takes them, out of the entities of all its lumps.</summary>
    public static Count Of(IReadOnlyCollection<Dictionary<string, object?>> entities)
    {
        static string Str(Dictionary<string, object?> e, string k) => e.GetValueOrDefault(k) as string ?? "";
        var team = entities.Where(e => Str(e, "classname").ToLowerInvariant() is "info_player_terrorist" or "info_player_counterterrorist").ToList();
        static bool IsT(Dictionary<string, object?> e) => Str(e, "classname").Equals("info_player_terrorist", StringComparison.OrdinalIgnoreCase);
        var set = team.Where(e => Str(e, "targetname").EndsWith(WingmanSet, StringComparison.OrdinalIgnoreCase)).ToList(); // a prefab's own names carry "[PR#]" in front
        if (set.Count > 0) return new Count(set.Count(IsT), set.Count(e => !IsT(e)), entities.Count, true);
        var on = team.Where(e => (!e.ContainsKey("enabled") || Truthy(e["enabled"])) && !(e.ContainsKey("startdisabled") && Truthy(e["startdisabled"]))).ToList();
        return new Count(on.Count(IsT), on.Count(e => !IsT(e)), entities.Count, false);
    }
}
