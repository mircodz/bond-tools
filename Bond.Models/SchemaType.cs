using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace BondTools.Models;

/// <summary>The IDL type, independent of its CLR representation.</summary>
public enum SchemaTypeKind
{
    /// <summary>A signed 8-bit integer.</summary>
    Int8,
    /// <summary>A signed 16-bit integer.</summary>
    Int16,
    /// <summary>A signed 32-bit integer.</summary>
    Int32,
    /// <summary>A signed 64-bit integer.</summary>
    Int64,
    /// <summary>An unsigned 8-bit integer.</summary>
    UInt8,
    /// <summary>An unsigned 16-bit integer.</summary>
    UInt16,
    /// <summary>An unsigned 32-bit integer.</summary>
    UInt32,
    /// <summary>An unsigned 64-bit integer.</summary>
    UInt64,
    /// <summary>A 32-bit floating-point number.</summary>
    Float,
    /// <summary>A 64-bit floating-point number.</summary>
    Double,
    /// <summary>A Boolean.</summary>
    Bool,
    /// <summary>A UTF-8 string.</summary>
    String,
    /// <summary>A UTF-16 string, distinct from string even when both map to System.String.</summary>
    WString,
    /// <summary>A binary blob.</summary>
    Blob,
    /// <summary>The containing model's IDL name.</summary>
    MetaName,
    /// <summary>The containing model's qualified IDL name.</summary>
    MetaFullName,
    /// <summary>An IDL list.</summary>
    List,
    /// <summary>An IDL vector.</summary>
    Vector,
    /// <summary>An IDL set.</summary>
    Set,
    /// <summary>An IDL map.</summary>
    Map,
    /// <summary>An explicitly nullable value.</summary>
    Nullable,
    /// <summary>A value with the declared default nothing; not the nullable IDL type.</summary>
    Maybe,
    /// <summary>A deferred bonded value.</summary>
    Bonded,
    /// <summary>A named struct.</summary>
    Struct,
    /// <summary>A named enum.</summary>
    Enum,
    /// <summary>A named alias, without erasing its declaration.</summary>
    Alias,
    /// <summary>A generic parameter in the enclosing declaration.</summary>
    TypeParameter,
    /// <summary>A signed integer generic argument, not a CLR type.</summary>
    IntegerArgument
}

/// <summary>An immutable, symbolic IDL type expression. No CLR type discovery is performed.</summary>
public abstract class SchemaType
{
    private protected SchemaType(SchemaTypeKind kind) => Kind = kind;

    /// <summary>The exact IDL type category.</summary>
    public SchemaTypeKind Kind { get; }

    /// <summary>Formats the symbolic IDL type without resolving declaration factories.</summary>
    public override string ToString() => Kind switch
    {
        SchemaTypeKind.MetaName => "bond_meta::name",
        SchemaTypeKind.MetaFullName => "bond_meta::full_name",
        _ => Kind.ToString().ToLowerInvariant()
    };

    internal abstract SchemaType Substitute(IReadOnlyList<SchemaType> arguments);
}

/// <summary>A primitive, blob, or metadata-string type.</summary>
public sealed class PrimitiveSchemaType : SchemaType
{
    /// <summary>Creates a leaf type; container and declaration kinds are rejected.</summary>
    public PrimitiveSchemaType(SchemaTypeKind kind) : base(kind)
    {
        if (kind is < SchemaTypeKind.Int8 or > SchemaTypeKind.MetaFullName)
            throw new ArgumentOutOfRangeException(nameof(kind));
    }

    internal override SchemaType Substitute(IReadOnlyList<SchemaType> arguments) => this;
}

/// <summary>A list, vector, set, nullable, nothing-default, or bonded type.</summary>
public sealed class UnarySchemaType : SchemaType
{
    /// <summary>Creates a symbolic single-argument type without evaluating its argument.</summary>
    public UnarySchemaType(SchemaTypeKind kind, SchemaType elementType) : base(kind)
    {
        if (kind is not (SchemaTypeKind.List or SchemaTypeKind.Vector or SchemaTypeKind.Set
            or SchemaTypeKind.Nullable or SchemaTypeKind.Maybe or SchemaTypeKind.Bonded))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ElementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
    }

    /// <summary>The element, set key, or wrapped type.</summary>
    public SchemaType ElementType { get; }

    /// <inheritdoc />
    public override string ToString() => Kind == SchemaTypeKind.Maybe
        ? ElementType + " = nothing" : base.ToString() + "<" + ElementType + ">";

    internal override SchemaType Substitute(IReadOnlyList<SchemaType> arguments) =>
        new UnarySchemaType(Kind, ElementType.Substitute(arguments));
}

