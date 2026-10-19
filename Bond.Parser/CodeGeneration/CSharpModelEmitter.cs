using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bond.Parser.Syntax;

namespace Bond.Parser.CodeGeneration;

public static partial class CSharpGenerator
{
    private sealed partial class Emitter
    {
        private const string Enumerable = "global::System.Linq.Enumerable.";
        private const string StructuralComparer = "global::System.Collections.StructuralComparisons.StructuralEqualityComparer";
        private const string InvariantCulture = "global::System.Globalization.CultureInfo.InvariantCulture";

        private int _modelVariables;

        /// <summary>A field of a struct or of one of its bases, typed with the struct's base type arguments.</summary>
        private sealed record ModelField(string Label, string Name, BondType Type, SourceLocation Location, string? Owner,
            Field Declaration, StructDeclaration Declarer)
        {
            // A field hidden by a derived field with the same name is accessed through its declaring type.
            public string On(string instance) => Owner == null ? $"{instance}.{Name}" : $"(({Owner}){instance}).{Name}";
        }

        private void EmitModelMembers(StructDeclaration structure, string self)
        {
            var fields = ModelFields(structure);
            var reserved = ModelMemberNames().ToHashSet();
            var names = fields.Select(field => (field.Name, field.Location))
                .Append((structure.Name, structure.Location))
                .Concat(structure.TypeParameters.Select(parameter => (parameter.Name, structure.Location)));
            foreach (var (name, location) in names)
            {
                if (reserved.Contains(name))
                {
                    Fail($"'{name}' conflicts with the generated {name}() method.", location);
                }
            }

            if (options.GenerateEquality)
            {
                EmitEquals(self, fields);
                EmitGetHashCode(fields);
            }

            if (options.GenerateToString)
            {
                EmitToString(structure.Name, fields);
            }

            if (options.GenerateClear)
            {
                EmitClear(structure, fields);
            }
        }

        private IEnumerable<string> ModelMemberNames()
        {
            if (options.GenerateEquality)
            {
                yield return "Equals";
                yield return "GetHashCode";
            }

            if (options.GenerateToString)
            {
                yield return "ToString";
            }

            if (options.GenerateClear)
            {
                yield return "Clear";
            }
        }

        // Fields of the struct and all of its bases, base first. Operations never depend on members generated for bases.
        private List<ModelField> ModelFields(StructDeclaration structure)
        {
            var chain = new List<(StructDeclaration Declaration, BondType.TypeReference Reference)>();
            BondType? current = new BondType.TypeReference(structure,
                structure.TypeParameters.Select(parameter => (BondType)new BondType.TypeParameter(parameter)).ToArray());
            while (current != null)
            {
                var reference = (BondType.TypeReference)UnwrapAlias(current, structure.Location);
                if (Canonical(reference.Declaration) is not StructDeclaration declaration)
                {
                    throw new GenerationException(
                        $"Cannot generate model members for '{structure.Name}' because base '{reference.Declaration.QualifiedName}' " +
                        "has no definition. Include its .bond file or disable model features.", structure.Location);
                }

                chain.Add((declaration, reference));
                current = declaration.BaseType == null ? null : Substitute(declaration.BaseType, declaration, reference.TypeArguments);
            }

            var duplicates = chain.SelectMany(link => link.Declaration.Fields)
                .GroupBy(field => field.Name)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet();
            var derivedNames = new HashSet<string>();
            var fields = new List<ModelField>();
            foreach (var (declaration, reference) in chain)
            {
                var owner = MapType(reference, structure.Location).Name;
                fields.InsertRange(0, declaration.Fields.OrderBy(field => field.Ordinal).Select(field => new ModelField(
                    duplicates.Contains(field.Name) ? declaration.Name + "." + field.Name : field.Name,
                    Identifier(field.Name, field.Location),
                    Substitute(field.Type, declaration, reference.TypeArguments),
                    field.Location,
                    derivedNames.Contains(field.Name) ? owner : null,
                    field,
                    declaration)));
                derivedNames.UnionWith(declaration.Fields.Select(field => field.Name));
            }

            return fields;
        }

        private void EmitEquals(string self, IReadOnlyList<ModelField> fields)
        {
            _modelVariables = 0;
            Line(0);
            Line(2, "public override bool Equals(object obj)");
            Line(2, "{");
            Line(3, "if (object.ReferenceEquals(this, obj))");
            Line(3, "{");
            Line(4, "return true;");
            Line(3, "}");
            Line(0);
            Line(3, "if (obj == null || ((object)this).GetType() != obj.GetType())");
            Line(3, "{");
            Line(4, "return false;");
            Line(3, "}");
            Line(0);
            if (fields.Count == 0)
            {
                Line(3, "return true;");
            }
            else
            {
                Line(3, $"{self} other = ({self})obj;");
                for (var i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    var comparison = EqualsValue(field.Type, field.On("this"), field.On("other"), field.Location);
                    Line(i == 0 ? 3 : 4, (i == 0 ? "return " : "&& ") + comparison + (i == fields.Count - 1 ? ";" : ""));
                }
            }

            Line(2, "}");
        }

