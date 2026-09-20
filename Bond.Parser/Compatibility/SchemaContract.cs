using System;
using System.Collections.Generic;
using System.Linq;

namespace Bond.Parser.Compatibility;

internal sealed record SchemaContract(
    bool IncludesImports,
    bool AllowsUnresolvedTypes,
    IReadOnlyList<ContractDeclaration> Declarations);

internal sealed record ContractDeclaration(
    string Name,
    string Kind,
    bool IsRoot,
    IReadOnlyList<string> Constraints,
    TypeShape? BaseType,
    IReadOnlyList<ContractField> Fields,
    IReadOnlyList<ContractConstant> Constants,
    IReadOnlyList<ContractMethod> Methods);

internal sealed record ContractField(
    int Ordinal, string Name, string JsonName, string Modifier,
    TypeShape Type, ContractDefault Default);

internal sealed record ContractDefault(string Kind, string Value);
internal sealed record ContractConstant(string Name, int Value);
internal sealed record ContractMethod(string Name, string Kind, TypeShape Input, TypeShape Result);

// Only type expressions are recursive; named types never contain declaration bodies.
internal sealed record TypeShape(string Kind, string? Name, long? Integer, IReadOnlyList<TypeShape> Arguments)
{
    internal static TypeShape Of(string kind, params TypeShape[] arguments) =>
        new(kind, null, null, Array.AsReadOnly(arguments));

    internal bool Same(TypeShape? other) => other is not null && Kind == other.Kind && Name == other.Name
        && Integer == other.Integer && Arguments.Count == other.Arguments.Count
        && Arguments.Zip(other.Arguments).All(pair => pair.First.Same(pair.Second));

    public override string ToString()
    {
        var name = Name ?? (Kind is "integer" or "parameter"
            ? (Kind == "parameter" ? "$" : "") + Integer?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Kind);
        return Arguments.Count == 0 ? name : $"{name}<{string.Join(", ", Arguments)}>";
    }
}