/// <summary>A map retaining separate symbolic key and value types.</summary>
public sealed class MapSchemaType : SchemaType
{
    /// <summary>Creates a map type.</summary>
    public MapSchemaType(SchemaType keyType, SchemaType valueType) : base(SchemaTypeKind.Map)
    {
        KeyType = keyType ?? throw new ArgumentNullException(nameof(keyType));
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
    }

    /// <summary>The map key type.</summary>
    public SchemaType KeyType { get; }

    /// <summary>The map value type.</summary>
    public SchemaType ValueType { get; }

    /// <inheritdoc />
    public override string ToString() => $"map<{KeyType}, {ValueType}>";

    internal override SchemaType Substitute(IReadOnlyList<SchemaType> arguments) =>
        new MapSchemaType(KeyType.Substitute(arguments), ValueType.Substitute(arguments));
}

/// <summary>A lazy declaration reference with explicitly supplied symbolic generic arguments.</summary>
public sealed class NamedSchemaType : SchemaType
{
    private readonly Lazy<SchemaDescriptor> _declaration;
    private readonly Lazy<SchemaDescriptor> _resolved;

    /// <summary>
    /// Creates a reference. The generated delegate is not called until Declaration or Resolve is used,
    /// allowing descriptors for recursive and mutually recursive models to initialize safely.
    /// </summary>
    public NamedSchemaType(SchemaTypeKind kind, string name, string @namespace,
        Func<SchemaDescriptor> declaration, IEnumerable<SchemaType>? typeArguments = null) : base(kind)
    {
        if (kind is not (SchemaTypeKind.Struct or SchemaTypeKind.Enum or SchemaTypeKind.Alias))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        ArgumentNullException.ThrowIfNull(declaration);
        TypeArguments = SchemaCollections.Freeze(typeArguments);
        _declaration = new Lazy<SchemaDescriptor>(
            () => declaration() ?? throw new InvalidOperationException("A declaration factory returned null."),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _resolved = new Lazy<SchemaDescriptor>(() => TypeArguments.Count == 0
            ? Declaration : Declaration.Bind(TypeArguments.ToArray()), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The referenced declaration's IDL name.</summary>
    public string Name { get; }

    /// <summary>The referenced declaration's IDL namespace, unaffected by C# mappings.</summary>
    public string Namespace { get; }

    /// <summary>The qualified IDL name. File-local aliases need not have globally unique names.</summary>
    public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

    /// <summary>The generic arguments, defensively copied and read-only.</summary>
    public IReadOnlyList<SchemaType> TypeArguments { get; }

    /// <summary>The original declaration template, obtained through a compiled delegate.</summary>
    public SchemaDescriptor Declaration => _declaration.Value;

    /// <summary>Resolves this reference and binds its explicit arguments, without traversing nested references.</summary>
    public SchemaDescriptor Resolve() => _resolved.Value;

    /// <inheritdoc />
    public override string ToString() => TypeArguments.Count == 0 ? FullName :
        FullName + "<" + string.Join(", ", TypeArguments) + ">";

    internal override SchemaType Substitute(IReadOnlyList<SchemaType> arguments) =>
        new NamedSchemaType(Kind, Name, Namespace, () => Declaration,
            TypeArguments.Select(argument => argument.Substitute(arguments)));
}

/// <summary>A positional parameter of the enclosing schema declaration, not a System.Type.</summary>
public sealed class TypeParameterSchemaType : SchemaType
{
    /// <summary>Creates a parameter reference using its zero-based declaration position.</summary>
    public TypeParameterSchemaType(int position, string name) : base(SchemaTypeKind.TypeParameter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        Position = position;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>The zero-based position in the declaration's TypeParameters.</summary>
    public int Position { get; }

    /// <summary>The declared parameter name.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ToString() => Name;

    internal override SchemaType Substitute(IReadOnlyList<SchemaType> arguments) =>
        Position < arguments.Count ? arguments[Position]
            : throw new ArgumentException("No argument was supplied for schema parameter " + Name + ".", nameof(arguments));
}

/// <summary>A signed literal generic argument, including arguments erased by an alias's CLR mapping.</summary>
public sealed class IntegerArgumentSchemaType : SchemaType
{
    /// <summary>Creates an integer type argument.</summary>
    public IntegerArgumentSchemaType(long value) : base(SchemaTypeKind.IntegerArgument) => Value = value;

    /// <summary>The declared signed integer value.</summary>
    public long Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    internal override SchemaType Substitute(IReadOnlyList<SchemaType> arguments) => this;
}
