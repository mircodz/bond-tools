using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Bond.Parser.Syntax;
using FluentAssertions;

namespace Bond.Parser.Tests;

public class AliasScopeTests
{
    [Theory]
    [InlineData("a.bond", "b.bond")]
    [InlineData("b.bond", "a.bond")]
    public async Task ImportedAliasScopesDoNotDependOnSiblingImportOrder(string first, string second)
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = $$"""
                import "{{first}}"
                import "{{second}}"
                namespace N
                struct Root { 0: B item; }
                """,
            ["a.bond"] = "namespace N using Value = int32;",
            ["b.bond"] = """
                import "c.bond"
                namespace N
                struct B { 0: Value value; }
                """,
            ["c.bond"] = "namespace N using Value = string;"
        };

        var result = await Parse(files);
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        var imported = result.Ast!.ResolvedDeclarations.OfType<StructDeclaration>().Single(declaration => declaration.Name == "B");
        imported.Fields.Should().ContainSingle().Which.Type.ResolveAliases().Should().Be(BondType.String.Instance);
        var root = result.Ast.Declarations.OfType<StructDeclaration>().Single();
        root.Fields[0].Type.Should().BeOfType<BondType.TypeReference>().Which.Declaration.Should().BeSameAs(imported);
    }

    [Theory]
    [InlineData("a.bond", "b.bond")]
    [InlineData("b.bond", "a.bond")]
    public async Task TransitiveAliasScopesKeepLocalShadowingAcrossDiamondImports(string first, string second)
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = $$"""
                import "{{first}}"
                import "{{second}}"
                namespace N
                using Value = bool;
                struct Root { 0: Value value; 1: A a; 2: B b; }
                """,
            ["a.bond"] = """
                import "middle.bond"
                namespace N
                struct A { 0: Value value; }
                """,
            ["b.bond"] = """
                import "middle.bond"
                namespace N
                using Value = string;
                struct B { 0: Value value; }
                """,
            ["middle.bond"] = """import "leaf.bond" namespace N""",
            ["leaf.bond"] = "namespace N using Value = int32;"
        };

        var result = await Parse(files);
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        var structures = result.Ast!.ResolvedDeclarations.OfType<StructDeclaration>().ToDictionary(declaration => declaration.Name);
        structures["A"].Fields[0].Type.ResolveAliases().Should().Be(BondType.Int32.Instance);
        structures["B"].Fields[0].Type.ResolveAliases().Should().Be(BondType.String.Instance);
        structures["Root"].Fields[0].Type.ResolveAliases().Should().Be(BondType.Bool.Instance);
    }

    [Theory]
    [InlineData("Value")]
    [InlineData("N.Value")]
    public async Task EmptyImportedAliasScopesCannotSeeSiblingOrRootAliases(string type)
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = """
                import "a.bond"
                import "b.bond"
                namespace N
                using Value = bool;
                struct Root { 0: B item; }
                """,
            ["a.bond"] = "namespace N using Value = int32;",
            ["b.bond"] = "namespace N\nstruct B { 0: " + type + " value; }"
        };

        var result = await Parse(files);
        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Message.Should().Contain("not found in symbol table");
        error.FilePath.Should().Be("b.bond");
        error.Line.Should().Be(2);
        error.Column.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("a.bond", "b.bond")]
    [InlineData("b.bond", "a.bond")]
    public async Task ConflictingImportedAliasesAreAmbiguousOnlyWhenReferenced(string first, string second)
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = $$"""
                import "{{first}}"
                import "{{second}}"
                namespace N
                struct Root { 0: Value value; }
                """,
            ["a.bond"] = "namespace N using Value = int32;",
            ["b.bond"] = "namespace N using Value = string;"
        };

        var result = await Parse(files);
        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Message.Should().Contain("Ambiguous type alias 'Value'");
        error.FilePath.Should().Be("root.bond");
        error.Line.Should().Be(4);
        error.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EquivalentImportedAliasDefinitionsRemainUsable()
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = """
                import "a.bond"
                import "b.bond"
                namespace N
                struct Root { 0: Value value; }
                """,
            ["a.bond"] = "namespace N using Value = int32;",
            ["b.bond"] = "namespace N using Value = int32;"
        };

        var result = await Parse(files);
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        result.Ast!.Declarations.OfType<StructDeclaration>().Single().Fields[0].Type.ResolveAliases()
            .Should().Be(BondType.Int32.Instance);
    }

    [Theory]
    [InlineData("using Value = Local;", "N.Value")]
    [InlineData("using Value<T> = map<Local, T>;", "N.Value<int32>")]
    [InlineData("using Value<T> = Holder<Local>;", "N.Value<int32>")]
    public async Task IdenticalIndirectAliasExpressionsAreComparedInTheirOwnScopes(string declaration, string fieldType)
    {
        foreach (var reverse in new[] { false, true })
        {
            var first = reverse ? "b.bond" : "a.bond";
            var second = reverse ? "a.bond" : "b.bond";
            var files = new Dictionary<string, string>
            {
                ["root.bond"] = $$"""
                    import "{{first}}"
                    import "{{second}}"
                    namespace N
                    struct Holder<T> {} struct Root { 0: {{fieldType}} value; }
                    """,
                ["a.bond"] = "namespace N using Local = int32; " + declaration,
                ["b.bond"] = "namespace N using Local = string; " + declaration
            };

            var result = await Parse(files);
            result.Success.Should().BeFalse();
            var error = result.Errors.Should().ContainSingle().Subject;
            error.Message.Should().Contain("Ambiguous type alias 'N.Value'");
            error.FilePath.Should().Be("root.bond");
            error.Line.Should().Be(4);
            error.Column.Should().BeGreaterThan(0);
        }
    }

    [Theory]
    [InlineData("using Value = Local;", "using Value = Local;", "N.Value")]
    [InlineData("using Intermediate = Local; using Value = Intermediate;", "using Value = Local;", "N.Value")]
    [InlineData("using Value<T> = T;", "using Identity<U> = U; using Value<V> = Identity<V>;", "N.Value<int32>")]
    [InlineData("using Inner<T> = T; using Value<T> = Inner<Local>;", "using Value<U> = Local;", "N.Value<string>")]
    public async Task EquivalentIndirectAndGenericImportedAliasesRemainUsable(string firstDeclaration, string secondDeclaration, string fieldType)
    {
        foreach (var reverse in new[] { false, true })
        {
            var first = reverse ? "b.bond" : "a.bond";
            var second = reverse ? "a.bond" : "b.bond";
            var files = new Dictionary<string, string>
            {
                ["root.bond"] = $$"""
                    import "{{first}}"
                    import "{{second}}"
                    namespace N
                    struct Root { 0: {{fieldType}} value; }
                    """,
                ["a.bond"] = "namespace N using Local = int32; " + firstDeclaration,
                ["b.bond"] = "namespace N using Local = int32; " + secondDeclaration
            };

            var result = await Parse(files);
            result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
            result.Ast!.Declarations.OfType<StructDeclaration>().Single().Fields[0].Type.ResolveAliases()
                .Should().Be(BondType.Int32.Instance);
        }
    }

    [Theory]
    [InlineData("a.bond", "b.bond")]
    [InlineData("b.bond", "a.bond")]
    public async Task EquivalentGenericAliasesExpandNestedContainersAndTypeArguments(string first, string second)
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = $$"""
                import "{{first}}"
                import "{{second}}"
                namespace N
                struct Holder<T> {}
                struct Root { 0: Value<string> value; }
                """,
            ["a.bond"] = """
                namespace N
                using Local = int32;
                using Value<T> = map<Local, vector<Holder<T>>>;
                """,
            ["b.bond"] = """
                namespace N
                using Identity<U> = U;
                using Local = Identity<int32>;
                using Items<U> = vector<Holder<Identity<U>>>;
                using Value<V> = map<Local, Items<V>>;
                """
        };

        var result = await Parse(files);
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        var root = result.Ast!.Declarations.OfType<StructDeclaration>().Single(declaration => declaration.Name == "Root");
        var map = root.Fields[0].Type.ResolveAliases().Should().BeOfType<BondType.Map>().Subject;
        map.KeyType.ResolveAliases().Should().Be(BondType.Int32.Instance);
        var vector = map.ValueType.ResolveAliases().Should().BeOfType<BondType.Vector>().Subject;
        var holder = vector.ElementType.ResolveAliases().Should().BeOfType<BondType.TypeReference>().Subject;
        holder.Declaration.QualifiedName.Should().Be("N.Holder");
        holder.TypeArguments.Should().ContainSingle().Which.ResolveAliases().Should().Be(BondType.String.Instance);
    }

    [Fact]
    public async Task LocalDuplicateAliasesAreStillRejectedWhenImportsAreShadowed()
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = """
                import "model.bond"
                namespace N
                using Value = string;
                using Value = bool;
                struct Root { 0: Value value; }
                """,
            ["model.bond"] = "namespace N using Value = int32;"
        };

        var result = await Parse(files);
        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Message.Should().Contain("Duplicate declaration: alias 'Value'");
        error.FilePath.Should().Be("root.bond");
        error.Line.Should().Be(4);
    }

    [Fact]
    public async Task ImportCyclesAndDiamondsRetainReachableAliasScopes()
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = """
                import "left.bond"
                import "right.bond"
                namespace N
                using RootValue = string;
                struct Root { 0: Left left; 1: Right right; }
                """,
            ["left.bond"] = """import "shared.bond" namespace N struct Left { 0: Value value; }""",
            ["right.bond"] = """import "shared.bond" namespace N struct Right { 0: Value value; }""",
            ["shared.bond"] = """import "root.bond" namespace N using Value = RootValue;"""
        };

        var result = await Parse(files);
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        var structures = result.Ast!.ResolvedDeclarations.OfType<StructDeclaration>().ToDictionary(declaration => declaration.Name);
        structures["Left"].Fields[0].Type.ResolveAliases().Should().Be(BondType.String.Instance);
        structures["Right"].Fields[0].Type.ResolveAliases().Should().Be(BondType.String.Instance);
    }

    [Fact(Timeout = 60_000)]
    public async Task LayeredDiamondImportsComputeBoundedScopesAndPreserveLocalShadowing()
    {
        const int layers = 48;
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = $$"""
                import "layer{{layers - 1}}.bond"
                namespace N
                using Value = string;
                struct Root { 0: Value value; 1: Layer{{layers - 1}} item; }
                """
        };
        for (var index = 0; index < layers; index++)
        {
            var imports = index > 0 ? $"import \"layer{index - 1}.bond\"\n" : "";
            if (index > 1)
            {
                imports += $"import \"layer{index - 2}.bond\"\n";
            }

            var alias = index == 0 ? "using Value = int32;" : "";
            files[$"layer{index}.bond"] = imports + $"namespace N {alias} struct Layer{index} {{ 0: Value value; }}";
        }

        var importRequests = 0;
        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await Task.Run(
            () => ParserFacade.ParseContentAsync(files["root.bond"], "root.bond", (_, path) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                importRequests++;
                return Task.FromResult((path, files[path]));
            }),
            cancellationToken).WaitAsync(cancellationToken);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        importRequests.Should().Be(2 * layers - 2);
        var structures = result.Ast!.ResolvedDeclarations.OfType<StructDeclaration>().ToArray();
        structures.Should().HaveCount(layers + 1);
        structures.Single(declaration => declaration.Name == "Root").Fields[0].Type.ResolveAliases()
            .Should().Be(BondType.String.Instance);
        structures.Where(declaration => declaration.Name != "Root").Should()
            .OnlyContain(declaration => declaration.Fields[0].Type.ResolveAliases() == BondType.Int32.Instance);
    }

    [Fact]
    public async Task SameNamedFileLocalGenericAliasesResolveWithoutFalseCycles()
    {
        var files = new Dictionary<string, string>
        {
            ["root.bond"] = """
                import "model.bond"
                namespace N
                using Value<T> = Original<T>;
                struct Root { 0: Value<int32> x; 1: Value<Value<string>> y; }
                """,
            ["model.bond"] = """
                namespace N
                using Value<T> = T;
                using Original<T> = Value<T>;
                """
        };

        var result = await Parse(files);
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        var fields = result.Ast!.Declarations.OfType<StructDeclaration>().Single().Fields;
        fields[0].Type.ResolveAliases().Should().Be(BondType.Int32.Instance);
        fields[1].Type.ResolveAliases().Should().Be(BondType.String.Instance);
    }

    [Fact]
    public async Task ImportedViewsRetainAliasTypesWhenRootAliasesShadowImports()
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
    [InlineData("using Loop<T> = Loop<T>;")]
    [InlineData("using Loop<T> = Loop<vector<T>>;")]
    [InlineData("using First<T> = Second<vector<T>>; using Second<T> = First<T>;")]
    [InlineData("using Loop<T> = vector<Loop<T>>;")]
    public async Task GenericAliasCyclesReportLocatedDiagnostics(string declaration)
    {
        var result = await ParserFacade.ParseContentAsync("namespace N\n" + declaration, "cycle.bond");

        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Message.Should().Contain("Cyclic");
        error.FilePath.Should().Be("cycle.bond");
        error.Line.Should().Be(2);
        error.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task StandaloneTypeResolutionUsesTheSuppliedAliasScope()
    {
        var parsed = await ParserFacade.ParseStringAsync("""
            namespace N
            using Values<T> = vector<T>;
            struct Item { 0: Values<int32> values; }
            """, options: new ParseOptions(IgnoreImports: true));
        parsed.Success.Should().BeTrue(string.Join("; ", parsed.Errors.Select(error => error.Message)));
        var ast = parsed.Ast!;
        var aliases = ast.Declarations.OfType<AliasDeclaration>().ToArray();
        var symbols = new SymbolTable();
        foreach (var declaration in ast.Declarations.OfType<StructDeclaration>())
        {
            symbols.AddDeclaration(declaration);
        }

        var resolved = TypeResolver.Resolve(ast, symbols, aliases);

        var item = resolved.Declarations.OfType<StructDeclaration>().Single();
        item.Fields.Should().ContainSingle().Which.Type.ResolveAliases()
            .Should().Be(new BondType.Vector(BondType.Int32.Instance));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveAliasesRejectsRecursiveTemplatesEvenWhenArgumentsGrow(bool growArguments)
    {
        var parameter = new TypeParam("T");
        var identity = new AliasDeclaration
        {
            Namespaces = [new Namespace(null, ["N"])],
            Name = "Identity",
            TypeParameters = [parameter],
            AliasedType = new BondType.TypeParameter(parameter)
        };
        var recursiveArguments = new BondType[1];
        var loop = new AliasDeclaration
        {
            Namespaces = identity.Namespaces,
            Name = "Loop",
            TypeParameters = [parameter],
            AliasedType = new BondType.TypeReference(identity, recursiveArguments)
        };
        BondType argument = new BondType.TypeParameter(parameter);
        if (growArguments)
        {
            argument = new BondType.Vector(argument);
        }

        recursiveArguments[0] = new BondType.TypeReference(loop, [argument]);
        var reference = new BondType.TypeReference(loop, [BondType.Int32.Instance]);

        Action resolve = () => reference.ResolveAliases();
        resolve.Should().Throw<InvalidOperationException>().WithMessage("Cyclic type alias 'Loop'.");
    }

    private static Task<ParseResult> Parse(Dictionary<string, string> files) =>
        ParserFacade.ParseContentAsync(files["root.bond"], "root.bond",
            (_, path) => Task.FromResult((path, files[path])));
}
