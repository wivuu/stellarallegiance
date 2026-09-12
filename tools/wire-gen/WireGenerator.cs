using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace WireGen;

// Incremental source generator for the binary wire format (docs/adr/0003-wire-format-source-generator.md).
//
// For every partial type carrying [WireMessage(id)] or [WireRecord] it emits, from the type's own
// field list in declaration order:
//
//   const byte MsgId            (messages only — the leading type byte)
//   const int  Size             (only when every field is fixed-size; messages include the id byte)
//   int  Measure()              exact serialized length, so callers allocate once
//   void Write(ref WireWriter)  straight-line little-endian span writes (no reflection, no boxing)
//   int  Write(Span<byte>)      convenience over a fresh WireWriter, returns bytes written
//   byte[] ToBytes()            Measure + Write
//   static T Read(ref WireReader)
//   static bool TryParse(ReadOnlySpan<byte>, out T)   (never throws — a truncated/hostile frame fails)
//   static T Parse(ReadOnlySpan<byte>)                (throws WireFormatException)
//
// Field encodings come from the field's TYPE plus the optional [Wire(...)] / [WireCount(...)] /
// [WireOptional] / [WireIgnore] attributes — see shared/Net/WireAttributes.cs for the vocabulary.
// The generated bodies are exactly the code a careful human writes by hand (BinaryPrimitives
// little-endian writes at a running offset), so there is no runtime cost over the hand-rolled
// writers this replaced; what changes is that the layout exists in ONE place and both ends of the
// wire compile the same definition.
[Generator(LanguageNames.CSharp)]
public sealed class WireGenerator : IIncrementalGenerator
{
    internal const string Ns = "StellarAllegiance.Shared.Net";
    internal const string MessageAttr = Ns + ".WireMessageAttribute";
    internal const string RecordAttr = Ns + ".WireRecordAttribute";
    internal const string WireAttr = Ns + ".WireAttribute";
    internal const string CountAttr = Ns + ".WireCountAttribute";
    internal const string OptionalAttr = Ns + ".WireOptionalAttribute";
    internal const string IgnoreAttr = Ns + ".WireIgnoreAttribute";
    internal const string Vec3Type = "StellarAllegiance.Shared.Vec3";
    internal const string QuatType = "StellarAllegiance.Shared.Quat";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var messages = context.SyntaxProvider.ForAttributeWithMetadataName(
            MessageAttr,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => TypeModel.FromSymbol((INamedTypeSymbol)ctx.TargetSymbol, isMessage: true)
        );
        var records = context.SyntaxProvider.ForAttributeWithMetadataName(
            RecordAttr,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => TypeModel.FromSymbol((INamedTypeSymbol)ctx.TargetSymbol, isMessage: false)
        );
        var all = messages.Collect().Combine(records.Collect());
        context.RegisterSourceOutput(all, static (spc, pair) => Execute(spc, pair.Left, pair.Right));
    }

    private static void Execute(
        SourceProductionContext spc,
        ImmutableArray<TypeModel> messages,
        ImmutableArray<TypeModel> records
    )
    {
        var models = new Dictionary<string, TypeModel>();
        foreach (var m in messages.Concat(records))
        {
            if (models.ContainsKey(m.FullName))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Rules.Duplicate, m.Location, m.FullName));
                continue;
            }
            models[m.FullName] = m;
        }

        var resolver = new Resolver(models);
        foreach (var m in models.Values)
        {
            foreach (var d in m.Diagnostics)
                spc.ReportDiagnostic(d);
            if (!m.Emittable)
                continue;
            var text = Emitter.Emit(m, resolver, spc);
            if (text != null)
                spc.AddSource(m.HintName, SourceText.From(text, Encoding.UTF8));
        }
    }
}

internal static class Rules
{
    public static readonly DiagnosticDescriptor NotPartial = new(
        "WIRE001",
        "Wire type must be partial",
        "'{0}' carries a wire attribute but is not declared partial; add the partial modifier so the codec can be generated",
        "Wire",
        DiagnosticSeverity.Error,
        true
    );
    public static readonly DiagnosticDescriptor Unsupported = new(
        "WIRE002",
        "Unsupported wire field type",
        "Field '{0}' of '{1}' has type '{2}', which has no wire encoding (mark it [WireIgnore], or give its type [WireRecord])",
        "Wire",
        DiagnosticSeverity.Error,
        true
    );
    public static readonly DiagnosticDescriptor OptionalNotTrailing = new(
        "WIRE003",
        "Optional wire fields must be trailing",
        "Field '{0}' of '{1}' follows a [WireOptional] field but is not itself optional; every field after the first optional one must be optional",
        "Wire",
        DiagnosticSeverity.Error,
        true
    );
    public static readonly DiagnosticDescriptor BadEncoding = new(
        "WIRE004",
        "Wire encoding does not apply to this field type",
        "Field '{0}' of '{1}' asks for encoding {2}, which cannot encode a '{3}'",
        "Wire",
        DiagnosticSeverity.Error,
        true
    );
    public static readonly DiagnosticDescriptor Duplicate = new(
        "WIRE005",
        "Duplicate wire type",
        "Wire type '{0}' was collected twice",
        "Wire",
        DiagnosticSeverity.Error,
        true
    );
    public static readonly DiagnosticDescriptor ReadonlyField = new(
        "WIRE006",
        "Wire field must be assignable",
        "Field '{0}' of '{1}' is readonly; the generated reader assigns every wire field (use a positional record for immutable types)",
        "Wire",
        DiagnosticSeverity.Error,
        true
    );
    public static readonly DiagnosticDescriptor NoCtor = new(
        "WIRE007",
        "Wire type needs a parameterless constructor",
        "'{0}' is a class without an accessible parameterless constructor; the generated reader cannot instantiate it",
        "Wire",
        DiagnosticSeverity.Error,
        true
    );
}

