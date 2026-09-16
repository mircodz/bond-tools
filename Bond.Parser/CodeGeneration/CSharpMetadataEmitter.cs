using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bond.Parser.Syntax;

namespace Bond.Parser.CodeGeneration;

public static partial class CSharpGenerator
{
    private sealed partial class Emitter
    {
        private const string MetadataNamespace = "global::BondTools.Models.";
        private readonly Dictionary<Declaration, int> _metadataIndices = new(ReferenceEqualityComparer.Instance);
        private readonly List<Declaration> _metadataDeclarations = [];

        private void EmitMetadata()
        {
            var roots = new List<Declaration>();
            foreach (var root in ast.Declarations)
            {
                if (root is not (StructDeclaration or EnumDeclaration or AliasDeclaration))
                    continue;
                var declaration = Canonical(root);
                if (!_metadataIndices.ContainsKey(declaration))
                    roots.Add(declaration);
                RegisterMetadata(declaration);
            }
            for (var index = 0; index < _metadataDeclarations.Count; index++)
            {
                switch (_metadataDeclarations[index])
                {
                    case StructDeclaration structure:
                        if (structure.BaseType != null)
                            RegisterMetadataType(structure.BaseType);
                        foreach (var field in structure.Fields.OrderBy(field => field.Ordinal))
                            RegisterMetadataType(field.Type);
                        break;
                    case AliasDeclaration alias:
                        RegisterMetadataType(alias.AliasedType);
                        break;
                }
            }
            if (_metadataDeclarations.Count == 0)
                return;

            var catalogName = MetadataCatalogName();
            Line(0, $"file static class {catalogName}");
            Line(0, "{");
            Line(1, $"private static readonly global::System.Lazy<{MetadataNamespace}SchemaDescriptor>[] Descriptors =");
            Line(2, $"new global::System.Lazy<{MetadataNamespace}SchemaDescriptor>[]");
            Line(2, "{");
            for (var index = 0; index < _metadataDeclarations.Count; index++)
                Line(3, $"new global::System.Lazy<{MetadataNamespace}SchemaDescriptor>(Create{index.ToString(CultureInfo.InvariantCulture)}),");
            Line(2, "};");
            Line(0);
            Line(1, $"internal static {MetadataNamespace}SchemaDescriptor Get(int index) => Descriptors[index].Value;");
            Line(0);
            foreach (var declaration in _metadataDeclarations)
                EmitMetadataDefinition(declaration);
            Line(0, "}");
            Line(0);

            foreach (var declaration in roots.Where(declaration => declaration is StructDeclaration or EnumDeclaration))
            {
                var name = Identifier(CompanionName(declaration, "Schema"), declaration.Location, typeName: true);
                Line(0, $"namespace {string.Join(".", CSharpNamespace(declaration).Select(part => Identifier(part, declaration.Location)))}");
                Line(0, "{");
                Line(1, $"public static class {name}");
                Line(1, "{");
                Line(2, $"public static {MetadataNamespace}SchemaDescriptor Descriptor => global::{catalogName}.Get({MetadataIndex(declaration)});");
                Line(1, "}");
                Line(0, "}");
                Line(0);
            }
        }

        private void RegisterMetadata(Declaration declaration)
        {
            declaration = Canonical(declaration);
            if (_metadataIndices.TryAdd(declaration, _metadataDeclarations.Count))
                _metadataDeclarations.Add(declaration);
        }

        private void RegisterMetadataType(BondType type)
        {
            if (type is BondType.TypeReference reference)
                RegisterMetadata(reference.Declaration);
            foreach (var child in Children(type))
                RegisterMetadataType(child);
        }

        private string MetadataIndex(Declaration declaration) =>
            _metadataIndices[Canonical(declaration)].ToString(CultureInfo.InvariantCulture);

        private string MetadataCatalogName()
        {
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in _metadataDeclarations)
            {
                reserved.Add(declaration.Name);
                if (declaration is not AliasDeclaration)
                    foreach (var part in CSharpNamespace(declaration))
                        reserved.Add(part);
            }
            var name = "__BondSchemaCatalog";
            while (reserved.Contains(name))
                name += "_";
            return name;
        }

