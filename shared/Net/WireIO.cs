using System;
using System.Buffers.Binary;
using System.Text;

namespace StellarAllegiance.Shared.Net;

// Span cursors the generated codecs write through (tools/wire-gen). Little-endian, no allocation,
// no virtual calls: every method is a bounds-checked primitive write at a running offset — the
// same code the hand-written Protocol.cs writers used to inline. WireReader never throws on a
// short or hostile frame; it raises Failed and returns defaults, so the generated TryParse can
// reject a bad frame without exceptions on the server's receive path.
public ref struct WireWriter
{
    private readonly Span<byte> _buf;
    public int Pos;

    public WireWriter(Span<byte> buf)
    {
        _buf = buf;
        Pos = 0;
    }

    public void U8(byte v) => _buf[Pos++] = v;

    public void I8(sbyte v) => _buf[Pos++] = (byte)v;

    public void Bool(bool v) => _buf[Pos++] = v ? (byte)1 : (byte)0;

    public void I16(short v)
    {
        BinaryPrimitives.WriteInt16LittleEndian(_buf.Slice(Pos), v);
        Pos += 2;
    }

    public void U16(ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buf.Slice(Pos), v);
        Pos += 2;
    }

    public void I32(int v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buf.Slice(Pos), v);
        Pos += 4;
    }

    public void U32(uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.Slice(Pos), v);
        Pos += 4;
    }

    public void I64(long v)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_buf.Slice(Pos), v);
        Pos += 8;
    }

    public void U64(ulong v)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buf.Slice(Pos), v);
        Pos += 8;
    }

    public void F32(float v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_buf.Slice(Pos), v);
        Pos += 4;
    }

    public void F64(double v)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(_buf.Slice(Pos), v);
        Pos += 8;
    }

    public void Bytes(ReadOnlySpan<byte> b)
    {
        b.CopyTo(_buf.Slice(Pos));
        Pos += b.Length;
    }

    // u16 byte-length + UTF-8 (the historical Protocol.WriteString layout). A string longer than
    // 65535 UTF-8 bytes is a caller bug (it could never be framed), so it throws rather than wraps.
    public void Str(string? s)
    {
        var chars = (s ?? "").AsSpan();
        int n = Encoding.UTF8.GetByteCount(chars);
        if (n > ushort.MaxValue)
            throw new ArgumentException("wire string exceeds 65535 bytes");
        U16((ushort)n);
        Encoding.UTF8.GetBytes(chars, _buf.Slice(Pos, n));
        Pos += n;
    }

    // u8 byte-length + UTF-8 (the Hello frame's short fields).
    public void StrU8(string? s)
    {
        var chars = (s ?? "").AsSpan();
        int n = Encoding.UTF8.GetByteCount(chars);
        if (n > byte.MaxValue)
            throw new ArgumentException("wire string exceeds 255 bytes");
        U8((byte)n);
        Encoding.UTF8.GetBytes(chars, _buf.Slice(Pos, n));
        Pos += n;
    }

    // BinaryWriter.Write(string) layout: 7-bit-encoded byte length + UTF-8 (the sector-name field).
    public void Str7(string? s)
    {
        var chars = (s ?? "").AsSpan();
        int n = Encoding.UTF8.GetByteCount(chars);
        uint v = (uint)n;
        while (v >= 0x80)
        {
            _buf[Pos++] = (byte)(v | 0x80);
            v >>= 7;
        }
        _buf[Pos++] = (byte)v;
        Encoding.UTF8.GetBytes(chars, _buf.Slice(Pos, n));
        Pos += n;
    }
}

public ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _buf;
    public int Pos;
    public bool Failed;

    public WireReader(ReadOnlySpan<byte> buf)
    {
        _buf = buf;
        Pos = 0;
        Failed = false;
    }

    public int Remaining => _buf.Length - Pos;

    public void Fail() => Failed = true;

    // Back up to a checkpoint and clear the failure — how an [WireOptional] tail that turned out to
    // be absent is treated as "not sent" rather than "malformed".
    public void Rewind(int pos)
    {
        Pos = pos;
        Failed = false;
    }

    private bool Need(int n)
    {
        if (Pos + n > _buf.Length)
        {
            Failed = true;
            return false;
        }
        return true;
    }

    public byte U8() => Need(1) ? _buf[Pos++] : (byte)0;

    public sbyte I8() => Need(1) ? (sbyte)_buf[Pos++] : (sbyte)0;

    public bool Bool() => Need(1) && _buf[Pos++] != 0;

    public short I16()
    {
        if (!Need(2))
            return 0;
        var v = BinaryPrimitives.ReadInt16LittleEndian(_buf.Slice(Pos));
        Pos += 2;
        return v;
    }

    public ushort U16()
    {
        if (!Need(2))
            return 0;
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_buf.Slice(Pos));
        Pos += 2;
        return v;
    }

    public int I32()
    {
        if (!Need(4))
            return 0;
        var v = BinaryPrimitives.ReadInt32LittleEndian(_buf.Slice(Pos));
        Pos += 4;
        return v;
    }

    public uint U32()
    {
        if (!Need(4))
            return 0;
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_buf.Slice(Pos));
        Pos += 4;
        return v;
    }

    public long I64()
    {
        if (!Need(8))
            return 0;
        var v = BinaryPrimitives.ReadInt64LittleEndian(_buf.Slice(Pos));
        Pos += 8;
        return v;
    }

    public ulong U64()
    {
        if (!Need(8))
            return 0;
        var v = BinaryPrimitives.ReadUInt64LittleEndian(_buf.Slice(Pos));
        Pos += 8;
        return v;
    }

    public float F32()
    {
        if (!Need(4))
            return 0f;
        var v = BinaryPrimitives.ReadSingleLittleEndian(_buf.Slice(Pos));
        Pos += 4;
        return v;
    }

    public double F64()
    {
        if (!Need(8))
            return 0d;
        var v = BinaryPrimitives.ReadDoubleLittleEndian(_buf.Slice(Pos));
        Pos += 8;
        return v;
    }

    public byte[] Bytes(int n)
    {
        if (n < 0 || !Need(n))
            return Array.Empty<byte>();
        var b = _buf.Slice(Pos, n).ToArray();
        Pos += n;
        return b;
    }

    private string Utf8(int n)
    {
        if (!Need(n))
            return "";
        var s = Encoding.UTF8.GetString(_buf.Slice(Pos, n));
        Pos += n;
        return s;
    }

    public string Str()
    {
        int n = U16();
        return Failed ? "" : Utf8(n);
    }

    public string StrU8()
    {
        int n = U8();
        return Failed ? "" : Utf8(n);
    }

    public string Str7()
    {
        // BinaryReader.Read7BitEncodedInt: up to 5 bytes, little-end first.
        uint n = 0;
        int shift = 0;
        while (true)
        {
            if (shift > 28)
            {
                Failed = true;
                return "";
            }
            byte b = U8();
            if (Failed)
                return "";
            n |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        return n > int.MaxValue ? Fail_() : Utf8((int)n);
    }

    private string Fail_()
    {
        Failed = true;
        return "";
    }
}

// Measure helpers the generated Measure() calls for the variable-size string encodings.
public static class WireIO
{
    public static int StrSize(string? s) => 2 + Encoding.UTF8.GetByteCount(s ?? "");

    public static int StrU8Size(string? s) => 1 + Encoding.UTF8.GetByteCount(s ?? "");

    public static int Str7Size(string? s)
    {
        int n = Encoding.UTF8.GetByteCount(s ?? "");
        int prefix = 1;
        for (uint v = (uint)n; v >= 0x80; v >>= 7)
            prefix++;
        return prefix + n;
    }
}
