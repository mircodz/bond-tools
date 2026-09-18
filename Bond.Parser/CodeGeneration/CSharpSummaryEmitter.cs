using System.Globalization;
using Bond.Parser.Syntax;

namespace Bond.Parser.CodeGeneration;

public static partial class CSharpGenerator
{
    private sealed partial class Emitter
    {
        private void EmitSummaryMembers(StructDeclaration structure)
        {
            if (!options.GenerateToString) return;
            Line(0);
            Line(2, $"string {ModelSupport}IGeneratedSummary.SummaryName => {Literal(structure.Name)};");
            Line(2, $"{ModelSupport}ModelDebugField[] {ModelSupport}IGeneratedSummary.GetSummaryFields()");
            Line(2, "{");
            Line(3, $"return new {ModelSupport}ModelDebugField[]");
            Line(3, "{");
            foreach (var field in OperationFields(structure))
            {
                var value = $"(({field.Owner})this).{Identifier(field.Field.Name, field.Field.Location)}";
                if (UnwrapAlias(field.Type, field.Field.Location) is BondType.Bonded)
                    value = $"{ModelSupport}ModelSummary.Opaque({value})";
                Line(4, $"new {ModelSupport}ModelDebugField({Literal(field.DeclaringName)}, {Literal(field.Field.Name)}, " +
                    $"{field.Field.Ordinal.ToString(CultureInfo.InvariantCulture)}, {value}),");
            }
            Line(3, "};");
            Line(2, "}");
            if (CanEmitConvenience(structure, "ToString"))
                Line(2, $"public override string ToString() => {ModelSupport}ModelSummary.Format(this);");
        }

        private void EmitSummaryCompanionMembers(StructDeclaration _, string self)
        {
            if (options.GenerateToString)
                Line(2, $"public static string ToString({self} value) => {ModelSupport}ModelSummary.Format(value);");
        }
    }
}