        private void EmitMetadataDefinition(Declaration declaration)
        {
            Line(1, $"private static {MetadataNamespace}SchemaDescriptor Create{MetadataIndex(declaration)}() =>");
            Line(2, $"new {MetadataNamespace}SchemaDescriptor(");
            Line(3, $"name: {Literal(declaration.Name)},");
            Line(3, $"@namespace: {Literal(string.Join(".", IdlNamespace(declaration)))},");
            var arguments = declaration.TypeParameters.Select(parameter => (BondType)new BondType.TypeParameter(parameter)).ToArray();
            var clrName = MapType(new BondType.TypeReference(declaration, arguments), declaration.Location).Name;
            Line(3, $"clrName: {Literal(clrName)},");
            Line(3, $"kind: {MetadataNamespace}SchemaKind.{MetadataKind(declaration)},");
            Line(3, $"typeParameters: {MetadataArray("TypeParameterDescriptor", declaration.TypeParameters.Select(parameter =>
                $"new {MetadataNamespace}TypeParameterDescriptor({Literal(parameter.Name)}, {MetadataNamespace}SchemaTypeConstraint.{parameter.Constraint})"))},");
            switch (declaration)
            {
                case StructDeclaration structure:
                    EmitStructMetadata(structure, 3);
                    break;
                case EnumDeclaration enumeration:
                    EmitEnumMetadata(enumeration, 3);
                    break;
                case AliasDeclaration alias:
                    Line(3, $"aliasedType: {MetadataType(alias.AliasedType, alias)});");
                    break;
                case ForwardDeclaration:
                    Line(3, $"fields: {MetadataArray("FieldDescriptor", [])});");
                    break;
            }
            Line(0);
        }

        private void EmitStructMetadata(StructDeclaration structure, int indent)
        {
            Line(indent, $"attributes: {MetadataAttributes(structure.Attributes)},");
            if (structure.BaseType != null)
                Line(indent, $"baseType: {MetadataType(structure.BaseType, structure)},");
            if (structure.IsView)
            {
                Line(indent, "isView: true,");
                Line(indent, $"viewTarget: new string[] {{ {string.Join(", ", (structure.ViewTarget ?? []).Select(Literal))} }},");
                Line(indent, $"viewFields: new string[] {{ {string.Join(", ", structure.ViewFields.Select(Literal))} }},");
            }
            Line(indent, $"fields: new {MetadataNamespace}FieldDescriptor[]");
            Line(indent, "{");
            foreach (var field in structure.Fields.OrderBy(field => field.Ordinal))
            {
                Line(indent + 1, $"new {MetadataNamespace}FieldDescriptor(");
                Line(indent + 2, $"{field.Ordinal.ToString(CultureInfo.InvariantCulture)}, {Literal(field.Name)},");
                Line(indent + 2, $"{MetadataType(field.Type, structure)},");
                Line(indent + 2, $"{MetadataNamespace}SchemaFieldModifier.{field.Modifier}, {MetadataDefault(field.DefaultValue)},");
                Line(indent + 2, $"{MetadataAttributes(field.Attributes)}),");
            }
            Line(indent, "});");
        }

        private void EmitEnumMetadata(EnumDeclaration enumeration, int indent)
        {
            Line(indent, $"attributes: {MetadataAttributes(enumeration.Attributes)},");
            Line(indent, $"enumValues: new {MetadataNamespace}EnumValueDescriptor[]");
            Line(indent, "{");
            long nextValue = 0;
            foreach (var constant in enumeration.Constants)
            {
                var value = constant.Value is long declared ? unchecked((int)declared) : (int)nextValue;
                var source = constant.Value is long literal ? MetadataInteger(literal) : "null";
                Line(indent + 1, $"new {MetadataNamespace}EnumValueDescriptor({Literal(constant.Name)}, {value.ToString(CultureInfo.InvariantCulture)}, {source}),");
                nextValue = (long)value + 1;
            }
            Line(indent, "});");
        }

        private string MetadataType(BondType type, Declaration owner)
        {
            string Unary(string kind, BondType element) =>
                $"new {MetadataNamespace}UnarySchemaType({MetadataNamespace}SchemaTypeKind.{kind}, {MetadataType(element, owner)})";

            switch (type)
            {
                case BondType.List list: return Unary("List", list.ElementType);
                case BondType.Vector vector: return Unary("Vector", vector.ElementType);
                case BondType.Set set: return Unary("Set", set.KeyType);
                case BondType.Nullable nullable: return Unary("Nullable", nullable.ElementType);
                case BondType.Maybe maybe: return Unary("Maybe", maybe.ElementType);
                case BondType.Bonded bonded: return Unary("Bonded", bonded.StructType);
                case BondType.Map map:
                    return $"new {MetadataNamespace}MapSchemaType({MetadataType(map.KeyType, owner)}, {MetadataType(map.ValueType, owner)})";
                case BondType.IntTypeArg integer:
                    return $"new {MetadataNamespace}IntegerArgumentSchemaType({MetadataInteger(integer.Value)})";
                case BondType.TypeParameter parameter:
                {
                    var position = Array.FindIndex(owner.TypeParameters, candidate => candidate == parameter.Param);
                    if (position < 0)
                        throw new GenerationException($"Schema type parameter '{parameter.Param.Name}' is not declared by '{owner.Name}'.", owner.Location);
                    return $"new {MetadataNamespace}TypeParameterSchemaType({position.ToString(CultureInfo.InvariantCulture)}, {Literal(parameter.Param.Name)})";
                }
                case BondType.TypeReference reference:
                {
                    var declaration = Canonical(reference.Declaration);
                    return $"new {MetadataNamespace}NamedSchemaType({MetadataNamespace}SchemaTypeKind.{MetadataKind(declaration)}, " +
                        $"{Literal(declaration.Name)}, {Literal(string.Join(".", IdlNamespace(declaration)))}, " +
                        $"static () => Get({MetadataIndex(declaration)}), " +
                        $"{MetadataArray("SchemaType", reference.TypeArguments.Select(argument => MetadataType(argument, owner)))})";
                }
                default:
                {
                    var kind = type switch
                    {
                        BondType.Int8 => "Int8",
                        BondType.Int16 => "Int16",
                        BondType.Int32 => "Int32",
                        BondType.Int64 => "Int64",
                        BondType.UInt8 => "UInt8",
                        BondType.UInt16 => "UInt16",
                        BondType.UInt32 => "UInt32",
                        BondType.UInt64 => "UInt64",
                        BondType.Float => "Float",
                        BondType.Double => "Double",
                        BondType.Bool => "Bool",
                        BondType.String => "String",
                        BondType.WString => "WString",
                        BondType.Blob => "Blob",
                        BondType.MetaName => "MetaName",
                        BondType.MetaFullName => "MetaFullName",
                        _ => throw new GenerationException($"Type '{type}' has no resolved schema metadata.", owner.Location)
                    };
                    return $"new {MetadataNamespace}PrimitiveSchemaType({MetadataNamespace}SchemaTypeKind.{kind})";
                }
            }
        }

        private static string MetadataKind(Declaration declaration) => declaration switch
        {
            StructDeclaration or ForwardDeclaration => "Struct",
            EnumDeclaration => "Enum",
            AliasDeclaration => "Alias",
            _ => throw new GenerationException($"Declaration '{declaration.Name}' is not a model schema.", declaration.Location)
        };

        private static string MetadataArray(string elementType, IEnumerable<string> items) =>
            $"new {MetadataNamespace}{elementType}[] {{ {string.Join(", ", items)} }}";

        private static string MetadataAttributes(Syntax.Attribute[] attributes) =>
            MetadataArray("SchemaAttribute", attributes.Select(attribute =>
                $"new {MetadataNamespace}SchemaAttribute({Literal(string.Join(".", attribute.QualifiedName))}, {Literal(attribute.Value)})"));

        private static string MetadataInteger(long value) => value.ToString(CultureInfo.InvariantCulture) + "L";

        private static string MetadataDefault(Default? value) => value switch
        {
            null => "null",
            Default.Bool boolean => $"new {MetadataNamespace}SchemaDefault.Boolean({(boolean.Value ? "true" : "false")})",
            Default.Integer integer => $"new {MetadataNamespace}SchemaDefault.Integer(global::System.Numerics.BigInteger.Parse(" +
                $"{Literal(integer.Value.ToString(CultureInfo.InvariantCulture))}, global::System.Globalization.CultureInfo.InvariantCulture))",
            Default.Float number => $"new {MetadataNamespace}SchemaDefault.FloatingPoint(global::System.BitConverter.Int64BitsToDouble(" +
                $"{MetadataInteger(BitConverter.DoubleToInt64Bits(number.Value))}))",
            Default.String text => $"new {MetadataNamespace}SchemaDefault.String({Literal(text.Value)})",
            Default.Enum member => $"new {MetadataNamespace}SchemaDefault.Enum({Literal(member.Identifier)})",
            Default.Nothing => $"{MetadataNamespace}SchemaDefault.Nothing.Instance",
            _ => throw new GenerationException("Unsupported schema default.", SourceLocation.Unknown)
        };
    }
}