        private void EmitGetHashCode(IReadOnlyList<ModelField> fields)
        {
            _modelVariables = 0;
            var hashes = fields
                .Select(field => HashValue(field.Type, field.On("this"), field.Location))
                .OfType<string>()
                .ToList();

            Line(0);
            Line(2, "public override int GetHashCode()");
            Line(2, "{");
            Line(3, "unchecked");
            Line(3, "{");
            Line(4, "int hash = 17;");
            foreach (var value in hashes)
            {
                Line(4, $"hash = hash * 31 + {value};");
            }

            Line(4, "return hash;");
            Line(3, "}");
            Line(2, "}");
        }

        private void EmitToString(string name, IReadOnlyList<ModelField> fields)
        {
            _modelVariables = 0;
            Line(0);
            Line(2, "public override string ToString()");
            Line(2, "{");
            if (fields.Count == 0)
            {
                Line(3, $"return {Literal(name + " { }")};");
            }
            else
            {
                Line(3, $"return {Literal(name + " { ")}");
                for (var i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    var label = (i == 0 ? "" : ", ") + field.Label + " = ";
                    Line(4, $"+ {Literal(label)} + {FormatValue(field.Type, field.On("this"), field.Location)}");
                }

                Line(4, "+ \" }\";");
            }

            Line(2, "}");
        }

        // Resets every field to its default, as the constructor sets it. Collections are emptied and structs generated
        // alongside this one cleared in place, so a cleared instance keeps its storage for the next read.
        private void EmitClear(StructDeclaration structure, IReadOnlyList<ModelField> fields)
        {
            Line(0);
            Line(2, $"public {(structure.BaseType != null ? "new " : "")}void Clear()");
            Line(2, "{");
            foreach (var field in fields)
            {
                var type = UnwrapAlias(field.Type, field.Location);
                if (IsMetaType(type))
                {
                    // The constructor names inherited metadata fields after this struct too; reads overwrite them.
                    var name = type is BondType.MetaName ? structure.Name : IdlFullName(structure);
                    Line(3, $"{field.On("this")} = {Literal(name)};");
                    continue;
                }

                var mapped = MapType(field.Type, field.Location);
                var value = FieldDefaultValue(field.Declaration with { Type = field.Type }, mapped, field.Declarer) ?? "default";
                var target = field.On("this");
                if (value != "default" && !mapped.IsCustom && ClearsInPlace(type))
                {
                    Line(3, $"if ({target} != null)");
                    Line(3, "{");
                    Line(4, $"{target}.Clear();");
                    Line(3, "}");
                    Line(3, "else");
                    Line(3, "{");
                    Line(4, $"{target} = {value};");
                    Line(3, "}");
                }
                else
                {
                    Line(3, $"{target} = {value};");
                }
            }

            Line(2, "}");
        }

        // Structs from other files may have been generated without Clear, so only this file's are cleared in place.
        private bool ClearsInPlace(BondType type) => type switch
        {
            BondType.Vector or BondType.List or BondType.Set or BondType.Map => true,
            BondType.TypeReference { Declaration: StructDeclaration declaration } => ast.Declarations
                .OfType<StructDeclaration>().Any(local => local.QualifiedName == declaration.QualifiedName),
            _ => false
        };

        // Returns the type that determines how a value is compared, hashed and formatted: aliases, nullable and nothing
        // wrappers removed. Null means a value whose type is unknown to the generator (custom-mapped CLR types and type
        // parameters): it is compared structurally.
        private BondType? ModelType(BondType type, SourceLocation location)
        {
            if (MapType(type, location).IsCustom)
            {
                return null;
            }

            while (true)
            {
                switch (UnwrapAlias(type, location))
                {
                    case BondType.Nullable nullable:
                        type = nullable.ElementType;
                        break;
                    case BondType.Maybe maybe:
                        type = maybe.ElementType;
                        break;
                    case BondType.TypeParameter:
                        return null;
                    case var unwrapped:
                        return unwrapped;
                }
            }
        }

        private string EqualsValue(BondType type, string left, string right, SourceLocation location)
        {
            switch (ModelType(type, location))
            {
                case null:
                    return $"{StructuralComparer}.Equals({left}, {right})";
                case BondType.Vector vector:
                    return SequenceEquals(left, right, vector.ElementType, location);
                case BondType.List list:
                    return SequenceEquals(left, right, list.ElementType, location);
                case BondType.Set:
                    return $"({left} == null ? {right} == null : {right} != null && {left}.SetEquals({right}))";
                case BondType.Map map:
                    {
                        var entry = Fresh("entry");
                        var item = Fresh("item");
                        var equal = EqualsValue(map.ValueType, $"{entry}.Value", item, location);
                        return $"({left} == null ? {right} == null : {right} != null && {left}.Count == {right}.Count && " +
                            $"{Enumerable}All({left}, {entry} => {right}.TryGetValue({entry}.Key, out {MapType(map.ValueType, location).Name} {item}) && {equal}))";
                    }
                case BondType.Blob:
                    return $"({left}.Array == null ? {right}.Array == null : " +
                        $"{right}.Array != null && {Enumerable}SequenceEqual({left}, {right}))";
                case BondType.Bonded:
                    return $"{StructuralComparer}.Equals({left} == null ? null : (object){left}.Deserialize(), " +
                        $"{right} == null ? null : (object){right}.Deserialize())";
                default:
                    return $"global::System.Collections.Generic.EqualityComparer<{MapType(type, location).Name}>.Default.Equals({left}, {right})";
            }
        }

        private string SequenceEquals(string left, string right, BondType element, SourceLocation location)
        {
            string items;
            if (ModelType(element, location) is not (null or BondType.Vector or BondType.List or BondType.Set
                or BondType.Map or BondType.Blob or BondType.Bonded))
            {
                // EqualityComparer<T>.Default matches the generated equality for values and generated structs.
                items = $"{Enumerable}SequenceEqual({left}, {right})";
            }
            else
            {
                var x = Fresh("left");
                var y = Fresh("right");
                var equal = Fresh("equal");
                items = $"{left}.Count == {right}.Count && {Enumerable}All({Enumerable}Zip({left}, {right}, " +
                    $"({x}, {y}) => {EqualsValue(element, x, y, location)}), {equal} => {equal})";
            }

            return $"({left} == null ? {right} == null : {right} != null && {items})";
        }

        // Null means the value does not contribute to the hash: bonded payloads are compared, but never deserialized to hash.
        private string? HashValue(BondType type, string value, SourceLocation location)
        {
            switch (ModelType(type, location))
            {
                case null:
                    return $"{StructuralComparer}.GetHashCode({value})";
                case BondType.Vector vector:
                    return HashItems(value, vector.ElementType, ordered: true, location);
                case BondType.List list:
                    return HashItems(value, list.ElementType, ordered: true, location);
                case BondType.Set set:
                    return HashItems(value, set.KeyType, ordered: false, location);
                case BondType.Map map:
                    {
                        var hash = Fresh("hash");
                        var entry = Fresh("entry");
                        var key = HashValue(map.KeyType, $"{entry}.Key", location) ?? "0";
                        var item = HashValue(map.ValueType, $"{entry}.Value", location) ?? "0";
                        return $"({value} == null ? 0 : {Enumerable}Aggregate({value}, 0, " +
                            $"({hash}, {entry}) => {hash} + ({key}) * 31 + {item}))";
                    }
                case BondType.Blob:
                    return $"{value}.Count";
                case BondType.Bonded:
                    return null;
                default:
                    return $"global::System.Collections.Generic.EqualityComparer<{MapType(type, location).Name}>.Default.GetHashCode({value})";
            }
        }

        private string HashItems(string value, BondType element, bool ordered, SourceLocation location)
        {
            var hash = Fresh("hash");
            var item = Fresh("item");
            var itemHash = HashValue(element, item, location) ?? "0";
            var step = ordered ? $"{hash} * 31 + {itemHash}" : $"{hash} + {itemHash}";
            return $"({value} == null ? 0 : {Enumerable}Aggregate({value}, {(ordered ? "17" : "0")}, ({hash}, {item}) => {step}))";
        }

        private string FormatValue(BondType type, string value, SourceLocation location)
        {
            switch (ModelType(type, location))
            {
                case BondType.String or BondType.WString or BondType.MetaName or BondType.MetaFullName:
                    return $"({value} == null ? \"null\" : \"\\\"\" + {value} + \"\\\"\")";
                case BondType.Vector vector:
                    return FormatItems(value, vector.ElementType, location);
                case BondType.List list:
                    return FormatItems(value, list.ElementType, location);
                case BondType.Set set:
                    return FormatItems(value, set.KeyType, location);
                case BondType.Map map:
                    {
                        var entry = Fresh("entry");
                        var key = FormatValue(map.KeyType, $"{entry}.Key", location);
                        var item = FormatValue(map.ValueType, $"{entry}.Value", location);
                        return $"({value} == null ? \"null\" : \"{{\" + string.Join(\", \", " +
                            $"{Enumerable}Select({value}, {entry} => {key} + \" = \" + {item})) + \"}}\")";
                    }
                case BondType.Blob:
                    return $"(\"blob[\" + {value}.Count + \"]\")";
                case BondType.Bonded:
                    return "\"bonded\"";
                default:
                    return $"global::System.Convert.ToString((object){value} ?? \"null\", {InvariantCulture})";
            }
        }

        private string FormatItems(string value, BondType element, SourceLocation location)
        {
            var item = Fresh("item");
            return $"({value} == null ? \"null\" : \"[\" + string.Join(\", \", " +
                $"{Enumerable}Select({value}, {item} => {FormatValue(element, item, location)})) + \"]\")";
        }

        private string Fresh(string name) => name + (_modelVariables++).ToString(CultureInfo.InvariantCulture);
    }
}
