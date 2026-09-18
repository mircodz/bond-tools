using System;
using System.Collections.Generic;

namespace Bond.Parser.CodeGeneration;

[Flags]
public enum CSharpModelFeatures
{
    None = 0,
    Descriptors = 1,
    Cloning = 2,
    Equality = 4,
    Debugger = 8,
    All = Descriptors | Cloning | Equality | Debugger
}

public sealed record CSharpGenerationOptions
{
    public IReadOnlyList<string> UsingNamespaces { get; init; } = [];
    public IReadOnlyList<string> NamespaceMappings { get; init; } = [];
    public IReadOnlyList<string> TypeMappings { get; init; } = [];
    public CSharpModelFeatures ModelFeatures { get; init; }
    public bool GenerateModelFeatures => ModelFeatures != CSharpModelFeatures.None;
    public bool GenerateDescriptors => ModelFeatures.HasFlag(CSharpModelFeatures.Descriptors);
    public bool GenerateCloning => ModelFeatures.HasFlag(CSharpModelFeatures.Cloning);
    public bool GenerateEquality => ModelFeatures.HasFlag(CSharpModelFeatures.Equality);
    public bool GenerateDebuggerSupport => ModelFeatures.HasFlag(CSharpModelFeatures.Debugger);
}
