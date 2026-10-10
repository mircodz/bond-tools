using System;
using System.Collections.Generic;

namespace Bond.Parser.CodeGeneration;

[Flags]
public enum CSharpModelFeatures
{
    None = 0,
    Cloning = 1,
    Equality = 2,
    StringRepresentation = 4,
    Clearing = 8,
    All = Cloning | Equality | StringRepresentation | Clearing
}

public sealed record CSharpGenerationOptions
{
    public IReadOnlyList<string> UsingNamespaces { get; init; } = [];
    public IReadOnlyList<string> NamespaceMappings { get; init; } = [];
    public IReadOnlyList<string> TypeMappings { get; init; } = [];
    public CSharpModelFeatures ModelFeatures { get; init; }

    /// <summary>Generates Compact Binary serialization, which requires the BondTools.Runtime package.</summary>
    public bool Serialization { get; init; }

    public bool GenerateModelFeatures => ModelFeatures != CSharpModelFeatures.None;
    public bool GenerateCloning => ModelFeatures.HasFlag(CSharpModelFeatures.Cloning);
    public bool GenerateEquality => ModelFeatures.HasFlag(CSharpModelFeatures.Equality);
    public bool GenerateToString => ModelFeatures.HasFlag(CSharpModelFeatures.StringRepresentation);
    public bool GenerateClear => ModelFeatures.HasFlag(CSharpModelFeatures.Clearing);
}
