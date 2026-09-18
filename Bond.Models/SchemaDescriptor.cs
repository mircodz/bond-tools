using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BondTools.Models;

/// <summary>The kind of named IDL declaration.</summary>
public enum SchemaKind
{
    /// <summary>A struct, including a view.</summary>
    Struct,
    /// <summary>An enum.</summary>
    Enum,
    /// <summary>A file-local type alias.</summary>
    Alias
}

/// <summary>The declared field presence modifier.</summary>
public enum SchemaFieldModifier
{
    /// <summary>An optional field.</summary>
    Optional,
    /// <summary>A required field.</summary>
    Required,
    /// <summary>A required_optional field.</summary>
    RequiredOptional
}

/// <summary>The declared constraint on a generic schema parameter.</summary>
public enum SchemaTypeConstraint
{
    /// <summary>No value constraint.</summary>
    None,
    /// <summary>The IDL value constraint.</summary>
    Value
}

/// <summary>An uninterpreted source attribute. Duplicate names and declaration order are preserved.</summary>
public sealed record SchemaAttribute(string Name, string Value);

/// <summary>A generic template parameter and its source constraint.</summary>
public sealed record TypeParameterDescriptor(string Name, SchemaTypeConstraint Constraint = SchemaTypeConstraint.None);

/// <summary>An enum member, including its effective signed 32-bit value and optional source initializer.</summary>
public sealed record EnumValueDescriptor(string Name, int Value, long? DeclaredValue = null);

/// <summary>A declared default, never a constructed model, container, or inferred CLR default.</summary>
public abstract record SchemaDefault
{
    private SchemaDefault() { }

    /// <summary>A Boolean source literal.</summary>
    public sealed record Boolean(bool Value) : SchemaDefault;

    /// <summary>An integer source literal, without narrowing unsigned 64-bit values.</summary>
    public sealed record Integer(BigInteger Value) : SchemaDefault;

    /// <summary>A floating-point source literal, retaining double precision and the sign of zero.</summary>
    public sealed record FloatingPoint(double Value) : SchemaDefault;

    /// <summary>A decoded string source literal.</summary>
    public sealed record String(string Value) : SchemaDefault;

    /// <summary>An enum member name as written in the source default.</summary>
    public sealed record Enum(string Name) : SchemaDefault;

    /// <summary>The explicit nothing default, distinct from an omitted default.</summary>
    public sealed record Nothing : SchemaDefault
    {
        /// <summary>The immutable nothing value.</summary>
        public static Nothing Instance { get; } = new();
        private Nothing() { }
    }
}

/// <summary>An immutable declared field. Inherited fields belong to the explicit base descriptor.</summary>
public sealed class FieldDescriptor
{
    /// <summary>Creates a field from source metadata, defensively copying attributes.</summary>
    public FieldDescriptor(ushort id, string name, SchemaType type,
        SchemaFieldModifier modifier = SchemaFieldModifier.Optional, SchemaDefault? defaultValue = null,
        IEnumerable<SchemaAttribute>? attributes = null)
    {
        Id = id;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Modifier = modifier;
        DefaultValue = defaultValue;
        Attributes = SchemaCollections.Freeze(attributes);
    }

    /// <summary>The IDL field ordinal.</summary>
    public ushort Id { get; }
    /// <summary>The declared IDL field name.</summary>
    public string Name { get; }
    /// <summary>The symbolic schema type, retaining aliases and nothing wrappers.</summary>
    public SchemaType Type { get; }
    /// <summary>The source presence modifier.</summary>
    public SchemaFieldModifier Modifier { get; }
    /// <summary>The wire modifier, including Bond's required_optional rule for metadata-name fields.</summary>
    public SchemaFieldModifier EffectiveModifier => Type.Kind is SchemaTypeKind.MetaName or SchemaTypeKind.MetaFullName
        ? SchemaFieldModifier.RequiredOptional : Modifier;
    /// <summary>The declared default; null means no default was written.</summary>
    public SchemaDefault? DefaultValue { get; }
    /// <summary>The source attributes, in declaration order.</summary>
    public IReadOnlyList<SchemaAttribute> Attributes { get; }

    internal FieldDescriptor Substitute(IReadOnlyList<SchemaType> arguments) =>
        new(Id, Name, Type.Substitute(arguments), Modifier, DefaultValue, Attributes);
}

/// <summary>
/// Immutable generated metadata for an IDL declaration. Reading it does not instantiate a model,
/// inspect CLR types, or depend on a serialization runtime.
/// </summary>
public sealed class SchemaDescriptor
{
    /// <summary>Creates a declaration template, copying all supplied collections.</summary>
    public SchemaDescriptor(string name, string @namespace, string? clrName, SchemaKind kind,
        IEnumerable<FieldDescriptor>? fields = null, SchemaType? baseType = null,
        IEnumerable<TypeParameterDescriptor>? typeParameters = null,
        IEnumerable<EnumValueDescriptor>? enumValues = null, IEnumerable<SchemaAttribute>? attributes = null,
        SchemaType? aliasedType = null, bool isView = false, IEnumerable<string>? viewTarget = null,
        IEnumerable<string>? viewFields = null, bool isForward = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        ClrName = clrName;
        Kind = kind;
        IsForward = isForward;
        Fields = SchemaCollections.Freeze(SchemaCollections.Freeze(fields).OrderBy(field => field.Id));
        if (Fields.Select(field => field.Id).Distinct().Count() != Fields.Count)
            throw new ArgumentException("A schema cannot declare duplicate field ordinals.", nameof(fields));
        BaseType = baseType;
        TypeParameters = SchemaCollections.Freeze(typeParameters);
        EnumValues = SchemaCollections.Freeze(enumValues);
        Attributes = SchemaCollections.Freeze(attributes);
        AliasedType = aliasedType;
        IsView = isView;
        ViewTarget = SchemaCollections.Freeze(viewTarget);
        ViewFields = SchemaCollections.Freeze(viewFields);
        TypeArguments = SchemaCollections.Freeze<SchemaType>(null);
        Definition = this;
    }

