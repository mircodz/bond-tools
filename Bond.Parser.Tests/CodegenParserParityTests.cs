using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.Json;
using Bond.Parser.Parser;
using Bond.Parser.Syntax;
using FluentAssertions;

namespace Bond.Parser.Tests;

public class CodegenParserParityTests
{
    [Theory]
    [InlineData("custom_alias_with_allocator.bond")]
    [InlineData("custom_alias_without_allocator.bond")]
    [InlineData("alias_with_allocator.bond")]
    [InlineData("alias_key.bond")]
    [InlineData("generic_service.bond")]
    [InlineData("immutable_collections.bond")]
    [InlineData("nullable_alias.bond")]
    [InlineData("schemadef.bond")]
    [InlineData("streaming.bond")]
    public async Task UpstreamGenericAndInheritanceFixturesHaveACompleteBoundEnvironment(string fixture)
    {
        var result = await ParserFacade.ParseFileAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        result.Ast!.ResolvedDeclarations.Should().NotBeEmpty();
        JsonSerializer.Serialize(result.Ast, BondJsonSerializerOptions.GetOptions()).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ViewsPreserveSelectedFieldsAndInheritGenericParametersAndBase()
    {
        var ast = await Parse("""
            namespace Example
            struct Base<T> { 0: bond_meta::name baseName; }
            [source("not copied")]
            struct Record<T : value> : Base<T> {
                11: string omitted;
                [field("copied")] 5: required_optional int32 number = 42;
                2: T item = nothing;
            }
            [view("own attribute")]
            struct Selected view_of Record { number; item; number; missing; baseName }
            struct Smaller view_of Selected { item }
            """);

        var source = ast.Declarations.OfType<StructDeclaration>().Single(d => d.Name == "Record");
        var view = ast.Declarations.OfType<StructDeclaration>().Single(d => d.Name == "Selected");
        view.IsView.Should().BeTrue();
        view.ViewTarget.Should().Equal("Record");
        view.TypeParameters.Should().Equal(source.TypeParameters);
        view.TypeParameters[0].Constraint.Should().Be(TypeConstraint.Value);
        new BondType.TypeParameter(view.TypeParameters[0]).IsScalar().Should().BeTrue();
        view.Attributes.Should().ContainSingle().Which.Value.Should().Be("own attribute");
        view.Fields.Select(f => f.Name).Should().Equal("item", "number");
        view.Fields.Select(f => f.Ordinal).Should().Equal((ushort)2, (ushort)5);
        view.Fields[1].Attributes.Should().ContainSingle().Which.Value.Should().Be("copied");
        view.Fields[1].Modifier.Should().Be(FieldModifier.RequiredOptional);
        view.Fields[1].DefaultValue.Should().Be(new Default.Integer(42));
        view.BaseType.Should().Be(source.BaseType);

        var smaller = ast.Declarations.OfType<StructDeclaration>().Single(d => d.Name == "Smaller");
        smaller.Fields.Should().ContainSingle().Which.Name.Should().Be("item");
        smaller.TypeParameters.Should().Equal(view.TypeParameters);
    }

    [Fact]
    public async Task ImportedEnvironmentKeepsBoundGenericAliasesAndInheritanceWithoutAddingRoots()
    {
        var ast = await Parse("""
            import "model.bond"
            namespace Application
            struct Root : Model.Middle<int32> { 0: Model.Choice choice = First; }
            """, new Dictionary<string, string>
        {
            ["model.bond"] = """
                import "base.bond"
                namespace Model
                using Items<T> = vector<T>;
                struct Middle<T> : Storage.Base<Items<T>> { 0: bond_meta::full_name name; }
                enum Choice { First, Second }
                """,
            ["base.bond"] = """
                namespace Storage
                using Identity<T> = T;
                struct Base<T> { 0: Identity<T> value; }
                """
        });

        ast.Declarations.Should().ContainSingle().Which.Name.Should().Be("Root");
        ast.ResolvedDeclarations.Select(d => d.QualifiedName).Should().BeEquivalentTo(
            "Application.Root", "Model.Middle", "Model.Items", "Model.Choice", "Storage.Base", "Storage.Identity");

        var middle = ast.ResolvedDeclarations.OfType<StructDeclaration>().Single(d => d.Name == "Middle");
        var baseType = middle.BaseType.Should().BeOfType<BondType.TypeReference>().Subject;
        baseType.Declaration.Should().BeOfType<StructDeclaration>()
            .Which.Fields[0].Type.ResolveAliases().Should().BeOfType<BondType.TypeParameter>();
        baseType.TypeArguments[0].ResolveAliases().Should().BeOfType<BondType.Vector>();

        var instantiated = middle.BaseType!.SubstituteTypeParameters(middle.TypeParameters, [BondType.Int32.Instance]);
        var argument = ((BondType.TypeReference)instantiated).TypeArguments[0].ResolveAliases();
        argument.Should().Be(new BondType.Vector(BondType.Int32.Instance));
    }

    [Fact]
    public async Task ImportedViewsUseTheTargetDeclarationNamespaceAndAliasEnvironment()
    {
        var ast = await Parse("""
            import "model.bond"
            namespace Application
            struct Selected view_of Model.Record { item }
            """, new Dictionary<string, string>
        {
            ["model.bond"] = """
                namespace Model
                enum State { Ready }
                using Value<T> = T;
                struct Record { 4: Value<State> item = Ready; }
                """
        });
        var view = ast.Declarations.Should().ContainSingle().Which.Should().BeOfType<StructDeclaration>().Subject;
        view.Fields.Should().ContainSingle().Which.Type.ResolveAliases().Should()
            .BeOfType<BondType.TypeReference>().Which.Declaration.QualifiedName.Should().Be("Model.State");
    }

    [Fact]
    public async Task ForwardDeclarationsReconcileToOneCanonicalDefinitionAndKeepRecursiveReferencesFinite()
    {
        var ast = await Parse("""
            namespace Example
            struct Node<U : value>;
            struct Node<T : value> { 0: vector<Node<T>> children; }
            struct Node<V : value>;
            struct Root { 0: Node<int32> node; }
            """);
        ast.Declarations.Should().HaveCount(4);
        ast.ResolvedDeclarations.Should().HaveCount(2);

        var node = ast.ResolvedDeclarations.OfType<StructDeclaration>().Single(d => d.Name == "Node");
        var reference = ((BondType.Vector)node.Fields[0].Type).ElementType.Should().BeOfType<BondType.TypeReference>().Subject;
        reference.Declaration.Should().BeOfType<ForwardDeclaration>();
        reference.Declaration.QualifiedName.Should().Be(node.QualifiedName);

        var json = JsonSerializer.Serialize(ast, BondJsonSerializerOptions.GetOptions());
        json.Should().NotContain("resolvedDeclarations").And.NotContain("ResolvedDeclarations");
        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("imports", "namespaces", "declarations");
    }

    [Fact]
    public async Task GenericAliasSubstitutionIsSimultaneousAndSupportsNestedIdentityApplications()
    {
        var ast = await Parse("""
            namespace Example
            using Identity<T> = T;
            using Pair<T, U> = map<T, vector<U>>;
            using Swap<T, U> = Pair<U, T>;
            struct Record {
                0: Identity<Identity<uint8>> number = 255;
                1: Swap<int32, string> values;
                2: Identity<bool> absent = nothing;
            }
            """);
        var fields = ast.Declarations.OfType<StructDeclaration>().Single().Fields;
        fields[0].Type.ResolveAliases().Should().Be(BondType.UInt8.Instance);
        fields[1].Type.ResolveAliases().Should().Be(new BondType.Map(BondType.String.Instance, new BondType.Vector(BondType.Int32.Instance)));

        var maybe = fields[2].Type.Should().BeOfType<BondType.Maybe>().Subject;
        maybe.ElementType.ResolveAliases().Should().Be(BondType.Bool.Instance);

        var alias = ast.Declarations.OfType<AliasDeclaration>().First();
        alias.AliasedType.Should().BeOfType<BondType.TypeParameter>();
    }

    [Theory]
    [InlineData("-7", -7L)]
    [InlineData("+7", 7L)]
    [InlineData("-0X10", -16L)]
    [InlineData("+0O10", 8L)]
    [InlineData("-18446744073709551615", 1L)]
    public async Task SignedIntegerAliasArgumentsPreserveTheirValueBeforeSubstitution(string literal, long expected)
    {
        var ast = await Parse("namespace Example using Array<N, T> = vector<T>; "
            + "struct Record { 0: Array<" + literal + ", int32> items; }");
        var type = ast.Declarations.OfType<StructDeclaration>().Single().Fields[0].Type
            .Should().BeOfType<BondType.TypeReference>().Subject;
        type.TypeArguments[0].Should().Be(new BondType.IntTypeArg(expected));
        type.ResolveAliases().Should().Be(new BondType.Vector(BondType.Int32.Instance));
    }

    [Theory]
    [InlineData("using Value<T> = T; struct Record { 0: Value<uint8> number = 256; }", "invalid default")]
    [InlineData("using Value<T> = T; struct Record { 0: Value<bool> flag = 1; }", "invalid default")]
    [InlineData("using Value<T> = T; enum State { Ready } struct Record { 0: Value<State> item = 0; }", "invalid default")]
    [InlineData("using Value<T> = T; enum State { Ready } struct Record { 0: Value<State> item = Unspecified; }", "invalid default")]
    [InlineData("enum State { Ready } struct Record { 0: State item = Unspecified; }", "invalid default")]
    [InlineData("using Value<T> = T; enum State { Ready } struct Record { 0: Value<State> item; }", "must have a default")]
    [InlineData("using Value<T> = T; struct Target {} struct Record { 0: Value<Target> item = nothing; }", "cannot have default")]
    [InlineData("using Key<T> = T; struct Target {} struct Record { 0: vector<map<Key<Target>, string>> items; }", "key type")]
    public async Task GenericAliasInstancesValidateTheirEffectiveTypes(string declarations, string error)
    {
        var result = await ParserFacade.ParseContentAsync("namespace Example\n" + declarations, "schema.bond");
        result.Success.Should().BeFalse();
        var diagnostic = result.Errors.Should().ContainSingle().Subject;
        diagnostic.Message.Should().Contain(error);
        diagnostic.FilePath.Should().Be("schema.bond");
        diagnostic.Line.Should().Be(2);
        diagnostic.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EnumAliasDefaultsAndNothingAreValidatedAfterSubstitution()
    {
        var ast = await Parse("""
            namespace Example
            enum State { Ready }
            using Value<T> = T;
            struct Record {
                0: Value<State> item = Ready;
                1: Value<State> absent = nothing;
            }
            """);
        var fields = ast.Declarations.OfType<StructDeclaration>().Single().Fields;
        fields[0].Type.ResolveAliases().IsEnum().Should().BeTrue();
        fields[1].Type.Should().BeOfType<BondType.Maybe>();
    }

    [Fact]
    public async Task GenericAliasContainerKeysAreCheckedAtTheDeclarationSiteLikeUpstream()
    {
        var ast = await Parse("""
            namespace Example
            using Keys<T> = set<T>;
            using Nested = Keys<list<map<int32, string>>>;
            struct Record { 0: Nested values; }
            """);
        ast.Declarations.OfType<StructDeclaration>().Single().Fields[0].Type.ResolveAliases()
            .Should().Be(new BondType.Set(new BondType.List(new BondType.Map(BondType.Int32.Instance, BondType.String.Instance))));
    }

    [Theory]
    [InlineData("struct Generic<T> {} struct Record { 0: Generic field; }", "requires 1 type argument")]
    [InlineData("using Value<T> = T; struct Record { 0: Value<int32, string> field; }", "requires 1 type argument")]
    [InlineData("struct Plain {} struct Record { 0: Plain<int32> field; }", "not a generic type")]
    [InlineData("struct Generic<T> { 0: T<int32> field; }", "cannot have type arguments")]
    [InlineData("struct Node<T : value>; struct Node<T> {}", "Duplicate declaration")]
    [InlineData("using First = Second; using Second = First;", "Cyclic")]
    [InlineData("struct First : Second {} struct Second : First {}", "Cyclic inheritance")]
    [InlineData("struct First view_of Second { field } struct Second view_of First { field }", "Cyclic")]
    [InlineData("enum State { Ready } struct View view_of State { Ready }", "defined struct target")]
    [InlineData("service Service {} struct Record { 0: Service field; }", "service")]
    public async Task InvalidTypeGraphsProduceLocatedDiagnostics(string declarations, string error)
    {
        var result = await ParserFacade.ParseContentAsync("namespace Example\n" + declarations, "schema.bond");
        result.Success.Should().BeFalse();
        var diagnostic = result.Errors.Should().ContainSingle().Subject;
        diagnostic.Message.Should().Contain(error);
        diagnostic.FilePath.Should().Be("schema.bond");
        diagnostic.Line.Should().Be(2);
        diagnostic.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ImportCyclesAndDiamondsDoNotLoseRootOrImportedDefinitions()
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = """
                import "left.bond"
                import "right.bond"
                namespace Model
                struct Root { 0: Shared item; }
                """,
            ["left.bond"] = """import "shared.bond" namespace Model struct Left {}""",
            ["right.bond"] = """import "shared.bond" namespace Model struct Right {}""",
            ["shared.bond"] = """import "root.bond" namespace Model struct Shared { 0: vector<Root> roots; }"""
        };

        var result = await ParserFacade.ParseContentAsync(files["root.bond"], "root.bond",
            (_, path) => Task.FromResult((path, files[path])));

        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        result.Ast!.Declarations.Should().ContainSingle().Which.Name.Should().Be("Root");
        result.Ast.ResolvedDeclarations.Select(d => d.Name).Should().BeEquivalentTo("Root", "Left", "Right", "Shared");
        JsonSerializer.Serialize(result.Ast, BondJsonSerializerOptions.GetOptions()).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ImportedSemanticErrorsRetainTheirOriginalSourceCoordinates()
    {
        var result = await ParserFacade.ParseContentAsync("""
            import "middle.bond"
            namespace Root
            struct Record : Imported.Invalid {}
            """, "root.bond", (_, path) => Task.FromResult((path, path == "middle.bond"
                ? """import "leaf.bond" namespace Imported"""
                : """
                  namespace Imported
                  using Value<T> = T;
                  struct Invalid {
                      0: Value<uint8> number = 256;
                  }
                  """)));
        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.FilePath.Should().Be("leaf.bond");
        error.Line.Should().Be(4);
        error.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CyclicImportedAliasesReportTheReferenceLocationInTheCorrectFile()
    {
        const string root = """
            import "leaf.bond"
            namespace Root
            using First = Imported.Second;
            """;
        var result = await ParserFacade.ParseContentAsync(root, "root.bond",
            (_, path) => Task.FromResult((path, path == "root.bond" ? root : """
                import "root.bond"
                namespace Imported
                // An imported alias refers back to the root alias.

                using Second = Root.First;
                """)));
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Message.Should().Contain("Cyclic");
        error.FilePath.Should().Be("leaf.bond");
        error.Line.Should().Be(5);
    }

    [Fact]
    public async Task ImportPathsAreUnescapedAndSupportMixedDirectorySeparators()
    {
        const string path = @"dir1/dir2\empty.bond";
        var result = await ParserFacade.ParseStringAsync(
            "import \"" + path + "\" namespace Example struct Record {}",
            (_, requested) =>
            {
                requested.Should().Be(path);
                return Task.FromResult((requested, "namespace Imported"));
            });
        result.Success.Should().BeTrue();

        var currentFile = Path.Combine(AppContext.BaseDirectory, "Fixtures", "imports", "root.bond");
        var (canonical, content) = await DefaultImportResolver.Resolve(currentFile, path);
        canonical.Should().Be(Path.Combine(Path.GetDirectoryName(currentFile)!, "dir1", "dir2", "empty.bond"));
        content.Should().Contain("namespace empty");
    }

    [Fact]
    public async Task ImportedAliasBodiesRemainBoundInTheirOwnFileWhenLocallyShadowed()
    {
        var ast = await Parse("""
            import "model.bond"
            namespace Example
            using Value = string;
            struct Root { 0: Value value; 1: Imported other; }
            """, new Dictionary<string, string>
        {
            ["model.bond"] = """
                namespace Example
                using Value = int32;
                struct Imported { 0: Value value; }
                """
        });
        ast.Declarations.OfType<StructDeclaration>().Single().Fields[0].Type.ResolveAliases().Should().Be(BondType.String.Instance);
        ast.ResolvedDeclarations.OfType<StructDeclaration>().Single(d => d.Name == "Imported")
            .Fields[0].Type.ResolveAliases().Should().Be(BondType.Int32.Instance);
    }

    [Fact]
    public async Task AliasesCanNameLaterRecursiveStructDefinitions()
    {
        var ast = await Parse("""
            namespace Example
            using Item<T> = Node<T>;
            struct Node<T> { 0: vector<Item<T>> children; }
            struct Root { 0: Item<int32> item; }
            """);
        var alias = ast.Declarations.OfType<AliasDeclaration>().Single();
        alias.AliasedType.Should().BeOfType<BondType.TypeReference>()
            .Which.Declaration.QualifiedName.Should().Be("Example.Node");
        ast.ResolvedDeclarations.OfType<StructDeclaration>().Single(d => d.Name == "Node").Fields.Should().ContainSingle();
        JsonSerializer.Serialize(ast, BondJsonSerializerOptions.GetOptions()).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ForwardDeclaredGenericViewsCanOccurInTheirSourceFields()
    {
        var ast = await Parse("""
            namespace Example
            struct Selected<U>;
            struct Record<T> { 0: vector<Selected<T>> children; }
            struct Selected view_of Record { children }
            """);
        var view = ast.ResolvedDeclarations.OfType<StructDeclaration>().Single(d => d.Name == "Selected");
        view.TypeParameters.Should().ContainSingle().Which.Name.Should().Be("T");
        view.Fields.Should().ContainSingle();
        JsonSerializer.Serialize(ast, BondJsonSerializerOptions.GetOptions()).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task EquivalentDefinitionsFromDifferentImportPathsHaveOneCanonicalEntry()
    {
        const string model = """
            namespace Model
            enum State { Ready }
            struct Record { 0: State state = Ready; }
            """;
        var ast = await Parse("""
            import "first.bond"
            import "second.bond"
            namespace Application
            struct Root { 0: Model.Record value; }
            """, new Dictionary<string, string>
        {
            ["first.bond"] = model,
            ["second.bond"] = "// the same schema under another path\n" + model
        });
        ast.ResolvedDeclarations.Select(d => d.QualifiedName).Should().BeEquivalentTo(
            "Application.Root", "Model.Record", "Model.State");
    }

    [Fact]
    public async Task EnumNumbersUseUpstreamSignedMachineIntegerInterpretation()
    {
        var ast = await Parse("""
            namespace Example
            enum Values {
                Positive = 0xFFFFFFFF,
                Negative = 0xFFFFFFFFFFFFFFFF,
                Wrapped = 18446744073709551616,
                Negated = -0xFFFFFFFFFFFFFFFF
            }
            """);
        ast.Declarations.OfType<EnumDeclaration>().Single().Constants.Select(c => c.Value)
            .Should().Equal(4294967295L, -1L, 0L, 1L);
    }

    [Fact]
    public async Task StringDecodingSupportsUpstreamCharacterEscapesWithoutDecodingTwice()
    {
        var ast = await Parse("""
            namespace Example
            struct Record {
                0: string text = "\\n|\123|\o123|\x1F600|\NUL|\SOH|\^A|\u0041|\U0001F600";
            }
            """);
        ast.Declarations.OfType<StructDeclaration>().Single().Fields[0].DefaultValue
            .Should().Be(new Default.String("\\n|{|S|😀|\0|\x01|\x01|A|😀"));
    }

    [Theory]
    [InlineData("struct Record { 65536: int32 field; }", "range 0-65535")]
    [InlineData("""struct Record { 0: string text = "\x110000"; }""", "Unicode code point")]
    [InlineData("""struct Record { 0: string text = "\x"; }""", "string literal")]
    public async Task InvalidLiteralsHaveSourceCoordinates(string declarations, string message)
    {
        var result = await ParserFacade.ParseContentAsync("namespace Example\n" + declarations, "schema.bond");
        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.FilePath.Should().Be("schema.bond");
        error.Line.Should().Be(2);
        error.Column.Should().BeGreaterThan(0);
        error.Message.Should().Contain(message);
    }

    private static async Task<Syntax.Bond> Parse(string content, Dictionary<string, string>? imports = null)
    {
        var result = await ParserFacade.ParseStringAsync(content,
            imports is null ? null : (_, path) => Task.FromResult((path, imports[path])));
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Ast!;
    }
}
