using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Bond.Parser.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bond.Parser.Tests;

public sealed class CSharpDocumentationTests
{
    [Theory]
    [InlineData("// Text")]
    [InlineData("/// Text")]
    [InlineData("/* Text */")]
    [InlineData("/** Text */")]
    [InlineData("/*\n * Text\n */")]
    [InlineData("/**\n * Text\n */")]
    public async Task DocumentsTypesPropertiesAndEnumConstants(string comment)
    {
        var code = await Generate($$"""
            namespace Example
            {{comment}}
            [Label("box")]
            struct Box<T> {
                {{comment}}
                [Label("value")]
                0: T event;
            }
            {{comment}}
            [Label("enum")]
            enum class {
                {{comment}}
                event = 1
            }
            """);
        var docs = CompileDocumentation(code);
        Assert.Equal(4, docs.Count);
        foreach (var member in new[] { "T:Example.Box`1", "P:Example.Box`1.event", "T:Example.class", "F:Example.class.event" })
        {
            Assert.Equal("Text", Summary(docs, member));
            Assert.Empty(docs[member].Elements("typeparam"));
        }
    }

    [Fact]
    public async Task CombinesLeadingAndTrailingCommentsWithoutLeakingToFollowingMembers()
    {
        var parsed = await Parse("""
            namespace Example
            // Leading type.
            struct Value {
                9: int32 last; // Last field.
                // Leading field.
                0: int32 first; /* Trailing field. */
                2: int32 next;
                /* Same-line leading. */ 3: int32 inline_field; /* Multiline trailing.
                    More trailing. */
                /// Own field.
                4: int32 own;
            } // Trailing type.
            struct Undocumented {}
            // Leading enum.
            enum State {
                // Leading constant.
                first = 0, // Trailing constant.
                second = 1;
                third = 2; /* Third constant. */
                fourth = 3
            } /* Trailing enum. */
            enum UndocumentedEnum { only }
            """);
        var structure = Assert.IsType<StructDeclaration>(parsed.Declarations[0]);
        Assert.Equal("// Last field.", structure.Fields.Single(field => field.Name == "last").TrailingTrivia!.Text);
        Assert.Single(structure.Fields.Single(field => field.Name == "first").LeadingTrivia);
        Assert.Empty(structure.Fields.Single(field => field.Name == "next").LeadingTrivia);

        var docs = CompileDocumentation(Generate(parsed));
        Assert.Equal("Leading type.\nTrailing type.", Summary(docs, "T:Example.Value"));
        Assert.Equal("Last field.", Summary(docs, "P:Example.Value.last"));
        Assert.Equal("Leading field.\nTrailing field.", Summary(docs, "P:Example.Value.first"));
        Assert.Equal("Same-line leading.\nMultiline trailing.\nMore trailing.", Summary(docs, "P:Example.Value.inline_field"));
        Assert.Equal("Own field.", Summary(docs, "P:Example.Value.own"));
        Assert.Equal("Leading enum.\nTrailing enum.", Summary(docs, "T:Example.State"));
        Assert.Equal("Leading constant.\nTrailing constant.", Summary(docs, "F:Example.State.first"));
        Assert.Equal("Third constant.", Summary(docs, "F:Example.State.third"));
        Assert.Equal(8, docs.Count);
    }

    [Fact]
    public async Task SameLineBlockCommentsBelongOnlyToThePrecedingMember()
    {
        var docs = CompileDocumentation(await Generate("""
            namespace Example
            struct Value {
                0: int32 first; /* First field. */ 1: int32 second;
            } /* First type. */ struct Next {}
            enum State { first = 0, /* First constant. */ second = 1 }
            """));
        Assert.Equal("First field.", Summary(docs, "P:Example.Value.first"));
        Assert.Equal("First type.", Summary(docs, "T:Example.Value"));
        Assert.Equal("First constant.", Summary(docs, "F:Example.State.first"));
        Assert.Equal(3, docs.Count);
    }

