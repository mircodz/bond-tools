using System.Linq;
using Bond.Parser.Syntax;
using Bond.Parser.Util;

namespace Bond.Parser.Parser;

/// <summary>
/// Validates default values against fully-resolved field types. Callers are
/// Aliases are expanded with their instantiated type arguments.
/// </summary>
public static class TypeValidator
{
    public static void ValidateType(BondType type, SourceLocation location)
    {
        switch (type)
        {
            case BondType.Set set:
                ValidateKey(set.KeyType, "set", location);
                break;
            case BondType.Map map:
                ValidateKey(map.KeyType, "map", location);
                ValidateType(map.ValueType, location);
                break;
            case BondType.List list: ValidateType(list.ElementType, location); break;
            case BondType.Vector vector: ValidateType(vector.ElementType, location); break;
            case BondType.Nullable nullable: ValidateType(nullable.ElementType, location); break;
            case BondType.Maybe maybe: ValidateType(maybe.ElementType, location); break;
            case BondType.Bonded bonded:
                var target = bonded.StructType.ResolveAliases();
                if (!target.IsStruct() && target is not BondType.TypeParameter)
                    throw new SemanticErrorException("A bonded type requires a struct", location);
                ValidateType(target, location);
                break;
            case BondType.TypeReference { Declaration: ServiceDeclaration }:
                throw new SemanticErrorException("A service cannot be used as a field or alias type", location);
            case BondType.TypeReference reference:
                foreach (var argument in reference.TypeArguments)
                    ValidateType(argument, location);
                break;
        }
    }

    private static void ValidateKey(BondType type, string container, SourceLocation location)
    {
        if (!type.ResolveAliases().IsValidKeyType())
            throw new SemanticErrorException($"Invalid {container} key type {type}", location);
        ValidateType(type, location);
    }

    public static bool ValidateDefaultValue(BondType fieldType, Default? defaultValue)
    {
        fieldType = fieldType.ResolveAliases();
        if (defaultValue == null) return true;
        if (fieldType is BondType.Maybe or BondType.Nullable) return defaultValue is Default.Nothing;
        if (fieldType is BondType.List or BondType.Set or BondType.Map or BondType.Vector) return defaultValue is Default.Nothing;

        return (fieldType, defaultValue) switch
        {
            (BondType.Int8,   Default.Integer i) => i.Value.IsInBounds<sbyte>(),
            (BondType.Int16,  Default.Integer i) => i.Value.IsInBounds<short>(),
            (BondType.Int32,  Default.Integer i) => i.Value.IsInBounds<int>(),
            (BondType.Int64,  Default.Integer i) => i.Value.IsInBounds<long>(),
            (BondType.UInt8,  Default.Integer i) => i.Value >= 0 && i.Value.IsInBounds<byte>(),
            (BondType.UInt16, Default.Integer i) => i.Value >= 0 && i.Value.IsInBounds<ushort>(),
            (BondType.UInt32, Default.Integer i) => i.Value >= 0 && i.Value.IsInBounds<uint>(),
            (BondType.UInt64, Default.Integer i) => i.Value >= 0 && i.Value.IsInBounds<ulong>(),
            (BondType.Float,  Default.Float or Default.Integer) => true, // int → float widening
            (BondType.Double, Default.Float or Default.Integer) => true, // int → double widening
            (BondType.Bool,   Default.Bool) => true,
            (BondType.String, Default.String) => true,
            (BondType.WString, Default.String) => true,
            (BondType.TypeReference { Declaration: EnumDeclaration e }, Default.Enum d) => e.Constants.Any(c => c.Name == d.Identifier),
            (BondType.TypeReference { Declaration: EnumDeclaration }, Default.Nothing) => true,
            (BondType.TypeParameter, _) => true,
            _ => false
        };
    }
}