// ---- Field type model --------------------------------------------------------------------------

internal enum Prim
{
    U8,
    I8,
    Bool,
    I16,
    U16,
    I32,
    U32,
    I64,
    U64,
    F32,
    F64,
}

internal enum Enc
{
    Default,
    Pos,
    Half,
    Quat,
    Angle,
    U8,
    U16,
    U32,
    StrU8,
    Str7Bit,
}

internal enum Width
{
    U8,
    U16,
    U32,
}

// The recursive encoding tree for one field. Every node knows how to emit its own measure /
// write / read code (see Emitter) and whether it has a fixed byte size.
internal abstract class Ty
{
    public sealed class Primitive : Ty
    {
        public Prim Kind;
    }

    public sealed class QuantFloat : Ty
    {
        public Enc Kind; // Pos | Half | Angle
        public float Range;
    }

    public sealed class Vec3 : Ty
    {
        public Enc Kind; // Default (f32) | Pos | Half
    }

    public sealed class Quat : Ty
    {
        public Enc Kind; // Default (4x f32) | Quat (smallest-three u32)
    }

    public sealed class Str : Ty
    {
        public Enc Kind; // Default (u16) | StrU8 | Str7Bit
    }

    public sealed class Enum : Ty
    {
        public Prim Underlying;
        public string TypeName = ""; // fully qualified
    }

    public sealed class Narrow : Ty
    {
        public Prim Target; // U8 | U16 | U32
        public string SourceType = ""; // int / uint / long ...
        public bool SourceSigned;
    }

    public sealed class Nested : Ty
    {
        public string TypeName = ""; // fully qualified (global::)
        public string Key = ""; // model lookup key
        public bool IsReference;
    }

    public sealed class Coll : Ty
    {
        public Ty Elem = null!;
        public Width Count;
        public bool IsArray; // else List<T> / IReadOnlyList<T>
        public string ElemTypeName = ""; // fully qualified element type
        public bool IsByteArray;
    }

    public sealed class Nullable : Ty
    {
        public Ty Elem = null!;
        public bool IsReference; // class? vs Nullable<T>
    }
}

internal sealed class FieldModel
{
    public string Name = "";
    public string TypeName = ""; // fully qualified C# type for temps
    public Ty Type = null!;
    public bool Optional;
    public bool Ignored;
    public bool IsProperty; // positional record member
}

internal sealed class TypeModel
{
    public string FullName = ""; // Namespace.Outer.Name (model key)
    public string Name = "";
    public string Namespace = "";
    public List<(string Keyword, string Name)> Containers = new(); // outer partial types, outermost first
    public string Keyword = ""; // struct | class | record struct | record
    public bool IsMessage;
    public byte MsgId;
    public bool IsValueType;
    public bool Positional; // read via primary constructor
    public List<FieldModel> Fields = new();
    public List<string> CtorArgs = new(); // positional: expression per ctor parameter (field name or "default")
    public List<Diagnostic> Diagnostics = new();
    public bool Emittable = true;
    public Location? Location;
    public string HintName => FullName.Replace('.', '_') + ".Wire.g.cs";

    public static TypeModel FromSymbol(INamedTypeSymbol sym, bool isMessage)
    {
        var m = new TypeModel
        {
            Name = sym.Name,
            Namespace = sym.ContainingNamespace.IsGlobalNamespace ? "" : sym.ContainingNamespace.ToDisplayString(),
            IsMessage = isMessage,
            IsValueType = sym.IsValueType,
            Location = sym.Locations.FirstOrDefault(),
        };
        m.Keyword = KeywordOf(sym);
        var chain = new List<(string, string)>();
        for (var c = sym.ContainingType; c != null; c = c.ContainingType)
            chain.Insert(0, (KeywordOf(c), c.Name));
        m.Containers = chain;
        var fullParts = new List<string>();
        if (m.Namespace.Length > 0)
            fullParts.Add(m.Namespace);
        foreach (var c in chain)
            fullParts.Add(c.Item2);
        fullParts.Add(sym.Name);
        m.FullName = string.Join(".", fullParts);

        if (isMessage)
        {
            var attr = sym.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == WireGenerator.MessageAttr);
            m.MsgId = (byte)(attr.ConstructorArguments.Length > 0 ? Convert.ToInt32(attr.ConstructorArguments[0].Value) : 0);
        }

