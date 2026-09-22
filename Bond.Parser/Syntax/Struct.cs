using System.Linq;

namespace Bond.Parser.Syntax;

public sealed record StructDeclaration : Declaration
{
    public required Attribute[] Attributes { get; init; }
    public BondType? BaseType { get; init; }
    public bool IsView { get; init; }
    public string[]? ViewTarget { get; init; }
    public string[] ViewFields { get; init; } = [];
    public required Field[] Fields { get; init; }

    public override string Kind => "struct";

    public override string ToString()
    {
        var typeParams = TypeParameters.Length > 0
            ? $"<{string.Join(", ", TypeParameters.Select(p => p.ToString()))}>"
            : "";
        var baseType = BaseType != null ? $" : {BaseType}" : "";
        return $"struct {Name}{typeParams}{baseType}";
    }
}
