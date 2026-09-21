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

        private string ModelInterfaces(StructDeclaration structure)
        {
            var interfaces = new List<string>();
            const CSharpModelFeatures coreFeatures = CSharpModelFeatures.Descriptors | CSharpModelFeatures.Cloning
                | CSharpModelFeatures.Equality | CSharpModelFeatures.Debugger;
            if ((options.ModelFeatures & coreFeatures) == coreFeatures)
            {
                interfaces.Add(ModelSupport + "IGeneratedModel");
            }
            else
            {
                if (options.GenerateDescriptors)
                {
                    interfaces.Add(ModelSupport + "IGeneratedSchemaProvider");
                }

                if (options.GenerateCloning)
                {
                    interfaces.Add(ModelSupport + "IGeneratedCloneable");
                }

                if (options.GenerateEquality)
                {
                    interfaces.Add(ModelSupport + "IGeneratedEquatable");
                }

                if (options.GenerateDebuggerSupport)
                {
                    interfaces.Add(ModelSupport + "IGeneratedDebugView");
                }
            }

            if (options.GenerateToString)
            {
                interfaces.Add(ModelSupport + "IGeneratedSummary");
            }

            if (options.GenerateCloning)
            {
                interfaces.Add("global::System.ICloneable");
            }

            if (options.GenerateEquality && CanEmitDefaultEquality(structure))
            {
                interfaces.Add($"global::System.IEquatable<{ModelSelf(structure)}>");
            }

            return string.Join(", ", interfaces);
        }

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
            var fields = options.GenerateCloning || options.GenerateEquality || options.GenerateDebuggerSupport
                ? OperationFields(structure).ToArray() : [];

            string Local(string name)
            {
                var candidate = "__bond" + name;
                while (structure.TypeParameters.Any(parameter => parameter.Name == candidate))
                {
                    candidate += "_";
                }

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
            if (options.GenerateDescriptors)
            {
                Line(2, $"{ModelSupport}SchemaDescriptor {ModelSupport}IGeneratedSchemaProvider.Descriptor => " +
                    $"{QualifiedCompanionName(structure, "Schema")}.Descriptor;");
            }

            if (options.GenerateCloning)
            {
                Line(2, $"object global::System.ICloneable.Clone() => {ModelSupport}ModelOperations.Clone(this);");
                Line(0);
                Line(2, $"{ModelSupport}IGeneratedCloneable {ModelSupport}IGeneratedCloneable.Clone({ModelSupport}CloneContext {context})");
                Line(2, "{");
                Line(3, $"if ({context}.TryGetClone<{self}>(this, out {self} {existing})) return {existing};");
                Line(3, $"if (((object)this).GetType() != typeof({self}))");
                Line(4, $"throw {ModelSupport}ModelOperations.MissingAdapter(((object)this).GetType());");
                Line(3, $"{self} {clone} = ({self}){ModelSupport}ModelOperations.ShallowClone(this);");
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
            }

            if (options.GenerateEquality)
            {
                if (CanEmitDefaultEquality(structure))
                {
                    Line(2, $"bool global::System.IEquatable<{self}>.Equals({self} {other}) => " +
                        $"{ModelSupport}ModelOperations.ValueEquals(this, {other});");
                }

                Line(2, $"bool {ModelSupport}IGeneratedEquatable.ValueEquals({ModelSupport}IGeneratedEquatable {other}, " +
                    $"{ModelSupport}EqualityContext {context})");
                Line(2, "{");
                Line(3, $"if (((object)this).GetType() != typeof({self}))");
                Line(4, $"throw {ModelSupport}ModelOperations.MissingAdapter(((object)this).GetType());");
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
                Line(2, $"int {ModelSupport}IGeneratedEquatable.ValueHashCode({ModelSupport}HashContext {context})");
                Line(2, "{");
                Line(3, $"if (((object)this).GetType() != typeof({self}))");
                Line(4, $"throw {ModelSupport}ModelOperations.MissingAdapter(((object)this).GetType());");
                Line(3, $"if ({context}.RemainingDepth == 0) return 0;");
                Line(3, $"{ModelSupport}HashContext {child} = {context}.Descend();");
                Line(3, $"int {hash} = 17;");
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    Line(3, $"{hash} = {ModelSupport}HashContext.Combine({hash}, {operations}.Field{i}.GetHashCode(" +
                        $"(({field.Owner})this).{Identifier(field.Field.Name, field.Field.Location)}, {child}));");
                }

                Line(3, $"return {hash};");
                Line(2, "}");
                Line(0);
            }

            if (options.GenerateDebuggerSupport)
            {
                Line(2, $"{ModelSupport}ModelDebugField[] {ModelSupport}IGeneratedDebugView.GetDebugFields()");
                Line(2, "{");
                Line(3, $"return new {ModelSupport}ModelDebugField[]");
                Line(3, "{");
                foreach (var field in fields)
                {
                    Line(4, $"new {ModelSupport}ModelDebugField({Literal(field.DeclaringName)}, {Literal(field.Field.Name)}, " +
                        $"{field.Field.Ordinal.ToString(CultureInfo.InvariantCulture)}, " +
                        $"(({field.Owner})this).{Identifier(field.Field.Name, field.Field.Location)}),");
                }

                Line(3, "};");
                Line(2, "}");
            }

            if (options.GenerateCloning && CanEmitConvenience(structure, "Clone"))
            {
                var hiding = BaseStructures(structure).Any(parent => CanEmitConvenience(parent, "Clone")) ? "new " : "";
                Line(2, $"public {hiding}{self} Clone() => {ModelSupport}ModelOperations.Clone(this);");
            }

            if (options.GenerateEquality && CanEmitDefaultEquality(structure))
            {
                Line(2, $"public bool Equals({self} {other}) => {ModelSupport}ModelOperations.ValueEquals(this, {other});");
                Line(2, $"public override bool Equals(object {other}) => {ModelSupport}ModelOperations.ValueEquals<object>(this, {other});");
            }

            if (options.GenerateEquality && CanEmitDefaultEquality(structure))
            {
                Line(2, $"public override int GetHashCode() => {ModelSupport}ModelOperations.ValueHashCode(this);");
            }

            if (options.GenerateToString)
            {
                EmitSummaryMembers(structure);
            }
        }

        private bool CanEmitConvenience(StructDeclaration structure, string name) =>
            structure.Name != name &&
            !structure.TypeParameters.Any(parameter => parameter.Name == name) &&
            !structure.Fields.Any(field => field.Name == name) &&
            !BaseStructures(structure).Any(parent => parent.Fields.Any(field => field.Name == name));

        private bool CanEmitDefaultEquality(StructDeclaration structure) =>
            CanEmitConvenience(structure, "Equals") && CanEmitConvenience(structure, "GetHashCode");

        private void EmitModelCompanions(StructDeclaration structure)
        {
            var arguments = structure.TypeParameters.Select((parameter, index) =>
                (BondType)new BondType.TypeParameter(parameter with
                {
                    Name = "__T" + index
                })).ToArray();
            var parameters = TypeArguments(arguments.Select(argument => MapType(argument, structure.Location).Name));
            var self = QualifiedName(structure) + parameters;
            var fields = OperationFields(structure, arguments).ToArray();

            Line(0, $"namespace {string.Join(".", CSharpNamespace(structure).Select(part => Identifier(part, structure.Location)))}");
            Line(0, "{");
            Line(1, $"public static class {Identifier(CompanionName(structure, "Operations"), structure.Location, typeName: true)}{parameters}");
            for (var i = 0; i < structure.TypeParameters.Length; i++)
            {
                if (structure.TypeParameters[i].Constraint == TypeConstraint.Value)
                {
                    Line(2, $"where __T{i} : struct");
                }
            }

            Line(1, "{");
            if (options.GenerateCloning)
            {
                Line(2, $"public static {self} Clone({self} value) => {ModelSupport}ModelOperations.Clone(value);");
            }

            if (options.GenerateEquality)
            {
                Line(2, $"public static bool Equals({self} left, {self} right) => {ModelSupport}ModelOperations.ValueEquals(left, right);");
                Line(2, $"public static int GetHashCode({self} value) => {ModelSupport}ModelOperations.ValueHashCode(value);");
            }

            if (options.GenerateToString)
            {
                EmitSummaryCompanionMembers(structure, self);
            }

            if (options.GenerateCloning || options.GenerateEquality)
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    var mapped = MapType(field.Type, field.Field.Location);
                    Line(2, $"internal static readonly {ModelSupport}IModelAdapter<{mapped.Name}> Field{i} = " +
                        $"{ModelAdapterExpression(field.Type, field.Field.Location)};");
                }
            }

            if ((options.GenerateCloning || options.GenerateEquality)
                && fields.Any(field => UsesMaterializedAdapter(field.Type, field.Field.Location)))
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
                {
                    throw new GenerationException(
                        $"Cannot generate model operations for '{structure.Name}' because base '{reference.Declaration.QualifiedName}' " +
                        "has no schema definition. Include its .bond definition or disable these operations.", structure.Location);
                }

                var owner = MapType(reference, structure.Location).Name;
                foreach (var field in declaration.Fields.OrderBy(field => field.Ordinal))
                {
                    yield return new(owner, IdlFullName(declaration), field, Substitute(field.Type, declaration, reference.TypeArguments));
                }

                current = declaration.BaseType is null ? null : Substitute(declaration.BaseType, declaration, reference.TypeArguments);
            }
        }

        private string ModelAdapterExpression(BondType type, SourceLocation location)
        {
            var mapped = MapType(type, location);
            var adapters = ModelSupport + "ModelAdapters.";
            if (mapped.IsCustom)
            {
                return $"{adapters}Value<{mapped.Name}>()";
            }

            if (type is BondType.TypeReference reference && Canonical(reference.Declaration) is AliasDeclaration alias)
            {
                return ModelAdapterExpression(Substitute(alias.AliasedType, alias, reference.TypeArguments), location);
            }

            if (type is BondType.TypeReference { TypeArguments.Length: > 0 } generic)
            {
                var arguments = new List<string>();
                foreach (var argument in generic.TypeArguments)
                {
                    var argumentName = MapType(argument, location).Name;
                    var adapter = ModelAdapterExpression(argument, location);
                    if (adapter != $"{adapters}Value<{argumentName}>()")
                    {
                        arguments.Add($"{ModelSupport}ModelAdapterArgument.Create<{argumentName}>({adapter})");
                    }
                }

                if (arguments.Count > 0)
                {
                    return $"{adapters}WithArguments({adapters}Value<{mapped.Name}>(), {string.Join(", ", arguments)})";
                }
            }

            string Element(BondType element) => ModelAdapterExpression(element, location);

            string Nullable(BondType element) => MapType(element, location).Name == mapped.Name
                ? Element(element) : $"{adapters}Nullable({Element(element)})";

            if (type is BondType.Bonded bonded)
            {
                var payloadType = MapType(bonded.StructType, location).Name;
                return $"{adapters}Materialized<{mapped.Name}, {payloadType}>(" +
                    $"static value => value is {ModelSupport}IMaterializedModelValue<{payloadType}> materialized ? materialized.Value : value.Deserialize(), " +
                    $"static value => new __MaterializedBonded<{payloadType}>(value), " +
                    $"{Element(bonded.StructType)})";
            }

            return type switch
            {
                BondType.Vector vector => $"{adapters}List({Element(vector.ElementType)})",
                BondType.List list => $"{adapters}LinkedList({Element(list.ElementType)})",
                BondType.Set set => $"{adapters}Set({Element(set.KeyType)})",
                BondType.Map map => $"{adapters}Map({Element(map.KeyType)}, {Element(map.ValueType)})",
                BondType.Blob => adapters + "Blob",
                BondType.Nullable nullable => Nullable(nullable.ElementType),
                BondType.Maybe maybe => Nullable(maybe.ElementType),
                _ => $"{adapters}Value<{mapped.Name}>()"
            };
        }

        private bool UsesMaterializedAdapter(BondType type, SourceLocation location)
        {
            if (MapType(type, location).IsCustom)
            {
                return false;
            }

            if (type is BondType.TypeReference reference && Canonical(reference.Declaration) is AliasDeclaration alias)
            {
                return UsesMaterializedAdapter(Substitute(alias.AliasedType, alias, reference.TypeArguments), location);
            }

            return type is BondType.Bonded || Children(type).Any(child => UsesMaterializedAdapter(child, location));
        }
    }
}
