namespace Bond.Parser.Syntax;

/// <summary>Root AST node for a parsed Bond file.</summary>
public record Bond(
    Import[] Imports,
    Namespace[] Namespaces,
    Declaration[] Declarations
)
{
    /// <summary>Bound declarations from this file and its imports; not additional generation roots.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Declaration[] ResolvedDeclarations { get; init; } = [];

    public override string ToString() =>
        $"Bond file with {Imports.Length} imports, {Namespaces.Length} namespaces, {Declarations.Length} declarations";
}