        bool isPartial = sym.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax() is TypeDeclarationSyntax t && t.Modifiers.Any(SyntaxKind.PartialKeyword)
        );
        if (!isPartial)
        {
            m.Diagnostics.Add(Diagnostic.Create(Rules.NotPartial, m.Location, m.FullName));
            m.Emittable = false;
        }

        // Positional record: the primary constructor whose parameters all map to same-named properties.
        IMethodSymbol? primary = null;
        if (sym.IsRecord)
        {
            foreach (var ctor in sym.InstanceConstructors.OrderByDescending(c => c.Parameters.Length))
            {
                if (
                    ctor.Parameters.Length == 0
                    || ctor.IsImplicitlyDeclared
                        && ctor.Parameters.Length == 1
                        && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, sym)
                )
                    continue;
                bool allMatch = ctor.Parameters.All(p =>
                    sym.GetMembers(p.Name)
                        .OfType<IPropertySymbol>()
                        .Any(pr => SymbolEqualityComparer.Default.Equals(pr.Type, p.Type))
                );
                if (allMatch)
                {
                    primary = ctor;
                    break;
                }
            }
        }

        if (primary != null)
        {
            m.Positional = true;
            foreach (var p in primary.Parameters)
            {
                var prop = sym.GetMembers(p.Name).OfType<IPropertySymbol>().First();
                var attrs = p.GetAttributes().Concat(prop.GetAttributes()).ToList();
                var f = BuildField(m, p.Name, p.Type, attrs, isProperty: true, isReadonly: false, sym);
                m.Fields.Add(f);
                m.CtorArgs.Add(f.Ignored ? "default" : "__" + f.Name);
            }
        }
        else
        {
            if (!sym.IsValueType)
            {
                bool hasCtor = sym.InstanceConstructors.Any(c =>
                    c.Parameters.Length == 0 && c.DeclaredAccessibility != Accessibility.Private
                );
                if (!hasCtor)
                {
                    m.Diagnostics.Add(Diagnostic.Create(Rules.NoCtor, m.Location, m.FullName));
                    m.Emittable = false;
                }
            }
            var members = sym.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f =>
                    !f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared && f.DeclaredAccessibility == Accessibility.Public
                )
                .OrderBy(f => f.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "", StringComparer.Ordinal)
                .ThenBy(f => f.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0)
                .ToList();
            foreach (var f in members)
                m.Fields.Add(
                    BuildField(
                        m,
                        f.Name,
                        f.Type,
                        f.GetAttributes().ToList(),
                        isProperty: false,
                        isReadonly: f.IsReadOnly,
                        sym
                    )
                );
        }

        // Optional fields must be trailing.
        bool seenOptional = false;
        foreach (var f in m.Fields)
        {
            if (f.Ignored)
                continue;
            if (f.Optional)
                seenOptional = true;
            else if (seenOptional)
            {
                m.Diagnostics.Add(Diagnostic.Create(Rules.OptionalNotTrailing, m.Location, f.Name, m.FullName));
                m.Emittable = false;
            }
        }
        return m;
    }

    private static string KeywordOf(INamedTypeSymbol s)
    {
        if (s.IsRecord)
            return s.IsValueType ? "record struct" : "record";
        return s.TypeKind switch
        {
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => "class",
        };
    }

    private static FieldModel BuildField(
        TypeModel owner,
        string name,
        ITypeSymbol type,
        List<AttributeData> attrs,
        bool isProperty,
        bool isReadonly,
        INamedTypeSymbol ownerSym
    )
    {
        var f = new FieldModel
        {
            Name = name,
            IsProperty = isProperty,
            TypeName = Fq(type),
        };
        Enc enc = Enc.Default;
        float range = 0f;
        Width count = Width.U8;
        foreach (var a in attrs)
        {
            string? an = a.AttributeClass?.ToDisplayString();
            if (an == WireGenerator.IgnoreAttr)
                f.Ignored = true;
            else if (an == WireGenerator.OptionalAttr)
                f.Optional = true;
            else if (an == WireGenerator.WireAttr)
            {
                if (a.ConstructorArguments.Length > 0)
                    enc = (Enc)Convert.ToInt32(a.ConstructorArguments[0].Value);
                foreach (var na in a.NamedArguments)
                    if (na.Key == "Range")
                        range = Convert.ToSingle(na.Value.Value);
            }
            else if (an == WireGenerator.CountAttr)
            {
                if (a.ConstructorArguments.Length > 0)
                    count = (Width)Convert.ToInt32(a.ConstructorArguments[0].Value);
            }
        }
        if (f.Ignored)
        {
            f.Type = new Ty.Primitive { Kind = Prim.U8 }; // never emitted
            return f;
        }
        if (isReadonly)
        {
            owner.Diagnostics.Add(Diagnostic.Create(Rules.ReadonlyField, owner.Location, name, owner.FullName));
            owner.Emittable = false;
        }
        var ty = Analyze(type, enc, range, count, out string? error);
        if (ty == null)
        {
            owner.Diagnostics.Add(
                error == "enc"
                    ? Diagnostic.Create(
                        Rules.BadEncoding,
                        owner.Location,
                        name,
                        owner.FullName,
                        enc.ToString(),
                        type.ToDisplayString()
                    )
                    : Diagnostic.Create(Rules.Unsupported, owner.Location, name, owner.FullName, type.ToDisplayString())
            );
            owner.Emittable = false;
            f.Type = new Ty.Primitive { Kind = Prim.U8 };
            return f;
        }
        f.Type = ty;
        return f;
    }

    private static readonly SymbolDisplayFormat FqFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    internal static string Fq(ITypeSymbol t) => t.ToDisplayString(FqFormat);

    private static bool IsWireType(ITypeSymbol t) =>
        t.GetAttributes()
            .Any(a =>
            {
                var n = a.AttributeClass?.ToDisplayString();
                return n == WireGenerator.RecordAttr || n == WireGenerator.MessageAttr;
            });

    private static string ModelKey(ITypeSymbol t)
    {
        var parts = new List<string>();
        var ns = t.ContainingNamespace;
        if (ns != null && !ns.IsGlobalNamespace)
            parts.Add(ns.ToDisplayString());
        var chain = new List<string>();
        for (var c = t.ContainingType; c != null; c = c.ContainingType)
            chain.Insert(0, c.Name);
        parts.AddRange(chain);
        parts.Add(t.Name);
        return string.Join(".", parts);
    }

    private static Ty? Analyze(ITypeSymbol type, Enc enc, float range, Width count, out string? error)
    {
        error = null;

        // Nullable<T> → presence byte + T
        if (
            type is INamedTypeSymbol nt
            && nt.IsGenericType
            && nt.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
        )
        {
            var inner = Analyze(nt.TypeArguments[0], enc, range, count, out error);
            return inner == null ? null : new Ty.Nullable { Elem = inner, IsReference = false };
        }

        // Arrays
        if (type is IArrayTypeSymbol at && at.Rank == 1)
        {
            var elem = Analyze(at.ElementType, enc, range, Width.U8, out error);
            if (elem == null)
                return null;
            return new Ty.Coll
            {
                Elem = elem,
                Count = count,
                IsArray = true,
                ElemTypeName = Fq(at.ElementType),
                IsByteArray = elem is Ty.Primitive p && p.Kind == Prim.U8,
            };
        }

        // List<T> / IReadOnlyList<T> / IList<T>
        if (type is INamedTypeSymbol lt && lt.IsGenericType && lt.TypeArguments.Length == 1)
        {
            string ctor = lt.ConstructedFrom.ToDisplayString();
            if (
                ctor == "System.Collections.Generic.List<T>"
                || ctor == "System.Collections.Generic.IReadOnlyList<T>"
                || ctor == "System.Collections.Generic.IList<T>"
            )
            {
                var elem = Analyze(lt.TypeArguments[0], enc, range, Width.U8, out error);
                if (elem == null)
                    return null;
                return new Ty.Coll
                {
                    Elem = elem,
                    Count = count,
                    IsArray = false,
                    ElemTypeName = Fq(lt.TypeArguments[0]),
                };
            }
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            if (enc is Enc.Default or Enc.StrU8 or Enc.Str7Bit)
                return new Ty.Str { Kind = enc };
            error = "enc";
            return null;
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol et && et.EnumUnderlyingType != null)
        {
            var u = PrimOf(et.EnumUnderlyingType.SpecialType);
            if (u == null)
                return null;
            return new Ty.Enum { Underlying = u.Value, TypeName = Fq(type) };
        }

        var prim = PrimOf(type.SpecialType);
        if (prim != null)
        {
            switch (enc)
            {
                case Enc.Default:
                    return new Ty.Primitive { Kind = prim.Value };
                case Enc.Pos:
                case Enc.Half:
                case Enc.Angle:
                    if (prim == Prim.F32)
                        return new Ty.QuantFloat { Kind = enc, Range = range };
                    error = "enc";
                    return null;
                case Enc.U8:
                case Enc.U16:
                case Enc.U32:
                    if (prim is Prim.I32 or Prim.U32 or Prim.I64 or Prim.U64 or Prim.I16 or Prim.U16)
                    {
                        bool signed = prim is Prim.I32 or Prim.I64 or Prim.I16;
                        return new Ty.Narrow
                        {
                            Target =
                                enc == Enc.U8 ? Prim.U8
                                : enc == Enc.U16 ? Prim.U16
                                : Prim.U32,
                            SourceType = Fq(type),
                            SourceSigned = signed,
                        };
                    }
                    error = "enc";
                    return null;
                default:
                    error = "enc";
                    return null;
            }
        }

        string full = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
        if (full == WireGenerator.Vec3Type)
        {
            if (enc is Enc.Default or Enc.Pos or Enc.Half)
                return new Ty.Vec3 { Kind = enc };
            error = "enc";
            return null;
        }
        if (full == WireGenerator.QuatType)
        {
            if (enc is Enc.Default or Enc.Quat)
                return new Ty.Quat { Kind = enc };
            error = "enc";
            return null;
        }

        if (IsWireType(type))
        {
            var nested = new Ty.Nested
            {
                TypeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Key = ModelKey(type),
                IsReference = type.IsReferenceType,
            };
            if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
                return new Ty.Nullable { Elem = nested, IsReference = true };
            return nested;
        }
        return null;
    }

    private static Prim? PrimOf(SpecialType st) =>
        st switch
        {
            SpecialType.System_Byte => Prim.U8,
            SpecialType.System_SByte => Prim.I8,
            SpecialType.System_Boolean => Prim.Bool,
            SpecialType.System_Int16 => Prim.I16,
            SpecialType.System_UInt16 => Prim.U16,
            SpecialType.System_Int32 => Prim.I32,
            SpecialType.System_UInt32 => Prim.U32,
            SpecialType.System_Int64 => Prim.I64,
            SpecialType.System_UInt64 => Prim.U64,
            SpecialType.System_Single => Prim.F32,
            SpecialType.System_Double => Prim.F64,
            _ => null,
        };
}

