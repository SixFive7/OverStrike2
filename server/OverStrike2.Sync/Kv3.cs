using System.Buffers.Binary;
using System.Text;

namespace OverStrike2.Sync;

/// <summary>Binary KV3, read: a compiled Source 2 resource's KV3 block as plain values - null, bool, long, ulong, double, string, byte[], an array
/// (List of values) and an object (Dictionary, its keys in order). Versions 1 to 5, uncompressed, LZ4 or Zstandard, binary blobs too. The reading
/// half of ValveResourceFormat's BinaryKV3, ported (MIT, Copyright (c) 2015 ValveResourceFormat Contributors; NOTICE, LICENSE-MIT): the same
/// buffers, alignments and types, with no writer and none of its object model. What it cannot read throws FormatException; the scan takes that
/// as nothing known.</summary>
public static class Kv3
{
    const uint Trailer = 0xFFEEDD00;

    enum T : byte
    {
        Null = 1, Boolean = 2, Int64 = 3, UInt64 = 4, Double = 5, String = 6, BinaryBlob = 7, Array = 8, Object = 9, ArrayTyped = 10, Int32 = 11,
        UInt32 = 12, True = 13, False = 14, Int64Zero = 15, Int64One = 16, DoubleZero = 17, DoubleOne = 18, Float = 19, Int16 = 20, UInt16 = 21,
        Unknown22 = 22, Int32AsByte = 23, ArrayTypeByteLength = 24, ArrayTypeAuxiliaryBuffer = 25, UInt32Zero = 26, UInt32One = 27,
    }

    /// <summary>A region of a decompressed buffer, read from the front.</summary>
    sealed class Region(byte[] data, int start, int length)
    {
        int at = start;
        readonly int end = start + length;

        public int Left => end - at;