    [Fact]
    public async Task PreservesParagraphsIndentationAndLiteralXmlWithDeterministicNewlines()
    {
        const string schema = """
            namespace Example
            /**
             * A <T> & "quoted" value > zero.
             *
             *     if (a < b && b > 0) {
             *         use("<see cref=\"T\"/>");
             *     }
             *
             * <include file="missing.xml" path="*"/>
             * </summary><remarks>literal</remarks>
             */
            struct Example {
                /*
                    First paragraph.

                        Indented second paragraph.
                */
                0: int32 value;
                // First line.
                //
                //     Indented line.

                /// Last paragraph.
                1: int32 lines;
            }
            """;
        var code = await Generate(schema);
        Assert.Equal(code, await Generate(schema.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.DoesNotContain("\r", code);

        var docs = CompileDocumentation(code);
        Assert.Equal("""
            A <T> & "quoted" value > zero.

                if (a < b && b > 0) {
                    use("<see cref=\"T\"/>");
                }

            <include file="missing.xml" path="*"/>
            </summary><remarks>literal</remarks>
            """, Summary(docs, "T:Example.Example"));
        Assert.Empty(docs["T:Example.Example"].Element("summary")!.Elements());
        Assert.Equal("First paragraph.\n\n    Indented second paragraph.", Summary(docs, "P:Example.Example.value"));
        Assert.Equal("First line.\n\n    Indented line.\n\nLast paragraph.", Summary(docs, "P:Example.Example.lines"));
    }

    [Fact]
    public async Task OmitsEmptyAndMissingDocumentation()
    {
        var code = await Generate("""
            namespace Example
            struct Undocumented {
                0: int32 value;
            }
            //
            ///
            /**/
            /**
             *
             */
            struct Empty {
                /* */
                0: int32 value;
            }
            enum EmptyEnum { value }
            """);
        Assert.DoesNotContain("///", code);
        Assert.Empty(CompileDocumentation(code));
    }

    [Fact]
    public async Task KeepsXmlInvalidCharactersPrintableAndUnicodeTextIntact()
    {
        const string text = "Controls: \0\u0001\u000b\u000c\u001f\uFFFE\uFFFF\uD800x\uDC00\uD800. Unicode: 🚀 λ \u200b. Lines: \u0085\u2028\u2029.";
        var docs = CompileDocumentation(await Generate("namespace Example\n// " + text + "\nstruct Value {}"));
        Assert.Equal(@"Controls: \u0000\u0001\u000B\u000C\u001F\uFFFE\uFFFF\uD800x\uDC00\uD800. Unicode: 🚀 λ " +
            "\u200b. Lines: \u0085\u2028\u2029.", Summary(docs, "T:Example.Value"));
    }

    [Fact]
    public async Task ViewsKeepRootImportedAndInheritedFieldDocumentation()
    {
        const string imported = """
            namespace Imported
            // Base type.
            struct Base {
                // Inherited field.
                0: int32 inherited;
            }
            // Imported type.
            struct Source<T> : Base {
                // Imported field.
                1: T value; // Imported detail.
                2: int32 plain;
            }
            """;
        var root = await Parse("""
            import "source.bond"
            namespace Root
            // Local source.
            struct Source {
                // Local field.
                0: int32 value;
            }
            // Local view.
            struct LocalView view_of Source { value }
            // Imported view.
            struct ImportedView view_of Imported.Source { value, plain }
            struct Child : Imported.Base {}
            """, (_, _) => Task.FromResult(("source.bond", imported)));
        var docs = CompileDocumentation(Generate(await Parse(imported)), Generate(root));
        Assert.Equal("Local view.", Summary(docs, "T:Root.LocalView"));
        Assert.Equal("Local field.", Summary(docs, "P:Root.LocalView.value"));
        Assert.Equal("Imported view.", Summary(docs, "T:Root.ImportedView`1"));
        Assert.Equal("Imported field.\nImported detail.", Summary(docs, "P:Root.ImportedView`1.value"));
        Assert.Equal("Imported field.\nImported detail.", Summary(docs, "P:Imported.Source`1.value"));
        Assert.Equal("Inherited field.", Summary(docs, "P:Imported.Base.inherited"));
        Assert.DoesNotContain("P:Root.ImportedView`1.plain", docs.Keys);
        Assert.DoesNotContain("T:Root.Child", docs.Keys);
    }

    private static async Task<Syntax.Bond> Parse(string schema, ImportResolver? importResolver = null)
    {
        var parsed = await ParserFacade.ParseStringAsync(schema, importResolver);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors));
        return parsed.Ast!;
    }

    private static async Task<string> Generate(string schema) => Generate(await Parse(schema));

    private static string Generate(Syntax.Bond ast)
    {
        var result = CSharpGenerator.Generate(ast, "input.bond");
        Assert.True(result.Success, string.Join("\n", result.Errors));
        return result.Code!;
    }

    private static Dictionary<string, XElement> CompileDocumentation(params string[] sources)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(global::Bond.SchemaAttribute).Assembly.Location)
            .Distinct(StringComparer.Ordinal);

        var compilation = CSharpCompilation.Create(
            "DocumentedContracts",
            sources.Select(source => CSharpSyntaxTree.ParseText(source,
                new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose))),
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["CS1591"] = ReportDiagnostic.Suppress }));

        using var assembly = new MemoryStream();
        using var documentation = new MemoryStream();
        var result = compilation.Emit(assembly, xmlDocumentationStream: documentation);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);

        documentation.Position = 0;
        return XDocument.Load(documentation, LoadOptions.PreserveWhitespace).Descendants("member")
            .ToDictionary(member => member.Attribute("name")!.Value, StringComparer.Ordinal);
    }

    private static string Summary(Dictionary<string, XElement> docs, string member)
    {
        var lines = docs[member].Element("summary")!.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim(' ', '\t').Length != 0);
        var end = Array.FindLastIndex(lines, line => line.Trim(' ', '\t').Length != 0);
        lines = lines[start..(end + 1)];

        var margin = lines.Where(line => line.Trim(' ', '\t').Length != 0)
            .Min(line => line.Length - line.TrimStart(' ', '\t').Length);
        return string.Join("\n", lines.Select(line => line[Math.Min(margin, line.Length)..].TrimEnd(' ', '\t')));
    }
}