// Cross-type facts (fixed sizes) resolved over the whole collected set, with a cycle guard.
internal sealed class Resolver
{
    private readonly Dictionary<string, TypeModel> _models;
    private readonly Dictionary<string, int?> _fixed = new();
    private readonly HashSet<string> _visiting = new();

    public Resolver(Dictionary<string, TypeModel> models) => _models = models;

    public bool Known(string key) => _models.ContainsKey(key);

    public int? FixedSize(TypeModel m)
    {
        if (_fixed.TryGetValue(m.FullName, out var cached))
            return cached;
        if (!_visiting.Add(m.FullName))
            return null; // recursive type: variable
        int total = m.IsMessage ? 1 : 0;
        int? result = total;
        foreach (var f in m.Fields)
        {
            if (f.Ignored)
                continue;
            if (f.Optional)
            {
                result = null;
                break;
            }
            var s = FixedSize(f.Type);
            if (s == null)
            {
                result = null;
                break;
            }
            total += s.Value;
            result = total;
        }
        _visiting.Remove(m.FullName);
        _fixed[m.FullName] = result;
        return result;
    }

    public int? FixedSize(Ty t)
    {
        switch (t)
        {
            case Ty.Primitive p:
                return Emitter.PrimSize(p.Kind);
            case Ty.QuantFloat:
                return 2;
            case Ty.Vec3 v:
                return v.Kind == Enc.Default ? 12 : 6;
            case Ty.Quat q:
                return q.Kind == Enc.Default ? 16 : 4;
            case Ty.Enum e:
                return Emitter.PrimSize(e.Underlying);
            case Ty.Narrow n:
                return Emitter.PrimSize(n.Target);
            case Ty.Nested n:
                return _models.TryGetValue(n.Key, out var nm) ? FixedSize(nm) : null;
            default:
                return null;
        }
    }
}

