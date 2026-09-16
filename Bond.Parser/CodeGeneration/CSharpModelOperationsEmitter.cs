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
        private const string ModelSupport = "global::BondTools.Models.";

        private string ModelSelf(StructDeclaration structure) =>
            QualifiedName(structure) + TypeArguments(structure.TypeParameters.Select(parameter =>
                Identifier(parameter.Name, structure.Location, typeName: true)));

        private string ModelInterfaces(StructDeclaration structure) =>
            $"{ModelSupport}IGeneratedModel, global::System.ICloneable, global::System.IEquatable<{ModelSelf(structure)}>";

        private void EmitModelAttributes(StructDeclaration structure)
        {
            Line(1, $"[global::System.Diagnostics.DebuggerDisplay({Literal(structure.Name)})]");
            Line(1, $"[global::System.Diagnostics.DebuggerTypeProxy(typeof({ModelSupport}GeneratedModelDebugView))]");
        }

        private void EmitModelMembers(StructDeclaration structure)
        {
            var self = ModelSelf(structure);
            var operations = QualifiedCompanionName(structure, "Operations") +
                TypeArguments(structure.TypeParameters.Select(parameter =>
                    Identifier(parameter.Name, structure.Location, typeName: true)));
            var fields = OperationFields(structure).ToArray();
            string Local(string name)
            {
                var candidate = "__bond" + name;
                while (structure.TypeParameters.Any(parameter => parameter.Name == candidate))
                    candidate += "_";
                return candidate;
            }
            var context = Local("Context");
            var clone = Local("Clone");
            var existing = Local("Existing");
            var other = Local("Other");
            var right = Local("Right");
            var hash = Local("Hash");
            var child = Local("Child");

            Line(0);
            Line(2, $"{ModelSupport}SchemaDescriptor {ModelSupport}IGeneratedModel.Descriptor => " +
                $"{QualifiedCompanionName(structure, "Schema")}.Descriptor;");
            Line(2, $"object global::System.ICloneable.Clone() => {ModelSupport}ModelOperations.Clone(this);");
            Line(2, $"bool global::System.IEquatable<{self}>.Equals({self} {other}) => " +
                $"{ModelSupport}ModelOperations.ValueEquals(this, {other});");
            Line(0);
            Line(2, $"{ModelSupport}IGeneratedModel {ModelSupport}IGeneratedModel.Clone({ModelSupport}CloneContext {context})");
            Line(2, "{");
            Line(3, $"if ({context}.TryGetClone<{self}>(this, out var {existing})) return {existing};");
            Line(3, $"if (((object)this).GetType() != typeof({self}))");
            Line(4, $"throw {ModelSupport}ModelOperations.MissingAdapter(((object)this).GetType());");
            Line(3, $"var {clone} = ({self}){ModelSupport}ModelOperations.ShallowClone(this);");
            Line(3, $"{context}.Register(this, {clone});");
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var name = Identifier(field.Field.Name, field.Field.Location);
                Line(3, $"(({field.Owner}){clone}).{name} = {operations}.Field{i}.Clone(" +
                    $"(({field.Owner})this).{name}, {context});");
            }
            Line(3, $"return {clone};");
            Line(2, "}");
            Line(0);
            Line(2, $"bool {ModelSupport}IGeneratedModel.ValueEquals({ModelSupport}IGeneratedModel {other}, " +
                $"{ModelSupport}EqualityContext {context})");
            Line(2, "{");
            Line(3, $"if ({other} is not {self} {right} || ((object)this).GetType() != ((object){right}).GetType()) return false;");
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var name = Identifier(field.Field.Name, field.Field.Location);
                Line(3, $"if (!{operations}.Field{i}.Equals((({field.Owner})this).{name}, " +
                    $"(({field.Owner}){right}).{name}, {context})) return false;");
            }
            Line(3, "return true;");
            Line(2, "}");
            Line(0);
            Line(2, $"int {ModelSupport}IGeneratedModel.ValueHashCode({ModelSupport}HashContext {context})");
            Line(2, "{");
            Line(3, $"if ({context}.RemainingDepth == 0) return 0;");
            Line(3, $"var {child} = {context}.Descend();");
            Line(3, $"var {hash} = 17;");
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                Line(3, $"{hash} = {ModelSupport}HashContext.Combine({hash}, {operations}.Field{i}.GetHashCode(" +
                    $"(({field.Owner})this).{Identifier(field.Field.Name, field.Field.Location)}, {child}));");
            }
            Line(3, $"return {hash};");
            Line(2, "}");
            Line(0);
            Line(2, $"{ModelSupport}ModelDebugField[] {ModelSupport}IGeneratedModel.GetDebugFields()");
            Line(2, "{");
            Line(3, $"return new {ModelSupport}ModelDebugField[]");
            Line(3, "{");
            foreach (var field in fields)
                Line(4, $"new {ModelSupport}ModelDebugField({Literal(field.DeclaringName)}, {Literal(field.Field.Name)}, " +
                    $"{field.Field.Ordinal.ToString(CultureInfo.InvariantCulture)}, " +
                    $"(({field.Owner})this).{Identifier(field.Field.Name, field.Field.Location)}),");
            Line(3, "};");
            Line(2, "}");

            if (CanEmitConvenience(structure, "Clone"))
            {
                var hiding = BaseStructures(structure).Any(parent => CanEmitConvenience(parent, "Clone")) ? "new " : "";
                Line(2, $"public {hiding}{self} Clone() => {ModelSupport}ModelOperations.Clone(this);");
            }
            if (CanEmitConvenience(structure, "Equals"))
            {
                Line(2, $"public bool Equals({self} {other}) => {ModelSupport}ModelOperations.ValueEquals(this, {other});");
                Line(2, $"public override bool Equals(object {other}) => {ModelSupport}ModelOperations.ValueEquals<object>(this, {other});");
            }
            if (CanEmitConvenience(structure, "GetHashCode"))
                Line(2, $"public override int GetHashCode() => {ModelSupport}ModelOperations.ValueHashCode(this);");
        }

        private bool CanEmitConvenience(StructDeclaration structure, string name) =>
            structure.Name != name &&
            !structure.TypeParameters.Any(parameter => parameter.Name == name) &&
            !structure.Fields.Any(field => field.Name == name) &&
            !BaseStructures(structure).Any(parent => parent.Fields.Any(field => field.Name == name));

        private void EmitModelCompanions(StructDeclaration structure)
        {
            var arguments = structure.TypeParameters.Select((parameter, index) =>
                (BondType)new BondType.TypeParameter(parameter with { Name = "__T" + index })).ToArray();
            var parameters = TypeArguments(arguments.Select(argument => MapType(argument, structure.Location).Name));
            var self = QualifiedName(structure) + parameters;
            var fields = OperationFields(structure, arguments).ToArray();
            Line(0, $"namespace {string.Join(".", CSharpNamespace(structure).Select(part => Identifier(part, structure.Location)))}");
            Line(0, "{");
            Line(1, $"public static class {Identifier(CompanionName(structure, "Operations"), structure.Location, typeName: true)}{parameters}");
            for (var i = 0; i < structure.TypeParameters.Length; i++)
                if (structure.TypeParameters[i].Constraint == TypeConstraint.Value)
                    Line(2, $"where __T{i} : struct");
            Line(1, "{");
            Line(2, $"public static {self} Clone({self} value) => {ModelSupport}ModelOperations.Clone(value);");
            Line(2, $"public static bool Equals({self} left, {self} right) => {ModelSupport}ModelOperations.ValueEquals(left, right);");
            Line(2, $"public static int GetHashCode({self} value) => {ModelSupport}ModelOperations.ValueHashCode(value);");
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var mapped = MapType(field.Type, field.Field.Location);
                Line(2, $"internal static readonly {ModelSupport}IModelAdapter<{mapped.Name}> Field{i} = " +
                    $"{ModelAdapterExpression(field.Type, field.Field.Location)};");
            }
            if (fields.Any(field => UsesMaterializedAdapter(field.Type, field.Field.Location)))
            {
                Line(2, "[global::System.Diagnostics.DebuggerDisplay(\"bonded (materialized)\")]");
                Line(2, $"private sealed class __MaterializedBonded<__Payload> : global::Bond.IBonded<__Payload>, {ModelSupport}IMaterializedModelValue<__Payload>");
                Line(2, "{");
                Line(3, "[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
                Line(3, "public __Payload Value { get; }");
                Line(3, "public __MaterializedBonded(__Payload value) { Value = value; }");
                Line(3, $"public __Payload Deserialize() => {ModelSupport}ModelOperations.Clone(Value);");
                Line(3, "public __U Deserialize<__U>() => typeof(__U) == typeof(__Payload)");
                Line(4, "? (__U)(object)Deserialize() : ((global::Bond.IBonded)new global::Bond.Bonded<__Payload>(Value)).Deserialize<__U>();");
                Line(3, "public void Serialize<__Writer>(__Writer writer) => ((global::Bond.IBonded)new global::Bond.Bonded<__Payload>(Value)).Serialize(writer);");
                Line(3, "public global::Bond.IBonded<__U> Convert<__U>() => this as global::Bond.IBonded<__U>;");
                Line(2, "}");
            }
            Line(1, "}");
            Line(0, "}");
            Line(0);
        }

        private sealed record OperationField(string Owner, string DeclaringName, Field Field, BondType Type);

        private IEnumerable<OperationField> OperationFields(StructDeclaration structure, BondType[]? arguments = null)
        {
            BondType? current = new BondType.TypeReference(structure,
                arguments ?? structure.TypeParameters.Select(parameter => (BondType)new BondType.TypeParameter(parameter)).ToArray());
            while (current != null)
            {
                var reference = (BondType.TypeReference)UnwrapAlias(current, structure.Location);
                if (Canonical(reference.Declaration) is not StructDeclaration declaration)
                    yield break;
                var owner = MapType(reference, structure.Location).Name;
                foreach (var field in declaration.Fields.OrderBy(field => field.Ordinal))
                    yield return new(owner, IdlFullName(declaration), field, Substitute(field.Type, declaration, reference.TypeArguments));
                current = declaration.BaseType is null ? null : Substitute(declaration.BaseType, declaration, reference.TypeArguments);
            }
        }

        private string ModelAdapterExpression(BondType type, SourceLocation location)
        {
            var mapped = MapType(type, location);
            var adapters = ModelSupport + "ModelAdapters.";
            if (mapped.IsCustom)
                return $"{adapters}Value<{mapped.Name}>()";
            if (type is BondType.TypeReference reference && Canonical(reference.Declaration) is AliasDeclaration alias)
                return ModelAdapterExpression(Substitute(alias.AliasedType, alias, reference.TypeArguments), location);
            string Element(BondType element) => ModelAdapterExpression(element, location);
            string Nullable(BondType element) => MapType(element, location).Name == mapped.Name
                ? Element(element) : $"{adapters}Nullable({Element(element)})";
            return type switch
            {
                BondType.Vector vector => $"{adapters}List({Element(vector.ElementType)})",
                BondType.List list => $"{adapters}LinkedList({Element(list.ElementType)})",
                BondType.Set set => $"{adapters}Set({Element(set.KeyType)})",
                BondType.Map map => $"{adapters}Map({Element(map.KeyType)}, {Element(map.ValueType)})",
                BondType.Blob => adapters + "Blob",
                BondType.Nullable nullable => Nullable(nullable.ElementType),
                BondType.Maybe maybe => Nullable(maybe.ElementType),
                BondType.Bonded bonded => $"{adapters}Materialized<{mapped.Name}, {MapType(bonded.StructType, location).Name}>(" +
                    $"static value => value is {ModelSupport}IMaterializedModelValue<{MapType(bonded.StructType, location).Name}> materialized ? materialized.Value : value.Deserialize(), " +
                    $"static value => new __MaterializedBonded<{MapType(bonded.StructType, location).Name}>(value), " +
                    $"{Element(bonded.StructType)})",
                _ => $"{adapters}Value<{mapped.Name}>()"
            };
        }

        private bool UsesMaterializedAdapter(BondType type, SourceLocation location)
        {
            if (MapType(type, location).IsCustom)
                return false;
            if (type is BondType.TypeReference reference && Canonical(reference.Declaration) is AliasDeclaration alias)
                return UsesMaterializedAdapter(Substitute(alias.AliasedType, alias, reference.TypeArguments), location);
            return type is BondType.Bonded || Children(type).Any(child => UsesMaterializedAdapter(child, location));
        }
    }
}
