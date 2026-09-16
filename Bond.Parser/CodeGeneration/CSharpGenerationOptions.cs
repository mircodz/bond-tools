using System.Collections.Generic;

namespace Bond.Parser.CodeGeneration;

public sealed record CSharpGenerationOptions
{
    public IReadOnlyList<string> NamespaceMappings { get; init; } = [];
    public IReadOnlyList<string> TypeMappings { get; init; } = [];
    public bool GenerateModelFeatures { get; init; } = true;
}