// ---- Code emission ---------------------------------------------------------------------------

internal static class Emitter
{
    public static int PrimSize(Prim p) =>
        p switch
        {
            Prim.U8 or Prim.I8 or Prim.Bool => 1,
            Prim.I16 or Prim.U16 => 2,
            Prim.I32 or Prim.U32 or Prim.F32 => 4,
            _ => 8,
        };

    private static string PrimMethod(Prim p) => p.ToString();

    private static string WidthMethod(Width w) =>
        w switch
        {
            Width.U8 => "U8",
            Width.U16 => "U16",
            _ => "U32",
        };

    private static string WidthCast(Width w) =>
        w switch
        {
            Width.U8 => "(byte)",
            Width.U16 => "(ushort)",
            _ => "(uint)",
        };

    private static string WidthMax(Width w) =>
        w switch
        {
            Width.U8 => "255",
            Width.U16 => "65535",
            _ => "int.MaxValue",
        };

    private static int WidthSize(Width w) =>
        w switch
        {
            Width.U8 => 1,
            Width.U16 => 2,
            _ => 4,
        };

    private const string Q = "global::StellarAllegiance.Shared.WireQuant";
    private const string IO = "global::StellarAllegiance.Shared.Net.WireIO";

    public static string? Emit(TypeModel m, Resolver res, SourceProductionContext spc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "// <auto-generated/> — WireGen (tools/wire-gen). Do not edit; edit the field list on the partial type."
        );
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8625, CS0162");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        int indent = 0;
        if (m.Namespace.Length > 0)
        {
            sb.AppendLine($"namespace {m.Namespace}");
            sb.AppendLine("{");
            indent++;
        }
        foreach (var c in m.Containers)
        {
            Line(sb, indent, $"partial {c.Keyword} {c.Name}");
            Line(sb, indent, "{");
            indent++;
        }
        Line(sb, indent, $"partial {m.Keyword} {m.Name}");
        Line(sb, indent, "{");
        indent++;

        int? fixedSize = res.FixedSize(m);
        if (m.IsMessage)
            Line(sb, indent, $"public const byte MsgId = {m.MsgId};");
        if (fixedSize != null)
            Line(sb, indent, $"public const int Size = {fixedSize.Value};");
        sb.AppendLine();

        // ---- Measure ----
        Line(sb, indent, "public int Measure()");
        Line(sb, indent, "{");
        if (fixedSize != null)
            Line(sb, indent + 1, "return Size;");
        else
        {
            Line(sb, indent + 1, $"int __n = {(m.IsMessage ? 1 : 0)};");
            foreach (var f in m.Fields)
            {
                if (f.Ignored)
                    continue;
                EmitMeasure(sb, indent + 1, f.Type, "this." + f.Name, res, "__n");
            }
            Line(sb, indent + 1, "return __n;");
        }
        Line(sb, indent, "}");
        sb.AppendLine();

        // ---- Write(ref WireWriter) ----
        Line(sb, indent, $"public void Write(ref global::{WireGenerator.Ns}.WireWriter w)");
        Line(sb, indent, "{");
        if (m.IsMessage)
            Line(sb, indent + 1, "w.U8(MsgId);");
        int tmp = 0;
        foreach (var f in m.Fields)
        {
            if (f.Ignored)
                continue;
            EmitWrite(sb, indent + 1, f.Type, "this." + f.Name, res, ref tmp);
        }
        Line(sb, indent, "}");
        sb.AppendLine();

        Line(sb, indent, "public int Write(Span<byte> dst)");
        Line(sb, indent, "{");
        Line(sb, indent + 1, $"var w = new global::{WireGenerator.Ns}.WireWriter(dst);");
        Line(sb, indent + 1, "Write(ref w);");
        Line(sb, indent + 1, "return w.Pos;");
        Line(sb, indent, "}");
        sb.AppendLine();

        Line(sb, indent, "public byte[] ToBytes()");
        Line(sb, indent, "{");
        Line(sb, indent + 1, "var buf = new byte[Measure()];");
        Line(sb, indent + 1, "Write(buf);");
        Line(sb, indent + 1, "return buf;");
        Line(sb, indent, "}");
        sb.AppendLine();

