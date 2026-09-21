using System;
using System.Collections.Generic;
using System.Linq;

namespace Bond.Parser.Syntax;

public abstract record BondType
{
    private BondType() { }

    // Primitive types
    public sealed record Int8 : BondType
    {
        public static readonly Int8 Instance = new();
        private Int8() { }
        public override string ToString() => "int8";
    }

    public sealed record Int16 : BondType
    {
        public static readonly Int16 Instance = new();
        private Int16() { }
        public override string ToString() => "int16";
    }

    public sealed record Int32 : BondType
    {
        public static readonly Int32 Instance = new();
        private Int32() { }
        public override string ToString() => "int32";
    }

    public sealed record Int64 : BondType
    {
        public static readonly Int64 Instance = new();
        private Int64() { }
        public override string ToString() => "int64";
    }

    public sealed record UInt8 : BondType
    {
        public static readonly UInt8 Instance = new();
        private UInt8() { }
        public override string ToString() => "uint8";
    }

    public sealed record UInt16 : BondType
    {
        public static readonly UInt16 Instance = new();
        private UInt16() { }
        public override string ToString() => "uint16";
    }

    public sealed record UInt32 : BondType
    {
        public static readonly UInt32 Instance = new();
        private UInt32() { }
        public override string ToString() => "uint32";
    }

    public sealed record UInt64 : BondType
    {
        public static readonly UInt64 Instance = new();
        private UInt64() { }
        public override string ToString() => "uint64";
    }

    public sealed record Float : BondType
    {
        public static readonly Float Instance = new();
        private Float() { }
        public override string ToString() => "float";
    }

    public sealed record Double : BondType
    {
        public static readonly Double Instance = new();
        private Double() { }
        public override string ToString() => "double";
    }

    public sealed record String : BondType
    {
        public static readonly String Instance = new();
        private String() { }
        public override string ToString() => "string";
    }

    public sealed record WString : BondType
    {
        public static readonly WString Instance = new();
        private WString() { }
        public override string ToString() => "wstring";
    }

    public sealed record Bool : BondType
    {
        public static readonly Bool Instance = new();
        private Bool() { }
        public override string ToString() => "bool";
    }

    // Container types
    public sealed record List(BondType ElementType) : BondType
    {
        public override string ToString() => $"list<{ElementType}>";
    }

    public sealed record Vector(BondType ElementType) : BondType
    {
        public override string ToString() => $"vector<{ElementType}>";
    }

    public sealed record Set(BondType KeyType) : BondType
    {
        public override string ToString() => $"set<{KeyType}>";
    }

    public sealed record Map(BondType KeyType, BondType ValueType) : BondType
    {
        public override string ToString() => $"map<{KeyType}, {ValueType}>";
    }

    public sealed record Nullable(BondType ElementType) : BondType
    {
        public override string ToString() => $"nullable<{ElementType}>";
    }

    public sealed record Blob : BondType
    {
        public static readonly Blob Instance = new();
        private Blob() { }
        public override string ToString() => "blob";
    }

    public sealed record Bonded(BondType StructType) : BondType
    {
        public override string ToString() => $"bonded<{StructType}>";
    }

    // Meta types
    public sealed record MetaName : BondType
    {
        public static readonly MetaName Instance = new();
        private MetaName() { }
        public override string ToString() => "bond_meta::name";
    }

    public sealed record MetaFullName : BondType
    {
        public static readonly MetaFullName Instance = new();
        private MetaFullName() { }
        public override string ToString() => "bond_meta::full_name";
    }

    // Resolved reference to a named declaration; TypeArguments hold the generic
    // instantiation (empty for non-generic refs).
    public sealed record TypeReference(Declaration Declaration, BondType[] TypeArguments) : BondType
    {
        public override string ToString()
        {
            var name = Declaration.Name;
            if (TypeArguments.Length > 0)
            {
                return $"{name}<{string.Join(", ", TypeArguments.Select(t => t.ToString()))}>";
            }

            return name;
        }

        // Identity is qualified name + element-wise type args. Synthesized record ==
        // would compare Declaration by reference, which differs across parses.
        public bool Equals(TypeReference? other) =>
            other is not null
            && Declaration.QualifiedName == other.Declaration.QualifiedName
            && TypeArguments.AsSpan().SequenceEqual(other.TypeArguments);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Declaration.QualifiedName);
            foreach (var t in TypeArguments)
            {
                hash.Add(t);
            }