        public ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > Left) throw new FormatException("KV3: a value past its buffer's end");
            var s = data.AsSpan(at, n);
            at += n;
            return s;
        }

        public byte Byte() => Take(1)[0];
        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public string CString()
        {
            var rest = data.AsSpan(at, Left);
            var zero = rest.IndexOf((byte)0);
            if (zero < 0) throw new FormatException("KV3: a string without its end");
            var s = Encoding.UTF8.GetString(rest[..zero]);
            at += zero + 1;
            return s;
        }
    }

    sealed class Buffers
    {
        public Region B1 = Empty, B2 = Empty, B4 = Empty, B8 = Empty;
    }

    static readonly Region Empty = new([], 0, 0);

    sealed class Context
    {
        public int Version;
        public long Values, MaxValues;
        public string[] Strings = [];
        public Buffers Buffer = new(), Auxiliary = new();
        public Region Types = Empty, ObjectLengths = Empty, BlobLengths = Empty, Blobs = Empty;
    }

    /// <summary>The most values one block may hold: an entity lump holds a few hundred thousand. A typed array of constants takes no bytes per value, so
    /// without it a block of a few bytes could ask for billions.</summary>
    public const long MaxValues = 16_000_000;

    // the constants, boxed once: an array of them costs a reference per value
    static readonly object True = true, False = false, Zero = 0L, One = 1L, DoubleZero = 0.0, DoubleOne = 1.0;

    /// <summary>The value a KV3 block holds (its root), out of the block's bytes - the magic first. FormatException: not one this can read, or one of more
    /// than maxValues values.</summary>
    public static object? Read(byte[] block, long limit = 256L << 20, long maxValues = MaxValues)
    {
        if (block.Length < 4) throw new FormatException("KV3: too short");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(block);
        if (magic == 0x03564B56) throw new FormatException("KV3: the legacy VKV3 form is not read");
        if ((magic & 0xFFFFFF00) != 0x4B563300 || (magic & 0xFF) is < 1 or > 5) throw new FormatException($"KV3: not a KV3 block (0x{magic:X8})");
        return ReadBuffer((int)(magic & 0xFF), new Region(block, 4, block.Length - 4), limit, maxValues);
    }

    static object? ReadBuffer(int version, Region r, long limit, long maxValues)
    {
        var cx = new Context { Version = version, MaxValues = maxValues };
        r.Take(16); // the format's GUID
        var method = r.U32();
        if (method > 2) throw new FormatException($"KV3: compression {method}");
        int dictionary = 0, frame = 0, countBytes1, countBytes4, countBytes8, countTypes = 0, sizeUncompressedTotal, sizeCompressedTotal, countBlocks = 0, sizeBlobs = 0;
        if (version == 1)
        {
            countBytes1 = r.I32();
            countBytes4 = r.I32();
            countBytes8 = r.I32();
            sizeUncompressedTotal = r.I32();
            sizeCompressedTotal = r.Left;
        }
        else
        {
            dictionary = r.U16();
            frame = r.U16();
            countBytes1 = r.I32();
            countBytes4 = r.I32();
            countBytes8 = r.I32();
            countTypes = r.I32();
            r.U16(); // countObjects
            r.U16(); // countArrays
            sizeUncompressedTotal = r.I32();
            sizeCompressedTotal = r.I32();
            countBlocks = r.I32();
            sizeBlobs = r.I32();
        }
        if (dictionary != 0) throw new FormatException("KV3: a compression dictionary");
        int countBytes2 = 0;
        if (version >= 4)
        {
            countBytes2 = r.I32();
            r.I32(); // sizeBlockCompressedSizesBytes
        }
        int sizeU1, sizeC1, sizeU2 = 0, sizeC2 = 0, b1x2 = 0, b2x2 = 0, b4x2 = 0, b8x2 = 0, objects2 = 0;
        if (version >= 5)
        {
            sizeU1 = r.I32();
            sizeC1 = r.I32();
            sizeU2 = r.I32();
            sizeC2 = r.I32();
            b1x2 = r.I32();
            b2x2 = r.I32();
            b4x2 = r.I32();
            b8x2 = r.I32();
            r.I32(); // unknown
            objects2 = r.I32();
            r.I32(); // countArrays_buffer2
            r.I32(); // unknown
        }
        else
        {
            (sizeU1, sizeC1) = (sizeUncompressedTotal, sizeCompressedTotal);
        }
        if (sizeU1 < 0 || sizeU2 < 0 || sizeBlobs < 0 || (long)sizeU1 + sizeU2 + sizeBlobs > limit) throw new FormatException("KV3: sizes out of bounds");

        // buffer 1 - before version 5 everything; from 5 the strings and the auxiliary buffer
        var outLength1 = version < 5 && method == 2 ? sizeU1 + sizeBlobs : sizeU1;
        var b1 = Inflate(method, r, sizeC1, outLength1, frame, version);
        Region? withBlobSizes = null;
        var at = 0;
        var buffer1 = new Buffers();
        Region Slice(ref int offset, int count, int size, int align)
        {
            if (count <= 0) return Empty;
            offset = Align(offset, align);
            var region = new Region(b1, offset, count * size);
            if (offset + count * size > sizeU1) throw new FormatException("KV3: buffer 1 too short");
            offset += count * size;
            return region;
        }
        buffer1.B1 = Slice(ref at, countBytes1, 1, 1);
        buffer1.B2 = Slice(ref at, countBytes2, 2, 2);
        buffer1.B4 = Slice(ref at, countBytes4, 4, 4);
        buffer1.B8 = Slice(ref at, countBytes8, 8, 8);
        if (countBytes8 <= 0 && version < 5) at = Align(at, 8);
        if (countBytes4 <= 0) throw new FormatException("KV3: no strings count");
        var countStrings = buffer1.B4.I32();
        if (countStrings < 0 || countStrings > sizeU1) throw new FormatException("KV3: strings count");
        cx.Strings = new string[countStrings];
        if (version >= 5)
        {
            cx.Auxiliary = buffer1;
            for (var i = 0; i < countStrings; i++) cx.Strings[i] = buffer1.B1.CString();
        }
        else
        {
            cx.Buffer = buffer1;
            var strings = new Region(b1, at, sizeU1 - at);
            var stringsStart = at;
            for (var i = 0; i < countStrings; i++)
            {
                var before = strings.Left;
                cx.Strings[i] = strings.CString();
                at += before - strings.Left;
            }
            var typesLength = version == 1 ? sizeUncompressedTotal - at - 4 : countTypes - at + stringsStart;
            if (typesLength < 0 || at + typesLength > sizeU1) throw new FormatException("KV3: types out of bounds");
            cx.Types = new Region(b1, at, typesLength);
            at += typesLength;
            if (countBlocks == 0)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(b1.AsSpan(at, 4)) != Trailer) throw new FormatException("KV3: no trailer");
            }
            else withBlobSizes = new Region(b1, at, sizeU1 - at);
        }

        // buffer 2 (version 5): the values, the object lengths and the types
        if (version >= 5)
        {
            var b2 = Inflate(method, r, sizeC2, sizeU2, frame, version);
            var buffer2 = new Buffers();
            cx.Buffer = buffer2;
            var offset = objects2 * 4;
            if (offset > sizeU2) throw new FormatException("KV3: object lengths out of bounds");
            cx.ObjectLengths = new Region(b2, 0, offset);
            Region Slice2(ref int o, int count, int size, int align)
            {
                if (count <= 0) return Empty;
                o = Align(o, align);
                if (o + count * size > sizeU2) throw new FormatException("KV3: buffer 2 too short");
                var region = new Region(b2, o, count * size);
                o += count * size;
                return region;
            }
            buffer2.B1 = Slice2(ref offset, b1x2, 1, 1);
            buffer2.B2 = Slice2(ref offset, b2x2, 2, 2);
            buffer2.B4 = Slice2(ref offset, b4x2, 4, 4);
            buffer2.B8 = Slice2(ref offset, b8x2, 8, 8);
            if (offset + countTypes > sizeU2) throw new FormatException("KV3: types out of bounds");
            cx.Types = new Region(b2, offset, countTypes);
            offset += countTypes;
            if (countBlocks == 0)
            {
                if (offset + 4 > sizeU2 || BinaryPrimitives.ReadUInt32LittleEndian(b2.AsSpan(offset, 4)) != Trailer) throw new FormatException("KV3: no trailer");
            }
            else withBlobSizes = new Region(b2, offset, sizeU2 - offset);
        }

        // the binary blobs
        if (countBlocks > 0)
        {
            if (withBlobSizes == null || countBlocks < 0) throw new FormatException("KV3: blob sizes");
            var lengths = withBlobSizes.Take(countBlocks * 4).ToArray();
            cx.BlobLengths = new Region(lengths, 0, lengths.Length);
            if (withBlobSizes.U32() != Trailer) throw new FormatException("KV3: no trailer after the blob sizes");
            byte[] blobs;
            if (method == 0) blobs = r.Take(sizeBlobs).ToArray();
            else if (method == 1)
            {
                var compressed = new List<(int Length, byte[] Data)>();
                while (withBlobSizes.Left >= 2)
                {
                    var n = withBlobSizes.U16();
                    compressed.Add((n, r.Take(n).ToArray()));
                }
                blobs = Lz4Chain(compressed, frame, sizeBlobs);
            }
            else if (version >= 5)
            {
                var size = sizeCompressedTotal - sizeC1 - sizeC2;
                blobs = Zstd(r.Take(size).ToArray(), sizeBlobs);
            }
            else blobs = b1.AsSpan(sizeU1, sizeBlobs).ToArray(); // before 5, Zstandard took the blobs along with buffer 1
            cx.Blobs = new Region(blobs, 0, blobs.Length);
            if (r.U32() != Trailer) throw new FormatException("KV3: no trailer after the blobs");
        }

        var (type, _) = ReadType(cx);
        return Value(cx, type, 0);
    }

    static int Align(int offset, int alignment) => (offset + alignment - 1) & ~(alignment - 1);

    /// <summary>One compressed buffer, inflated to exactly `length` bytes.</summary>
    static byte[] Inflate(uint method, Region r, int compressed, int length, int frame, int version)
    {
        if (method == 0) return r.Take(length).ToArray();
        var input = r.Take(compressed).ToArray();
        if (method == 1)
        {
            var output = Scan.Lz4Block(input, length);
            if (output.Length != length) throw new FormatException($"KV3: LZ4 gave {output.Length} bytes, not {length}");
            return output;
        }
        return Zstd(input, length);
    }

    static byte[] Zstd(byte[] input, int length)
    {
        using var d = new ZstdSharp.Decompressor();
        var output = new byte[length];
        if (!d.TryUnwrap(input, output, out var written) || written != length) throw new FormatException($"KV3: Zstandard gave {written} bytes, not {length}");
        return output;
    }

    /// <summary>LZ4 blocks chained: each decodes after the output so far, which its matches may reach back into - of at most `frame` bytes each.</summary>
    static byte[] Lz4Chain(List<(int Length, byte[] Data)> blocks, int frame, int total)
    {
        var dst = new List<byte>(total);
        foreach (var (_, src) in blocks)
        {
            var before = dst.Count;
            int i = 0, n = src.Length;
            while (i < n)
            {
                var token = src[i++];
                var lit = token >> 4;
                if (lit == 15) { byte b; do { b = src[i++]; lit += b; } while (b == 255); }
                if (i + lit > n) throw new FormatException("KV3: an LZ4 block cut short");
                dst.AddRange(src.AsSpan(i, lit).ToArray());
                i += lit;
                if (i >= n) break;
                var off = src[i] | (src[i + 1] << 8);
                i += 2;
                if (off == 0 || off > dst.Count) throw new FormatException("KV3: a bad LZ4 offset");
                var ml = token & 15;
                if (ml == 15) { byte b; do { b = src[i++]; ml += b; } while (b == 255); }
                ml += 4;
                var start = dst.Count - off;
                for (var k = 0; k < ml; k++) dst.Add(dst[start + k]);
            }
            if (dst.Count - before > Math.Max(frame, 1) || dst.Count > total) throw new FormatException("KV3: an LZ4 block too long");
        }
        if (dst.Count != total) throw new FormatException($"KV3: the blobs gave {dst.Count} bytes, not {total}");
        return dst.ToArray();
    }

    static (T Type, byte Flag) ReadType(Context cx)
    {
        var b = cx.Types.Byte();
        byte flag = 0;
        if (cx.Version >= 3)
        {
            if ((b & 0x80) != 0) { b &= 0x3F; flag = cx.Types.Byte(); }
        }
        else if ((b & 0x80) != 0) { b &= 0x7F; flag = cx.Types.Byte(); }
        return ((T)b, flag);
    }

    static object? Value(Context cx, T type, int depth)
    {
        if (depth > 64) throw new FormatException("KV3: nested too deep");
        if (++cx.Values > cx.MaxValues) throw new FormatException($"KV3: more than {cx.MaxValues} values");
        var buf = cx.Buffer;
        switch (type)
        {
            case T.Null: return null;
            case T.True: return True;
            case T.False: return False;
            case T.Int64Zero: return Zero;
            case T.Int64One: return One;
            case T.DoubleZero: return DoubleZero;
            case T.DoubleOne: return DoubleOne;
            case T.UInt32Zero: return Zero;
            case T.UInt32One: return One;
            case T.Boolean: return buf.B1.Byte() == 1;
            case T.Int32AsByte: return (long)buf.B1.Byte();
            case T.Int16: return (long)buf.B2.I16();
            case T.UInt16: return (long)buf.B2.U16();
            case T.Int32: return (long)buf.B4.I32();
            case T.UInt32: return (long)buf.B4.U32();
            case T.Float: return (double)buf.B4.F32();
            case T.Int64: return buf.B8.I64();
            case T.UInt64: return buf.B8.U64();
            case T.Double: return buf.B8.F64();
            case T.String:
            {
                var id = buf.B4.I32();
                if (id == -1) return "";
                if (id < 0 || id >= cx.Strings.Length) throw new FormatException("KV3: a string number out of range");
                return cx.Strings[id];
            }
            case T.BinaryBlob when cx.Version < 2:
            {
                var length = buf.B4.I32();
                return length > 0 ? buf.B1.Take(length).ToArray() : [];
            }
            case T.BinaryBlob:
            {
                var length = cx.BlobLengths.I32();
                return length > 0 ? cx.Blobs.Take(length).ToArray() : [];
            }
            case T.Array:
            {
                var count = buf.B4.I32();
                if (count < 0) throw new FormatException("KV3: an array's length");
                var list = new List<object?>(Math.Min(count, 4096));
                for (var i = 0; i < count; i++)
                {
                    var (t, _) = ReadType(cx);
                    list.Add(Value(cx, t, depth + 1));
                }
                return list;
            }
            case T.ArrayTyped or T.ArrayTypeByteLength:
            {
                var count = type == T.ArrayTypeByteLength ? buf.B1.Byte() : buf.B4.I32();
                if (count < 0) throw new FormatException("KV3: an array's length");
                var (t, _) = ReadType(cx);
                var list = new List<object?>(Math.Min(count, 4096));
                for (var i = 0; i < count; i++) list.Add(Value(cx, t, depth + 1));
                return list;
            }
            case T.ArrayTypeAuxiliaryBuffer:
            {
                var count = buf.B1.Byte();
                var (t, _) = ReadType(cx);
                var list = new List<object?>(count);
                (cx.Auxiliary, cx.Buffer) = (cx.Buffer, cx.Auxiliary);
                try { for (var i = 0; i < count; i++) list.Add(Value(cx, t, depth + 1)); }
                finally { (cx.Auxiliary, cx.Buffer) = (cx.Buffer, cx.Auxiliary); }
                return list;
            }
            case T.Object:
            {
                var count = cx.Version >= 5 ? cx.ObjectLengths.I32() : buf.B4.I32();
                if (count < 0) throw new FormatException("KV3: an object's length");
                var obj = new Dictionary<string, object?>(Math.Min(count, 4096), StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var (t, _) = ReadType(cx);
                    var id = cx.Buffer.B4.I32();
                    var name = id == -1 ? "" : id >= 0 && id < cx.Strings.Length ? cx.Strings[id] : throw new FormatException("KV3: a key number out of range");
                    obj[name] = Value(cx, t, depth + 1);
                }
                return obj;
            }
            default:
                throw new FormatException($"KV3: type {(int)type}");
        }
    }
}