        // ---- Read(ref WireReader) ----
        string self = m.Name;
        Line(sb, indent, $"public static {self} Read(ref global::{WireGenerator.Ns}.WireReader r)");
        Line(sb, indent, "{");
        if (m.IsMessage)
            Line(sb, indent + 1, "if (r.U8() != MsgId) r.Fail();");
        if (m.Positional)
        {
            foreach (var f in m.Fields)
                if (!f.Ignored)
                    Line(
                        sb,
                        indent + 1,
                        $"{f.TypeName} __{f.Name} = {(f.Optional ? EmptyValue(f.Type) ?? "default" : "default")};"
                    );
        }
        else
        {
            Line(sb, indent + 1, m.IsValueType ? $"{self} v = default;" : $"{self} v = new {self}();");
            // An absent optional tail reads as EMPTY (string "" / empty collection), never null — the
            // defaults are set up front because the reader jumps to __done at the first missing field.
            foreach (var f in m.Fields)
                if (!f.Ignored && f.Optional && EmptyValue(f.Type) is string empty)
                    Line(sb, indent + 1, $"v.{f.Name} = {empty};");
        }
        bool anyOptional = m.Fields.Any(f => !f.Ignored && f.Optional);
        tmp = 0;
        foreach (var f in m.Fields)
        {
            if (f.Ignored)
                continue;
            string target = m.Positional ? "__" + f.Name : "v." + f.Name;
            if (f.Optional)
            {
                Line(sb, indent + 1, "if (r.Remaining == 0 || r.Failed) goto __done;");
                Line(sb, indent + 1, "{");
                Line(sb, indent + 2, "int __mark = r.Pos;");
                Line(sb, indent + 2, $"{f.TypeName} __opt = default;");
                EmitRead(sb, indent + 2, f.Type, "__opt", res, ref tmp);
                Line(sb, indent + 2, "if (r.Failed) { r.Rewind(__mark); goto __done; }");
                Line(sb, indent + 2, $"{target} = __opt;");
                Line(sb, indent + 1, "}");
            }
            else
                EmitRead(sb, indent + 1, f.Type, target, res, ref tmp);
        }
        if (anyOptional)
            Line(sb, indent + 1, "__done:");
        if (m.Positional)
            Line(sb, indent + 1, $"return new {self}({string.Join(", ", m.CtorArgs)});");
        else
            Line(sb, indent + 1, "return v;");
        Line(sb, indent, "}");
        sb.AppendLine();

        Line(sb, indent, $"public static bool TryParse(ReadOnlySpan<byte> src, out {self} value)");
        Line(sb, indent, "{");
        Line(sb, indent + 1, $"var r = new global::{WireGenerator.Ns}.WireReader(src);");
        Line(sb, indent + 1, "value = Read(ref r);");
        Line(sb, indent + 1, "return !r.Failed;");
        Line(sb, indent, "}");
        sb.AppendLine();

        Line(sb, indent, $"public static {self} Parse(ReadOnlySpan<byte> src)");
        Line(sb, indent, "{");
        Line(sb, indent + 1, "if (!TryParse(src, out var value))");
        Line(sb, indent + 2, $"throw new global::{WireGenerator.Ns}.WireFormatException(\"{m.FullName}\");");
        Line(sb, indent + 1, "return value;");
        Line(sb, indent, "}");

