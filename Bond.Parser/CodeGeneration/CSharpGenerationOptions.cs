using System;
using System.Collections.Generic;

namespace Bond.Parser.CodeGeneration;

[Flags]
public enum CSharpModelFeatures
{
    None = 0,
    Equality = 1,
    StringRepresentation = 2,
    Clearing = 4,
    All = Equality | StringRepresentation | Clearing
}

public sealed record CSharpGenerationOptions
{
    public IReadOnlyList<string> UsingNamespaces { get; init; } = [];
    public IReadOnlyList<string> NamespaceMappings { get; init; } = [];
    public IReadOnlyList<string> TypeMappings { get; init; } = [];
    public CSharpModelFeatures ModelFeatures { get; init; }

    /// <summary>Generates Compact Binary serialization, which requires the BondTools.Runtime package.</summary>
    public bool Serialization { get; init; }

    /// <summary>
    /// Generates C# structs instead of classes, so nested structs and container elements are stored inline rather than
    /// allocated one by one. Their members are public fields. C# structs can't inherit, so schemas with bases fail to
    /// generate.
    /// </summary>
    public bool Structs { get; init; }

    public bool GenerateModelFeatures => ModelFeatures != CSharpModelFeatures.None;
    public bool GenerateEquality => ModelFeatures.HasFlag(CSharpModelFeatures.Equality);
    public bool GenerateToString => ModelFeatures.HasFlag(CSharpModelFeatures.StringRepresentation);
    public bool GenerateClear => ModelFeatures.HasFlag(CSharpModelFeatures.Clearing);
}
