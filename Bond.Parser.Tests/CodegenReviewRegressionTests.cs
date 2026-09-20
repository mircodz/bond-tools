using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Formatting;
using Bond.Parser.Parser;
using Bond.Parser.Syntax;

namespace Bond.Parser.Tests;

public sealed class CodegenReviewRegressionTests
{
    [Fact]
    public async Task RootAliasDoesNotReplaceAnExplicitlyEmptyImportedAliasScope()
    {
        var imports = new Dictionary<string, string>
        {
            ["leaf.bond"] = "namespace Example using Value = int32;",
            ["middle.bond"] = """
                import "leaf.bond"
                namespace Example
                struct Imported { 0: Value value; }
                """
        };
        var parsed = await ParserFacade.ParseContentAsync("""
            import "middle.bond"
            namespace Example
            using Value = string;
            struct View view_of Imported { value }
            struct Local { 0: Value value; }
            """, "/schemas/root.bond",
            (_, import) => Task.FromResult(("/schemas/" + import, imports[import])));
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));
        var imported = parsed.Ast!.ResolvedDeclarations.OfType<StructDeclaration>().Single(type => type.Name == "Imported");
        var view = parsed.Ast.Declarations.OfType<StructDeclaration>().Single(type => type.Name == "View");
        var local = parsed.Ast.Declarations.OfType<StructDeclaration>().Single(type => type.Name == "Local");
        Assert.IsType<BondType.Int32>(Assert.Single(imported.Fields).Type.ResolveAliases());
        Assert.IsType<BondType.Int32>(Assert.Single(view.Fields).Type.ResolveAliases());
        Assert.IsType<BondType.String>(Assert.Single(local.Fields).Type.ResolveAliases());
        var generated = CSharpGenerator.Generate(parsed.Ast, "root.bond");
        Assert.True(generated.Success);
        Assert.Contains("public int value", generated.Code!);
        Assert.Contains("public string value", generated.Code!);
    }

    [Theory]
    [InlineData(CSharpModelFeatures.Cloning)]
    [InlineData(CSharpModelFeatures.Equality)]
    [InlineData(CSharpModelFeatures.Debugger)]
    [InlineData(CSharpModelFeatures.StringRepresentation)]
    public async Task IncompleteBaseDefinitionsCannotSilentlyProducePartialOperations(CSharpModelFeatures feature)
    {
        var parsed = await ParserFacade.ParseStringAsync("namespace Example struct Base; struct Child : Base {}");
        Assert.True(parsed.Success);
        var generated = CSharpGenerator.Generate(parsed.Ast!, "child.bond",
            new CSharpGenerationOptions { ModelFeatures = feature });
        Assert.False(generated.Success);
        Assert.Null(generated.Code);
        Assert.Contains(generated.Errors, error => error.Message.Contains("base 'Example.Base'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CSharpModelFeatures.None)]
    [InlineData(CSharpModelFeatures.Descriptors)]
    public async Task ExternalBasesStillSupportPlainModelsAndSymbolicDescriptors(CSharpModelFeatures feature)
    {
        var parsed = await ParserFacade.ParseStringAsync("namespace Example struct Base; struct Child : Base {}");
        Assert.True(parsed.Success);
        var generated = CSharpGenerator.Generate(parsed.Ast!, "child.bond",
            new CSharpGenerationOptions { ModelFeatures = feature });
        Assert.True(generated.Success, string.Join("\n", generated.Errors.Select(error => error.Message)));
        CSharpGeneratorTests.Compile(generated.Code!, """
            namespace Example {
                [Bond.Schema] public class Base {
                    [Bond.Id(0)] public System.Collections.Generic.List<int> values { get; set; } = new();
                }
            }
            """);
    }

    [Theory]
    [InlineData("-32", -32)]
    [InlineData("-0X20", -32)]
    [InlineData("+32", 32)]
    public async Task FormattingPreservesSignedGenericArguments(string literal, long expected)
    {
        var schema = $$"""
            namespace Example
            using Values<T, N> = vector<T>;
            struct Item { 0: Values<int32, {{literal}}> values; }
            """;
        var formatted = BondFormatter.Format(schema, "item.bond");
        Assert.True(formatted.Success);
        Assert.Contains("Values<int32, " + literal + ">", formatted.FormattedText!);
        var parsed = await ParserFacade.ParseStringAsync(formatted.FormattedText!);
        Assert.True(parsed.Success);
        var structure = Assert.Single(parsed.Ast!.Declarations.OfType<StructDeclaration>());
        var reference = Assert.IsType<BondType.TypeReference>(Assert.Single(structure.Fields).Type);
        Assert.Equal(expected, Assert.IsType<BondType.IntTypeArg>(reference.TypeArguments[1]).Value);
        Assert.Equal(formatted.FormattedText, BondFormatter.Format(formatted.FormattedText!, "item.bond").FormattedText);
    }
}