        indent--;
        Line(sb, indent, "}");
        for (int i = 0; i < m.Containers.Count; i++)
        {
            indent--;
            Line(sb, indent, "}");
        }
        if (m.Namespace.Length > 0)
            sb.AppendLine("}");
        return sb.ToString();
    }

    // The "absent" value of an optional field: empty string / empty collection; null for anything else.
    private static string? EmptyValue(Ty t) =>
        t switch
        {
            Ty.Str => "\"\"",
            Ty.Coll c when c.IsArray => $"Array.Empty<{c.ElemTypeName}>()",
            Ty.Coll c => $"new List<{c.ElemTypeName}>()",
            _ => null,
        };

    private static void Line(StringBuilder sb, int indent, string text)
    {
        for (int i = 0; i < indent; i++)
            sb.Append("    ");
        sb.AppendLine(text);
    }

    // ---- Measure ----
    private static void EmitMeasure(StringBuilder sb, int ind, Ty t, string v, Resolver res, string acc)
    {
        var fs = res.FixedSize(t);
        if (fs != null)
        {
            Line(sb, ind, $"{acc} += {fs.Value};");
            return;
        }
        switch (t)
        {
            case Ty.Str s:
                Line(sb, ind, $"{acc} += {IO}.{StrSizeMethod(s.Kind)}({v});");
                break;
            case Ty.Nested n:
                Line(sb, ind, $"{acc} += {v}.Measure();");
                break;
            case Ty.Nullable nu:
                Line(sb, ind, $"{acc} += 1;");
                Line(sb, ind, nu.IsReference ? $"if ({v} is not null)" : $"if ({v}.HasValue)");
                Line(sb, ind, "{");
                EmitMeasure(sb, ind + 1, nu.Elem, nu.IsReference ? v : v + ".Value", res, acc);
                Line(sb, ind, "}");
                break;
            case Ty.Coll c:
            {
                string countExpr = c.IsArray ? $"({v} is null ? 0 : {v}.Length)" : $"({v} is null ? 0 : {v}.Count)";
                Line(sb, ind, $"{acc} += {WidthSize(c.Count)};");
                var es = res.FixedSize(c.Elem);
                if (es != null)
                    Line(sb, ind, $"{acc} += Math.Min({countExpr}, {WidthMax(c.Count)}) * {es.Value};");
                else
                {
                    string i = "__i" + ind;
                    Line(
                        sb,
                        ind,
                        $"for (int {i} = 0, __c{ind} = Math.Min({countExpr}, {WidthMax(c.Count)}); {i} < __c{ind}; {i}++)"
                    );
                    Line(sb, ind, "{");
                    EmitMeasure(sb, ind + 1, c.Elem, $"{v}[{i}]", res, acc);
                    Line(sb, ind, "}");
                }
                break;
            }
            default:
                throw new InvalidOperationException("unreachable: variable-size leaf");
        }
    }

    private static string StrSizeMethod(Enc k) =>
        k switch
        {
            Enc.StrU8 => "StrU8Size",
            Enc.Str7Bit => "Str7Size",
            _ => "StrSize",
        };

    // ---- Write ----
    private static void EmitWrite(StringBuilder sb, int ind, Ty t, string v, Resolver res, ref int tmp)
    {
        switch (t)
        {
            case Ty.Primitive p:
                Line(sb, ind, $"w.{PrimMethod(p.Kind)}({v});");
                break;
            case Ty.QuantFloat q:
                Line(
                    sb,
                    ind,
                    q.Kind switch
                    {
                        Enc.Pos => $"w.I16({Q}.PackPos({v}));",
                        Enc.Half => $"w.U16({Q}.PackHalf({v}));",
                        _ =>
                            $"w.I16({Q}.PackAngle({v}, {q.Range.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}f));",
                    }
                );
                break;
            case Ty.Vec3 vec:
                foreach (var ax in new[] { "X", "Y", "Z" })
                    Line(
                        sb,
                        ind,
                        vec.Kind switch
                        {
                            Enc.Pos => $"w.I16({Q}.PackPos({v}.{ax}));",
                            Enc.Half => $"w.U16({Q}.PackHalf({v}.{ax}));",
                            _ => $"w.F32({v}.{ax});",
                        }
                    );
                break;
            case Ty.Quat qt:
                if (qt.Kind == Enc.Quat)
                    Line(sb, ind, $"w.U32({Q}.PackQuat({v}.X, {v}.Y, {v}.Z, {v}.W));");
                else
                    foreach (var ax in new[] { "X", "Y", "Z", "W" })
                        Line(sb, ind, $"w.F32({v}.{ax});");
                break;
            case Ty.Str s:
                Line(
                    sb,
                    ind,
                    s.Kind switch
                    {
                        Enc.StrU8 => $"w.StrU8({v});",
                        Enc.Str7Bit => $"w.Str7({v});",
                        _ => $"w.Str({v});",
                    }
                );
                break;
            case Ty.Enum e:
                Line(sb, ind, $"w.{PrimMethod(e.Underlying)}(({PrimCSharp(e.Underlying)}){v});");
                break;
            case Ty.Narrow n:
            {
                string cast = PrimCSharp(n.Target);
                string max = n.Target switch
                {
                    Prim.U8 => "255",
                    Prim.U16 => "65535",
                    _ => "uint.MaxValue",
                };
                if (n.SourceSigned)
                    Line(sb, ind, $"w.{PrimMethod(n.Target)}(({cast})Math.Clamp({v}, 0, {max}));");
                else
                    Line(sb, ind, $"w.{PrimMethod(n.Target)}(({cast})Math.Min({v}, {max}));");
                break;
            }
            case Ty.Nested nested:
                Line(sb, ind, $"{v}.Write(ref w);");
                break;
            case Ty.Nullable nu:
                Line(sb, ind, nu.IsReference ? $"if ({v} is null) w.U8(0); else" : $"if (!{v}.HasValue) w.U8(0); else");
                Line(sb, ind, "{");
                Line(sb, ind + 1, "w.U8(1);");
                EmitWrite(sb, ind + 1, nu.Elem, nu.IsReference ? v : v + ".Value", res, ref tmp);
                Line(sb, ind, "}");
                break;
            case Ty.Coll c:
            {
                string n = "__n" + tmp++;
                string countExpr = c.IsArray ? $"({v} is null ? 0 : {v}.Length)" : $"({v} is null ? 0 : {v}.Count)";
                Line(sb, ind, $"int {n} = Math.Min({countExpr}, {WidthMax(c.Count)});");
                Line(sb, ind, $"w.{WidthMethod(c.Count)}({WidthCast(c.Count)}{n});");
                if (c.IsByteArray)
                {
                    Line(sb, ind, $"w.Bytes(new ReadOnlySpan<byte>({v}, 0, {n}));");
                    break;
                }
                string i = "__i" + tmp++;
                Line(sb, ind, $"for (int {i} = 0; {i} < {n}; {i}++)");
                Line(sb, ind, "{");
                EmitWrite(sb, ind + 1, c.Elem, $"{v}[{i}]", res, ref tmp);
                Line(sb, ind, "}");
                break;
            }
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    private static string PrimCSharp(Prim p) =>
        p switch
        {
            Prim.U8 => "byte",
            Prim.I8 => "sbyte",
            Prim.Bool => "bool",
            Prim.I16 => "short",
            Prim.U16 => "ushort",
            Prim.I32 => "int",
            Prim.U32 => "uint",
            Prim.I64 => "long",
            Prim.U64 => "ulong",
            Prim.F32 => "float",
            _ => "double",
        };

    // ---- Read ----
    private static void EmitRead(StringBuilder sb, int ind, Ty t, string target, Resolver res, ref int tmp)
    {
        switch (t)
        {
            case Ty.Primitive p:
                Line(sb, ind, $"{target} = r.{PrimMethod(p.Kind)}();");
                break;
            case Ty.QuantFloat q:
                Line(
                    sb,
                    ind,
                    q.Kind switch
                    {
                        Enc.Pos => $"{target} = {Q}.UnpackPos(r.I16());",
                        Enc.Half => $"{target} = {Q}.UnpackHalf(r.U16());",
                        _ =>
                            $"{target} = {Q}.UnpackAngle(r.I16(), {q.Range.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}f);",
                    }
                );
                break;
            case Ty.Vec3 vec:
            {
                string one = vec.Kind switch
                {
                    Enc.Pos => $"{Q}.UnpackPos(r.I16())",
                    Enc.Half => $"{Q}.UnpackHalf(r.U16())",
                    _ => "r.F32()",
                };
                Line(sb, ind, $"{target} = new global::{WireGenerator.Vec3Type}({one}, {one}, {one});");
                break;
            }
            case Ty.Quat qt:
                if (qt.Kind == Enc.Quat)
                {
                    string n = "__q" + tmp++;
                    Line(
                        sb,
                        ind,
                        $"{Q}.UnpackQuat(r.U32(), out float {n}x, out float {n}y, out float {n}z, out float {n}w);"
                    );
                    Line(sb, ind, $"{target} = new global::{WireGenerator.QuatType}({n}x, {n}y, {n}z, {n}w);");
                }
                else
                    Line(sb, ind, $"{target} = new global::{WireGenerator.QuatType}(r.F32(), r.F32(), r.F32(), r.F32());");
                break;
            case Ty.Str s:
                Line(
                    sb,
                    ind,
                    s.Kind switch
                    {
                        Enc.StrU8 => $"{target} = r.StrU8();",
                        Enc.Str7Bit => $"{target} = r.Str7();",
                        _ => $"{target} = r.Str();",
                    }
                );
                break;
            case Ty.Enum e:
                Line(sb, ind, $"{target} = ({e.TypeName})r.{PrimMethod(e.Underlying)}();");
                break;
            case Ty.Narrow n:
                Line(sb, ind, $"{target} = ({n.SourceType})r.{PrimMethod(n.Target)}();");
                break;
            case Ty.Nested nested:
                Line(sb, ind, $"{target} = {nested.TypeName}.Read(ref r);");
                break;
            case Ty.Nullable nu:
                Line(sb, ind, $"if (r.U8() == 0) {target} = null; else");
                Line(sb, ind, "{");
                if (nu.IsReference)
                    EmitRead(sb, ind + 1, nu.Elem, target, res, ref tmp);
                else
                {
                    string tv = "__nv" + tmp++;
                    Line(sb, ind + 1, $"{ElemTypeName(nu.Elem)} {tv} = default;");
                    EmitRead(sb, ind + 1, nu.Elem, tv, res, ref tmp);
                    Line(sb, ind + 1, $"{target} = {tv};");
                }
                Line(sb, ind, "}");
                break;
            case Ty.Coll c:
            {
                string n = "__n" + tmp++;
                Line(sb, ind, $"int {n} = (int)Math.Min((uint)r.{WidthMethod(c.Count)}(), (uint)int.MaxValue);");
                // A hostile count cannot allocate past the bytes actually present: every element is
                // at least one byte, so a count beyond Remaining is a malformed frame.
                Line(sb, ind, $"if ({n} > r.Remaining) {{ r.Fail(); {n} = 0; }}");
                if (c.IsByteArray)
                {
                    Line(sb, ind, $"{target} = r.Bytes({n});");
                    break;
                }
                // Lists are read into a concrete List<T> local (the field may be typed IReadOnlyList<T>).
                string lst = "__l" + tmp++;
                if (c.IsArray)
                    Line(sb, ind, $"{target} = {n} == 0 ? Array.Empty<{c.ElemTypeName}>() : new {c.ElemTypeName}[{n}];");
                else
                    Line(sb, ind, $"var {lst} = new List<{c.ElemTypeName}>({n});");
                string i = "__i" + tmp++;
                Line(sb, ind, $"for (int {i} = 0; {i} < {n} && !r.Failed; {i}++)");
                Line(sb, ind, "{");
                if (c.IsArray)
                    EmitRead(sb, ind + 1, c.Elem, $"{target}[{i}]", res, ref tmp);
                else
                {
                    string ev = "__e" + tmp++;
                    Line(sb, ind + 1, $"{c.ElemTypeName} {ev} = default;");
                    EmitRead(sb, ind + 1, c.Elem, ev, res, ref tmp);
                    Line(sb, ind + 1, $"{lst}.Add({ev});");
                }
                Line(sb, ind, "}");
                if (!c.IsArray)
                    Line(sb, ind, $"{target} = {lst};");
                break;
            }
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    private static string ElemTypeName(Ty t) =>
        t switch
        {
            Ty.Primitive p => PrimCSharp(p.Kind),
            Ty.QuantFloat => "float",
            Ty.Vec3 => "global::" + WireGenerator.Vec3Type,
            Ty.Quat => "global::" + WireGenerator.QuatType,
            Ty.Str => "string",
            Ty.Enum e => e.TypeName,
            Ty.Narrow n => n.SourceType,
            Ty.Nested n => n.TypeName,
            _ => "object",
        };
}
