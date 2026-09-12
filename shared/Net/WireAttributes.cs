using System;

namespace StellarAllegiance.Shared.Net;

// The wire-format vocabulary read by the source generator in tools/wire-gen (docs/adr/0003).
//
// A frame or record is a PARTIAL struct/class whose public instance fields, in declaration order,
// ARE its byte layout. The generator emits Measure / Write / Read / TryParse for every type marked
// below; nothing here is consulted at runtime — the attributes exist only so the compiler can see
// the layout. Encodings are chosen by field TYPE with these overrides:
//
//   type                        wire bytes                     notes
//   byte/sbyte/bool             1                              bool = 0/1
//   short/ushort                2 LE
//   int/uint/float              4 LE
//   long/ulong/double           8 LE
//   enum                        underlying integer
//   string                      u16 length + UTF-8             [Wire(WireEnc.StrU8)] = u8 length,
//                                                              [Wire(WireEnc.Str7Bit)] = BinaryWriter 7-bit length
//   Vec3                        3x f32                         [Wire(WireEnc.Pos)] = 3x i16 sector-local,
//                                                              [Wire(WireEnc.Half)] = 3x f16
//   Quat                        4x f32                         [Wire(WireEnc.Quat)] = smallest-three u32
//   float                       f32                            [Wire(WireEnc.Pos|Half)] as above,
//                                                              [Wire(WireEnc.Angle, Range = r)] = i16 over [-r, r]
//   int/uint/long/...           native                         [Wire(WireEnc.U8|U16|U32)] = clamped narrow write
//   T? (Nullable / class?)      u8 presence + T
//   T[] / List<T>               count + elements               count width from [WireCount], default u8;
//                                                              the writer caps the count at the width's max
//   [WireRecord] type           its own layout inline
//
// [WireOptional] marks a TRAILING field an older sender may omit: the reader stops silently (fields
// keep their defaults) when the bytes run out before it. [WireIgnore] keeps a field off the wire.

// A frame: the leading byte is the message id, then the fields.
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class WireMessageAttribute : Attribute
{
    public byte Id { get; }

    public WireMessageAttribute(byte id) => Id = id;
}

// A record embedded in frames (no id byte of its own).
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class WireRecordAttribute : Attribute { }

public enum WireEnc : byte
{
    Default = 0,
    Pos = 1, // float/Vec3: sector-local i16 (WireQuant.PackPos)
    Half = 2, // float/Vec3: IEEE half (WireQuant.PackHalf)
    Quat = 3, // Quat: smallest-three u32 (WireQuant.PackQuat)
    Angle = 4, // float: i16 fraction of Range (WireQuant.PackAngle)
    U8 = 5, // integer: clamped to a byte
    U16 = 6, // integer: clamped to a ushort
    U32 = 7, // integer: clamped to a uint
    StrU8 = 8, // string: u8 length prefix (255 bytes max)
    Str7Bit = 9, // string: BinaryWriter/BinaryReader 7-bit-encoded length prefix
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class WireAttribute : Attribute
{
    public WireEnc Enc { get; }

    // Only for WireEnc.Angle: the half-range the i16 spans (yaw = MathF.PI, pitch = MathF.PI / 2).
    public float Range { get; set; }

    public WireAttribute(WireEnc enc) => Enc = enc;
}

public enum WireWidth : byte
{
    U8 = 0,
    U16 = 1,
    U32 = 2,
}

// Count-prefix width for an array / list field (default u8 when absent).
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class WireCountAttribute : Attribute
{
    public WireWidth Width { get; }

    public WireCountAttribute(WireWidth width) => Width = width;
}

// Trailing field an older sender may omit; the reader keeps the default when the bytes run out.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class WireOptionalAttribute : Attribute { }

// Not on the wire.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false)]
public sealed class WireIgnoreAttribute : Attribute { }

// Thrown by the generated Parse when a frame is truncated, carries the wrong id, or is malformed.
public sealed class WireFormatException : Exception
{
    public WireFormatException(string type)
        : base($"malformed wire frame for {type}") { }
}