    private SchemaDescriptor(SchemaDescriptor definition, IReadOnlyList<SchemaType> arguments)
        : this(definition.Name, definition.Namespace, definition.ClrName, definition.Kind,
            definition.Fields.Select(field => field.Substitute(arguments)),
            definition.BaseType?.Substitute(arguments), definition.TypeParameters, definition.EnumValues,
            definition.Attributes, definition.AliasedType?.Substitute(arguments), definition.IsView,
            definition.ViewTarget, definition.ViewFields, definition.IsForward)
    {
        Definition = definition;
        TypeArguments = arguments;
    }

    /// <summary>The original IDL declaration name.</summary>
    public string Name { get; }
    /// <summary>The IDL namespace, unaffected by C# namespace and type mappings.</summary>
    public string Namespace { get; }
    /// <summary>The qualified IDL name. Alias identity is the descriptor, not this potentially shadowed name.</summary>
    public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;
    /// <summary>
    /// The mapped C# representation of the declaration template, including generic parameter names.
    /// Binding preserves this template name; actual schema arguments are available in TypeArguments.
    /// Null means the symbolic template has no representable CLR type, for example an erased integer argument alias.
    /// </summary>
    public string? ClrName { get; }
    /// <summary>The named declaration category.</summary>
    public SchemaKind Kind { get; }
    /// <summary>
    /// True when only a forward declaration is available. Its fields and base layout are unknown,
    /// not an empty struct definition, and are never discovered by inspecting the CLR type.
    /// </summary>
    public bool IsForward { get; }
    /// <summary>Own declared fields sorted by ordinal, excluding inherited fields; unavailable on forward declarations.</summary>
    public IReadOnlyList<FieldDescriptor> Fields { get; }
    /// <summary>The explicit symbolic base type, if any.</summary>
    public SchemaType? BaseType { get; }
    /// <summary>The declaration's generic parameters in source order.</summary>
    public IReadOnlyList<TypeParameterDescriptor> TypeParameters { get; }
    /// <summary>Enum members in declaration order, including repeated numeric values.</summary>
    public IReadOnlyList<EnumValueDescriptor> EnumValues { get; }
    /// <summary>Declaration attributes in source order.</summary>
    public IReadOnlyList<SchemaAttribute> Attributes { get; }
    /// <summary>The alias's symbolic target without erasing nested aliases.</summary>
    public SchemaType? AliasedType { get; }
    /// <summary>Whether this struct was declared as a view.</summary>
    public bool IsView { get; }
    /// <summary>The view target's qualified-name segments as written in the IDL.</summary>
    public IReadOnlyList<string> ViewTarget { get; }
    /// <summary>The view's field selection as written, including repeated or unmatched selectors.</summary>
    public IReadOnlyList<string> ViewFields { get; }
    /// <summary>The unbound declaration template; identical to this descriptor when unbound.</summary>
    public SchemaDescriptor Definition { get; }
    /// <summary>Explicitly supplied binding arguments; empty on a declaration template.</summary>
    public IReadOnlyList<SchemaType> TypeArguments { get; }

    /// <summary>
    /// Binds a template using explicit symbolic arguments. Substitution is simultaneous, preserves
    /// defaults and declaration identity, and does not eagerly resolve recursive references.
    /// The argument count is checked; source constraints remain available in TypeParameters.
    /// </summary>
    public SchemaDescriptor Bind(params SchemaType[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length != TypeParameters.Count)
            throw new ArgumentException("The number of schema arguments must match the declaration's parameters.", nameof(arguments));
        return arguments.Length == 0 ? Definition : new SchemaDescriptor(Definition, SchemaCollections.Freeze(arguments));
    }

    /// <summary>
    /// Creates a symbolic reference to this declaration for use as an explicit generic argument.
    /// On a bound descriptor, omitting arguments preserves its existing binding.
    /// </summary>
    public NamedSchemaType AsType(params SchemaType[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var supplied = arguments.Length == 0 && TypeArguments.Count != 0
            ? TypeArguments : SchemaCollections.Freeze(arguments);
        if (supplied.Count != TypeParameters.Count)
            throw new ArgumentException("The number of schema arguments must match the declaration's parameters.", nameof(arguments));
        var kind = Kind switch
        {
            SchemaKind.Struct => SchemaTypeKind.Struct,
            SchemaKind.Enum => SchemaTypeKind.Enum,
            SchemaKind.Alias => SchemaTypeKind.Alias,
            _ => throw new InvalidOperationException("Unknown declaration kind.")
        };
        return new NamedSchemaType(kind, Name, Namespace, () => Definition, supplied);
    }
}

internal static class SchemaCollections
{
    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values)
    {
        var copy = values?.ToList() ?? new List<T>();
        if (copy.Any(value => value is null))
            throw new ArgumentException("Schema collections cannot contain null values.", nameof(values));
        return copy.AsReadOnly();
    }
}