            return hash.ToHashCode();
        }
    }

    public sealed record TypeParameter(TypeParam Param) : BondType
    {
        public override string ToString() => Param.Name;
    }

    // Inserted by AstBuilder when a field has a `nothing` default.
    public sealed record Maybe(BondType ElementType) : BondType
    {
        public override string ToString() => $"nullable<{ElementType}>";
    }

    // Integer literal as a generic type argument (e.g., vector<T, 32>).
    public sealed record IntTypeArg(long Value) : BondType
    {
        public override string ToString() => Value.ToString();
    }

    // Pre-resolution: a name plus type args, no Declaration yet. Replaced by
    // TypeReference during semantic analysis.
    public sealed record UnresolvedType(string[] QualifiedName, BondType[] TypeArguments) : BondType
    {
        public override string ToString()
        {
            var name = string.Join(".", QualifiedName);
            if (TypeArguments.Length > 0)
            {
                return $"{name}<{string.Join(", ", TypeArguments.Select(t => t.ToString()))}>";
            }

            return name;
        }

        // Synthesized record == compares the string[] and BondType[] by reference.
        public bool Equals(UnresolvedType? other) =>
            other is not null
            && QualifiedName.AsSpan().SequenceEqual(other.QualifiedName)
            && TypeArguments.AsSpan().SequenceEqual(other.TypeArguments);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var s in QualifiedName)
            {
                hash.Add(s);
            }

            foreach (var t in TypeArguments)
            {
                hash.Add(t);
            }

            return hash.ToHashCode();
        }
    }
}

public static class BondTypeExtensions
{
    /// <summary>Expands outer aliases, substituting arguments into each alias template.</summary>
    public static BondType ResolveAliases(this BondType type)
    {
        if (type is not BondType.TypeReference { Declaration: AliasDeclaration })
        {
            return type;
        }

        var visited = new HashSet<BondType.TypeReference>();
        while (type is BondType.TypeReference { Declaration: AliasDeclaration alias } reference)
        {
            if (!visited.Add(reference))
            {
                throw new InvalidOperationException($"Cyclic type alias '{alias.Name}'.");
            }

            type = alias.AliasedType.SubstituteTypeParameters(alias.TypeParameters, reference.TypeArguments);
        }

        return type;
    }

    /// <summary>Substitutes parameters in a type expression without changing referenced declaration templates.</summary>
    public static BondType SubstituteTypeParameters(this BondType type, TypeParam[] parameters, BondType[] arguments)
    {
        if (parameters.Length != arguments.Length)
        {
            throw new ArgumentException("The number of type arguments must match the number of type parameters.");
        }

        BondType Substitute(BondType value)
        {
            if (value is BondType.TypeParameter parameter)
            {
                var index = Array.FindIndex(parameters, p => p == parameter.Param);
                return index >= 0 ? arguments[index] : value;
            }

            return value switch
            {
                BondType.List list => new BondType.List(Substitute(list.ElementType)),
                BondType.Vector vector => new BondType.Vector(Substitute(vector.ElementType)),
                BondType.Set set => new BondType.Set(Substitute(set.KeyType)),
                BondType.Map map => new BondType.Map(Substitute(map.KeyType), Substitute(map.ValueType)),
                BondType.Nullable nullable => new BondType.Nullable(Substitute(nullable.ElementType)),
                BondType.Maybe maybe => new BondType.Maybe(Substitute(maybe.ElementType)),
                BondType.Bonded bonded => new BondType.Bonded(Substitute(bonded.StructType)),
                BondType.TypeReference reference => reference with { TypeArguments = reference.TypeArguments.Select(Substitute).ToArray() },
                BondType.UnresolvedType unresolved => unresolved with { TypeArguments = unresolved.TypeArguments.Select(Substitute).ToArray() },
                _ => value
            };
        }

        return Substitute(type);
    }

    public static bool IsScalar(this BondType type) =>
        type.ResolveAliases() is BondType.Int8 or BondType.Int16 or BondType.Int32 or BondType.Int64
            or BondType.UInt8 or BondType.UInt16 or BondType.UInt32 or BondType.UInt64
            or BondType.Float or BondType.Double or BondType.Bool
            or BondType.TypeParameter { Param.Constraint: TypeConstraint.Value }
            or BondType.TypeReference { Declaration: EnumDeclaration };

    public static bool IsString(this BondType type) =>
        type.ResolveAliases() is BondType.String or BondType.WString;

    public static bool IsEnum(this BondType type) =>
        type.ResolveAliases() is BondType.TypeReference { Declaration: EnumDeclaration };

    public static bool IsStruct(this BondType type) =>
        type.ResolveAliases() is BondType.TypeReference { Declaration: StructDeclaration or ForwardDeclaration };

    public static bool IsValidKeyType(this BondType type) =>
        type.IsScalar() || type.IsString() || type.ResolveAliases() is BondType.TypeParameter;
}
