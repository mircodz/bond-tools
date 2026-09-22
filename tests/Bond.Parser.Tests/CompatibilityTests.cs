using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.Compatibility;
using Bond.Parser.Parser;
using Bond.Parser.Syntax;
using FluentAssertions;

namespace Bond.Parser.Tests;

public class CompatibilityTests
{
    private readonly CompatibilityChecker _checker = new();

    private static async Task<Syntax.Bond> Parse(string text)
    {
        var result = await ParserFacade.ParseStringAsync(text);
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));

        return result.Ast!;
    }

    private static Syntax.Bond Root(params Declaration[] declarations) => new([], [], declarations);

    private static StructDeclaration Structure(params Field[] fields) =>
        new()
        {
            Name = "Record",
            Namespaces = [new(null, ["Test"])],
            Attributes = [],
            Fields = fields
        };

    private static Field Field(ushort ordinal = 0, string name = "value", BondType? type = null) =>
        new([], ordinal, FieldModifier.Optional, type ?? BondType.Int32.Instance, name, null);

    [Fact]
    public async Task AddingRequiredField_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User {
                0: required string id;
                1: required string email;
            }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.ToLower().Contains("required") &&
            c.Description.Contains("email"));
    }

    [Fact]
    public async Task RemovingRequiredField_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User {
                0: required string id;
                1: required string email;
            }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("removed"));
    }

    [Fact]
    public async Task ChangingFieldOrdinal_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 1: required string id; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        // Changing ordinals is detected as remove + add.
        changes.Should().HaveCount(2);
        changes.Should().Contain(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.ToLower().Contains("removed"));
        changes.Should().Contain(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.ToLower().Contains("added"));
    }

    [Fact]
    public async Task ChangingFieldType_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required int32 age; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required string age; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.ToLower().Contains("type"));
    }

    [Fact]
    public async Task ChangingAlwaysWrittenDefaultValue_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required int32 status = 0; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required int32 status = 1; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.ToLower().Contains("default"));
    }

    [Fact]
    public async Task DirectOptionalToRequired_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: optional string name; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required string name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.ToLower().Contains("modifier"));
    }

    [Fact]
    public async Task DirectRequiredToOptional_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required string name; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: optional string name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.ToLower().Contains("modifier"));
    }

    [Fact]
    public async Task RenamingField_IsBreakingText()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: optional string name; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: optional string full_name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingText &&
            c.Description.Contains("name") &&
            c.Description.Contains("full_name"));
    }

    [Fact]
    public async Task AddingOptionalField_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User {
                0: required string id;
                1: optional string email;
            }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("email"));
    }

    [Fact]
    public async Task RemovingOptionalField_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User {
                0: required string id;
                1: optional string email;
            }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("removed"));
    }

    [Fact]
    public async Task OptionalToRequiredOptional_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: optional string name; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required_optional string name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.ToLower().Contains("modifier"));
    }

    [Fact]
    public async Task RequiredOptionalToRequired_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required_optional string name; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required string name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.ToLower().Contains("modifier"));
    }

    [Fact]
    public async Task Int32ToInt64Promotion_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required int32 value; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required int64 value; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("int32") &&
            c.Description.Contains("int64"));
    }

    [Fact]
    public async Task FloatToDoublePromotion_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required float value; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required double value; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("float") &&
            c.Description.Contains("double"));
    }

    [Fact]
    public async Task VectorToList_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required vector<string> tags; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required list<string> tags; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("vector") &&
            c.Description.Contains("list"));
    }

    [Fact]
    public async Task Int32ToEnum_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required int32 status; }
        """);
        var newSchema = await Parse("""
            namespace Test
            enum Status { Active = 0 }
            struct User { 0: required Status status; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("int32"));
    }

    [Fact]
    public async Task AddingEnumConstant_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            enum Status { Active = 0 }
        """);
        var newSchema = await Parse("""
            namespace Test
            enum Status { Active = 0, Inactive = 1 }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("Inactive"));
    }

    [Fact]
    public async Task ChangingEnumConstantValue_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            enum Status { Active = 0, Inactive = 1 }
        """);
        var newSchema = await Parse("""
            namespace Test
            enum Status { Active = 0, Inactive = 5 }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("value") &&
            c.Description.Contains("Inactive"));
    }

    [Fact]
    public async Task RemovingEnumConstant_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            enum Status { Active = 0, Inactive = 1 }
        """);
        var newSchema = await Parse("""
            namespace Test
            enum Status { Active = 0 }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("removed") &&
            c.Description.Contains("Inactive"));
    }

    [Fact]
    public async Task ChangingBaseStruct_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct Base1 { 0: required string id; }
            struct Base2 { 0: required int32 id; }
            struct User : Base1 { 1: required string name; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct Base1 { 0: required string id; }
            struct Base2 { 0: required int32 id; }
            struct User : Base2 { 1: required string name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("Inheritance"));
    }

    [Fact]
    public async Task AddingBaseStruct_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct Base { 0: required string id; }
            struct User { 1: required string name; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct Base { 0: required string id; }
            struct User : Base { 1: required string name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("Inheritance"));
    }

    [Fact]
    public async Task RemovingDeclarationAlone_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
            struct Profile { 0: required string bio; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("removed") &&
            c.Description.Contains("Profile"));
    }

    [Fact]
    public async Task AddingDeclaration_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
        """);
        var newSchema = await Parse("""
            namespace Test
            struct User { 0: required string id; }
            struct Profile { 0: required string bio; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("added") &&
            c.Description.Contains("Profile"));
    }

    [Fact]
    public async Task ChangingDeclarationType_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            struct Status { 0: required int32 value; }
        """);
        var newSchema = await Parse("""
            namespace Test
            enum Status { Active = 0 }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("kind changed"));
    }

    [Fact]
    public async Task IdenticalSchemas_NoChanges()
    {
        var schema = await Parse("""
            namespace Test
            struct User {
                0: required string id;
                1: optional string name;
            }
        """);

        var changes = _checker.CheckCompatibility(schema, schema);

        changes.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportedSchemasResolveTypesForCompatibilityChecks()
    {
        var root = Path.GetFullPath(Path.Combine("bond-parser-tests", Guid.NewGuid().ToString("N")));
        var mainPath = Path.Combine(root, "schema.bond");
        var commonPath = Path.Combine(root, "common.bond");

        var commonSchema = """
            namespace Test
            struct Common { 0: required int32 id; }
        """;

        var oldSchema = """
            import "common.bond"
            namespace Test
            struct User { 0: required Common c; }
        """;

        var newSchema = """
            import "common.bond"
            namespace Test
            struct User {
                0: required Common c;
                1: optional int32 age;
            }
        """;

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [commonPath] = commonSchema
        };

        ImportResolver resolver = (currentFile, importPath) =>
        {
            var currentDir = Path.GetDirectoryName(currentFile) ?? root;
            var absolutePath = Path.GetFullPath(Path.Combine(currentDir, importPath));
            if (!files.TryGetValue(absolutePath, out var content))
            {
                throw new FileNotFoundException($"Imported file not found: {importPath}", absolutePath);
            }

            return Task.FromResult((absolutePath, content));
        };

        var oldResult = await ParserFacade.ParseContentAsync(oldSchema, mainPath, resolver);
        oldResult.Success.Should().BeTrue($"parsing should succeed but got errors: {string.Join(", ", oldResult.Errors.Select(e => e.Message))}");

        var newResult = await ParserFacade.ParseContentAsync(newSchema, mainPath, resolver);
        newResult.Success.Should().BeTrue($"parsing should succeed but got errors: {string.Join(", ", newResult.Errors.Select(e => e.Message))}");

        var changes = _checker.CheckCompatibility(oldResult.Ast!, newResult.Ast!);

        changes.Should().Contain(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("age"));
    }

    [Fact]
    public async Task ServiceInheritanceChange_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            service Base1 { void Method1(void); }
            service Base2 { void Method2(void); }
            service MySvc : Base1 { void Method3(void); }
        """);
        var newSchema = await Parse("""
            namespace Test
            service Base1 { void Method1(void); }
            service Base2 { void Method2(void); }
            service MySvc : Base2 { void Method3(void); }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("Inheritance"));
    }

    [Fact]
    public async Task ServiceInheritanceAdded_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            service Base { void BaseMethod(void); }
            service MySvc { void MyMethod(void); }
        """);
        var newSchema = await Parse("""
            namespace Test
            service Base { void BaseMethod(void); }
            service MySvc : Base { void MyMethod(void); }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("Inheritance"));
    }

    [Fact]
    public async Task ServiceInheritanceRemoved_IsBreaking()
    {
        var oldSchema = await Parse("""
            namespace Test
            service Base { void BaseMethod(void); }
            service MySvc : Base { void MyMethod(void); }
        """);
        var newSchema = await Parse("""
            namespace Test
            service Base { void BaseMethod(void); }
            service MySvc { void MyMethod(void); }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("Inheritance"));
    }

    [Fact]
    public async Task AddingEnumConstantInMiddle_WithoutShiftingValues_IsLegalNumericAlias()
    {
        var oldSchema = await Parse("""
            namespace Test
            enum Status { Active = 0, Inactive = 1 }
        """);
        var newSchema = await Parse("""
            namespace Test
            enum Status { Active = 0, Pending, Inactive = 1 }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("Pending"));
        changes.Should().NotContain(c => c.Severity == ChangeSeverity.Error);
    }

    [Fact]
    public async Task UnusedAliasTypeChangesDoNotAffectWireCompatibility()
    {
        var oldSchema = await Parse("""
            namespace Test
            using MyType = string;
        """);
        var newSchema = await Parse("""
            namespace Test
            using MyType = int32;
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().BeEmpty();
    }

    [Fact]
    public async Task AliasChange_VectorToList_IsCompatible()
    {
        // Unused aliases do not affect payloads; vector<T> and list<T> also share the wire encoding.
        var oldSchema = await Parse("""
            namespace Test
            using Items = vector<int32>;
        """);
        var newSchema = await Parse("""
            namespace Test
            using Items = list<int32>;
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().NotContain(c => c.Category == ChangeCategory.BreakingWire);
    }

    [Fact]
    public async Task EnumInsertion_ShiftsImplicitValues_IsBreaking()
    {
        // Insertion shifts B from 1 to 2 and C from 2 to 3.
        var oldSchema = await Parse("""
            namespace Test
            enum Status { A, B, C }
        """);
        var newSchema = await Parse("""
            namespace Test
            enum Status { A, X, B, C }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().Contain(c => c.Category == ChangeCategory.BreakingWire);
    }

    [Fact]
    public async Task EnumInsertion_ExplicitSurroundings_NoCollision_IsCompatible()
    {
        var oldSchema = await Parse("""
            namespace Test
            enum Status { A = 0, B = 5 }
        """);
        var newSchema = await Parse("""
            namespace Test
            enum Status { A = 0, X, B = 5 }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().NotContain(c => c.Category == ChangeCategory.BreakingWire);
    }

    [Theory]
    [InlineData("struct Node { 0: nullable<Node> next; }")]
    [InlineData("struct A; struct B { 0: nullable<A> a; } struct A { 0: nullable<B> b; }")]
    [InlineData("struct Node<T> { 0: vector<Node<T>> children; 1: T value; }")]
    public async Task RecursiveSchemas_UseNamedIdentityAcrossIndependentParses(string body)
    {
        var old = await Parse("namespace Test " + body);
        var current = await Parse("namespace Test " + body);

        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task CommentsFieldOrderAliasNamesAndGenericParameterNamesAreCosmetic()
    {
        var old = await Parse("""
            namespace Test
            using Items<T> = vector<T>;
            struct Box<T> { 7: Items<T> items; 1: string label; }
            enum State { Z = 3, A = 0 }
            """);
        var current = await Parse("""
            namespace Test
            // These edits do not change the contract.
            enum State { A = 0, Z = 3 }
            using Renamed<U> = vector<U>;
            struct Box<U> { 1: string label = ""; 7: Renamed<U> items; }
            """);

        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task ComparisonsUseOnlyTheTwoSuppliedSchemas_NotPriorCalls()
    {
        var old = await Parse("namespace Test struct Record { 0: int32 original; }");
        var removed = await Parse("namespace Test struct Record {}");
        var renamed = await Parse("namespace Test struct Record { [JsonName(\"original\")] 0: int32 renamed; }");

        _checker.Compare(old, removed).HasBreakingChanges.Should().BeFalse();
        _checker.Compare(removed, renamed).Changes.Should().ContainSingle(c =>
            c.Id == DiagnosticIds.OptionalFieldAdded && c.Severity == ChangeSeverity.Info);
        _checker.Compare(old, renamed).Changes.Should().BeEmpty();
    }

    [Fact]
    public void NullPublicAstProducesAnInvalidSchemaDiagnostic() =>
        _checker.Compare(null!, Root()).Changes.Should().ContainSingle(c => c.Category == ChangeCategory.InvalidSchema);

    [Fact]
    public async Task InnerBodyChange_IsReportedAtDefinitionNotEveryReference()
    {
        const string schema = "namespace Test struct Inner { 0: int32 value; } struct Outer { 0: Inner a; 1: vector<Inner> b; }";
        var old = await Parse(schema);
        var current = await Parse(schema.Replace("int32", "string"));

        var changes = _checker.Compare(old, current).Changes;

        changes.Should().ContainSingle().Which.Location.Should().Be("Test.Inner.value");
        changes[0].Id.Should().Be(DiagnosticIds.FieldType);
    }

    [Fact]
    public void DuplicatePublicAstFields_AreDiagnosticsNotDictionaryExceptions()
    {
        foreach (var fields in new[] { new[] { Field(), Field(name: "other") }, new[] { Field(), Field(1) } })
        {
            var malformed = Root(Structure(fields));
            _checker.Compare(Root(), malformed).Changes.Should().ContainSingle(c =>
                c.Category == ChangeCategory.InvalidSchema && c.Id == DiagnosticIds.DuplicateField);
        }
    }

    [Fact]
    public void DuplicatePublicAstDeclarationsAndEnumNamesAndMethods_HaveDistinctIds()
    {
        var structure = Structure();
        var enumeration = new EnumDeclaration
        {
            Name = "State",
            Namespaces = structure.Namespaces,
            Attributes = [],
            Constants = [new("A", 0), new("A", 1)]
        };
        var method = new FunctionMethod
        {
            Name = "Get",
            Attributes = [],
            InputType = MethodType.Void.Instance,
            ResultType = MethodType.Void.Instance
        };
        var service = new ServiceDeclaration
        {
            Name = "Api",
            Namespaces = structure.Namespaces,
            Attributes = [],
            Methods = [method, method]
        };
        var cases = new[]
        {
            (Root(structure, structure with { }), DiagnosticIds.DuplicateDeclaration),
            (Root(enumeration), DiagnosticIds.DuplicateEnumMember),
            (Root(service), DiagnosticIds.DuplicateMethod)
        };

        foreach (var (schema, id) in cases)
        {
            _checker.Compare(Root(), schema).Changes.Should().ContainSingle(c => c.Id == id && c.Category == ChangeCategory.InvalidSchema);
        }
    }

    [Fact]
    public void MalformedPublicAstDefaultsAndInheritanceAreRejected()
    {
        var badDefault = Root(Structure(Field() with
        {
            DefaultValue = new Default.String("not an integer")
        }));
        _checker.Compare(Root(), badDefault).Changes.Should().ContainSingle(c => c.Category == ChangeCategory.InvalidSchema);

        var forward = new ForwardDeclaration { Name = "Record", Namespaces = Structure().Namespaces };
        var cyclic = Root(Structure() with
        {
            BaseType = new BondType.TypeReference(forward, [])
        });
        _checker.Compare(Root(), cyclic).Changes.Should().ContainSingle(c => c.Category == ChangeCategory.InvalidSchema && c.Description.Contains("Cyclic inheritance"));
    }

    [Fact]
    public async Task ForwardDefinitionPairAndRepeatedResolvedContext_Coalesce()
    {
        var old = await Parse("namespace Test struct Record; struct Record { 0: int32 value; }");
        var current = await Parse("namespace Test struct Record { 0: int32 value; }");
        old = old with
        {
            ResolvedDeclarations = old.ResolvedDeclarations.Concat(old.ResolvedDeclarations).ToArray()
        };

        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task StandaloneForwardBecomingDefinition_IsNotWireBreaking_ButReverseIsIncomplete()
    {
        var old = await Parse("namespace Test struct Record;");
        var current = await Parse("namespace Test struct Record { 0: required int32 value; }");

        _checker.Compare(old, current).HasBreakingChanges.Should().BeFalse();
        _checker.Compare(current, old).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.IncompleteDefinition && c.Severity == ChangeSeverity.Error);
    }

    [Fact]
    public async Task TransitiveImportedBodyChanges_AreIncludedWithoutParentStorms()
    {
        var old = await Imported("int32");
        var current = await Imported("string");

        _checker.Compare(old, current).Changes.Should().ContainSingle(c =>
            c.Id == DiagnosticIds.FieldType && c.Location == "Test.Leaf.value");
        _checker.Compare(old, current, new CompatibilityOptions { IncludeImports = false }).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task RootsOnlyComparisonStillResolvesTypedDefaultsThroughGenericAliases()
    {
        const string imported = "namespace Test enum State { A = 1, Alias = 1 }";
        async Task<Syntax.Bond> Schema(string member)
        {
            var result = await ParserFacade.ParseContentAsync(
                $"import \"state.bond\" namespace Test using Value<T> = T; struct Record {{ 0: Value<State> state = {member}; }}",
                Path.GetFullPath("compatibility-memory/root.bond"),
                (_, _) => Task.FromResult((Path.GetFullPath("compatibility-memory/state.bond"), imported)));
            result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
            return result.Ast!;
        }

        var old = await Schema("A");
        var current = await Schema("Alias");

        _checker.Compare(old, current, new CompatibilityOptions { IncludeImports = false }).Changes.Should().BeEmpty();
    }

    private static async Task<Syntax.Bond> Imported(string type)
    {
        var root = Path.GetFullPath("compatibility-memory/root.bond");
        var files = new Dictionary<string, string>
        {
            ["middle.bond"] = "import \"leaf.bond\" namespace Test struct Middle { 0: Leaf leaf; }",
            ["leaf.bond"] = $"namespace Test struct Leaf {{ 0: {type} value; }}"
        };
        var result = await ParserFacade.ParseContentAsync("import \"middle.bond\" namespace Test struct Root { 0: Middle value; }",
            root, (_, path) => Task.FromResult((Path.GetFullPath("compatibility-memory/" + path), files[path])));
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));

        return result.Ast!;
    }

    [Fact]
    public async Task ScopedAliasesWithSameQualifiedName_AreNotDuplicateGlobalDeclarations()
    {
        var root = Path.GetFullPath("compatibility-memory/root.bond");
        var files = new Dictionary<string, string>
        {
            ["a.bond"] = "namespace Test using Local = int32; struct A { 0: Local value; }",
            ["b.bond"] = "namespace Test using Local = string; struct B { 0: Local value; }"
        };
        var result = await ParserFacade.ParseContentAsync("import \"a.bond\" import \"b.bond\" namespace Test struct Root { 0: A a; 1: B b; }",
            root, (_, path) => Task.FromResult((Path.GetFullPath("compatibility-memory/" + path), files[path])));
        result.Success.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        _checker.Compare(result.Ast!, result.Ast!).Changes.Should().BeEmpty();

        var explicitSchema = await Parse("namespace Test struct A { 0: int32 value; } struct B { 0: string value; } struct Root { 0: A a; 1: B b; }");
        _checker.Compare(result.Ast!, explicitSchema).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task GenericAliasesExpandAtUses_AndAliasRenameIsCosmetic()
    {
        const string text = "namespace Test using Items<T> = map<string, vector<T>>; struct Record { 0: Items<int8> value; }";
        var old = await Parse(text);
        var renamed = await Parse(text.Replace("Items", "Renamed"));
        _checker.Compare(old, renamed).Changes.Should().BeEmpty();

        var changed = await Parse(text.Replace("vector<T>", "set<T>"));
        _checker.Compare(old, changed).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType && c.Location == "Test.Record.value");
    }

    [Fact]
    public void UnresolvedTypesRequireExplicitOptIn()
    {
        var schema = Root(Structure(Field(type: new BondType.UnresolvedType(["External"], []))));
        _checker.Compare(schema, schema).Changes.Should().OnlyContain(c => c.Id == DiagnosticIds.UnresolvedType);

        var options = new CompatibilityOptions { IncludeImports = false, AllowUnresolvedTypes = true };
        _checker.Compare(schema, schema, options).Changes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("optional", "optional", true)]
    [InlineData("optional", "required_optional", true)]
    [InlineData("required_optional", "required", false)]
    [InlineData("required", "required", false)]
    public async Task DefaultsOnlyBreakOmissionWhenAnOptionalSideExists(string before, string after, bool breaking)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: {before} int32 value = 1; }}");
        var current = await Parse($"namespace Test struct Record {{ 0: {after} int32 value = 2; }}");

        var change = _checker.Compare(old, current).Changes.Single(c => c.Id == DiagnosticIds.DefaultValue);

        (change.Severity == ChangeSeverity.Error).Should().Be(breaking);
        change.Recommendation.Should().NotContain("parse failure");
    }

    [Theory]
    [InlineData("int32", "0")]
    [InlineData("uint64", "0")]
    [InlineData("float", "0.0")]
    [InlineData("double", "0")]
    [InlineData("bool", "false")]
    [InlineData("string", "\"\"")]
    [InlineData("wstring", "\"\"")]
    public async Task ExplicitScalarZeroAndEmptyDefaults_AreCosmetic(string type, string value)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: {type} value; }}");
        var current = await Parse($"namespace Test struct Record {{ 0: {type} value = {value}; }}");
        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("optional", "required_optional", "producers first")]
    [InlineData("required_optional", "required", "every producer")]
    [InlineData("required", "required_optional", "readers first")]
    [InlineData("required_optional", "optional", "all readers")]
    [InlineData("required", "optional", "relax all readers first")]
    [InlineData("optional", "required", "producers first")]
    public async Task ModifierRecommendationsAreDirectional(string before, string after, string expected)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: {before} int32 value; }}");
        var current = await Parse($"namespace Test struct Record {{ 0: {after} int32 value; }}");

        var changes = _checker.Compare(old, current).Changes;

        changes.Should().ContainSingle();
        changes[0].Recommendation!.ToLowerInvariant().Should().Contain(expected);
        var direct = before == "optional" && after == "required" || before == "required" && after == "optional";
        (changes[0].Severity == ChangeSeverity.Error).Should().Be(direct);
    }

    [Fact]
    public async Task OptionalAddsAndRemovalsAreSafe_ButRequiredRemovalsBreak()
    {
        var empty = await Parse("namespace Test struct Record {}");
        var optional = await Parse("namespace Test struct Record { 0: int32 value; }");
        _checker.Compare(empty, optional).HasBreakingChanges.Should().BeFalse();
        _checker.Compare(optional, empty).HasBreakingChanges.Should().BeFalse();

        var required = await Parse("namespace Test struct Record { 0: required int32 value; }");
        _checker.Compare(required, empty).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldRemoved && c.Severity == ChangeSeverity.Error);
    }

    [Theory]
    [InlineData("map<string, vector<int8>>", "map<string, list<int16>>")]
    [InlineData("nullable<int16>", "nullable<int32>")]
    [InlineData("float", "double")]
    public async Task NestedPromotionsAreCompatibleWithRolloutWarnings(string before, string after)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: {before} value; }}");
        var current = await Parse($"namespace Test struct Record {{ 0: {after} value; }}");

        var result = _checker.Compare(old, current);

        result.HasBreakingChanges.Should().BeFalse();
        result.Changes.Should().Contain(c => c.Id == DiagnosticIds.FieldType && c.Severity == ChangeSeverity.Warning);
    }

    [Fact]
    public async Task NothingTransitionsSeparatePresenceFromNullableWireEncoding()
    {
        var scalar = await Parse("namespace Test struct Record { 0: int32 value; }");
        var nothing = await Parse("namespace Test struct Record { 0: int32 value = nothing; }");
        var nullable = await Parse("namespace Test struct Record { 0: nullable<int32> value; }");

        var changes = _checker.Compare(scalar, nothing).Changes;

        changes.Should().Contain(c => c.Id == DiagnosticIds.NothingDefault && c.Severity == ChangeSeverity.Error);
        changes.Should().ContainSingle();
        changes.Should().NotContain(c => c.Id == DiagnosticIds.FieldType);
        _checker.Compare(nothing, nullable).Changes.Should().Contain(c => c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingWire);
    }

    [Theory]
    [InlineData("int32", "nothing")]
    [InlineData("uint64", "18446744073709551615")]
    [InlineData("double", "-0.0")]
    [InlineData("State", "Alias")]
    public async Task AliasExpansionPreservesNothingAndTypedDefaults(string type, string value)
    {
        const string prefix = "namespace Test enum State { A = 1, Alias = 1 } using Value<T> = T; ";
        var old = await Parse(prefix + $"struct Record {{ 0: Value<{type}> value = {value}; }}");
        var current = await Parse(prefix + $"struct Record {{ 0: {type} value = {value}; }}");

        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task JsonNamePinnedRenamePasses_ChangedEffectiveNameBreaks()
    {
        var old = await Parse("namespace Test struct Record { 0: int32 original; }");
        var pinned = await Parse("namespace Test struct Record { [JsonName(\"original\")] 0: int32 renamed; }");
        var changes = _checker.Compare(old, pinned).Changes;
        changes.Should().BeEmpty();

        var changed = await Parse("namespace Test struct Record { [JsonName(\"changed\")] 0: int32 original; }");
        _checker.Compare(old, changed).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.TextName && c.Category == ChangeCategory.BreakingText);
    }

    [Theory]
    [InlineData("int32", "string")]
    [InlineData("vector<int32>", "list<string>")]
    [InlineData("map<string, int32>", "map<string, string>")]
    [InlineData("nullable<int32>", "nullable<string>")]
    [InlineData("blob", "vector<string>")]
    public async Task ChangedOrdinalsStillCheckSimpleJsonPayloadTypes(string before, string after)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: {before} value; }}");
        var current = await Parse($"namespace Test struct Record {{ 1: {after} value; }}");
        var result = _checker.Compare(old, current);

        result.ExitCode.Should().Be(1);
        result.Changes.Should().HaveCount(3);
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType
            && c.Category == ChangeCategory.BreakingText && c.Severity == ChangeSeverity.Error
            && c.Location == "Test.Record.value");
        result.Changes.Should().Contain(c => c.Id == DiagnosticIds.FieldRemoved && c.Category == ChangeCategory.Compatible);
        result.Changes.Should().Contain(c => c.Id == DiagnosticIds.OptionalFieldAdded && c.Category == ChangeCategory.Compatible);
    }

    [Theory]
    [InlineData("int32", "int32")]
    [InlineData("int8", "int64")]
    [InlineData("string", "wstring")]
    [InlineData("vector<int8>", "set<int16>")]
    [InlineData("blob", "list<int16>")]
    [InlineData("State", "int64")]
    public async Task ChangedOrdinalsWithCompatibleJsonPayloadsStayCompatible(string before, string after)
    {
        const string prefix = "namespace Test enum State { A = 0 } ";
        var oldDefault = before == "State" ? " = A" : "";
        var old = await Parse(prefix + $"struct Record {{ 0: {before} value{oldDefault}; }}");
        var current = await Parse(prefix + $"struct Record {{ 1: {after} value; }}");

        _checker.Compare(old, current).HasBreakingChanges.Should().BeFalse();
    }

    [Theory]
    [InlineData("1: string renamed;", false)]
    [InlineData("1: string Value;", false)]
    [InlineData("[JsonName(\"value\")] 1: string renamed;", true)]
    [InlineData("[JsonName(\"value\")] 1: int32 renamed;", false)]
    [InlineData("[JsonName(\"other\")] 1: string value;", false)]
    public async Task ChangedOrdinalsMatchJsonNameRatherThanSourceName(string field, bool breaking)
    {
        var old = await Parse("namespace Test struct Record { 0: int32 value; }");
        var current = await Parse($"namespace Test struct Record {{ {field} }}");
        var result = _checker.Compare(old, current);

        result.HasBreakingChanges.Should().Be(breaking);
        result.Changes.Count(c => c.Category == ChangeCategory.BreakingText).Should().Be(breaking ? 1 : 0);
    }

    [Theory]
    [InlineData("Old<int32>", "New<string>", true)]
    [InlineData("Old<int32>", "New<int32>", false)]
    [InlineData("Box<int32>", "Box<string>", true)]
    [InlineData("Wrapped<int32>", "Wrapped<string>", true)]
    [InlineData("bonded<Old<int32>>", "New<string>", true)]
    [InlineData("Node<int32>", "Node<string>", true)]
    public async Task ChangedOrdinalJsonChecksExpandNestedGenericAndAliasPayloads(string before, string after, bool breaking)
    {
        const string prefix = """
            namespace Test
            struct Old<T> { 0: T member; }
            struct New<T> { 1: T member; }
            struct Box<T> { 0: T member; }
            struct Node<T> { 0: nullable<Node<T>> next; 1: T member; }
            using Wrapped<T> = map<string, vector<T>>;
            """;
        var old = await Parse(prefix + $"struct Record {{ 0: {before} value; }}");
        var current = await Parse(prefix + $"struct Record {{ 1: {after} value; }}");
        var result = _checker.Compare(old, current);

        result.HasBreakingChanges.Should().Be(breaking);
        if (breaking)
        {
            result.Changes.Should().ContainSingle(c => c.Severity == ChangeSeverity.Error
                && c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingText);
        }
    }

    [Fact]
    public async Task JsonPayloadMismatchStopsBeforeLaterExpandingGenericFields()
    {
        // The reverse pass would reach recursion before the incompatible scalar.
        const string prefix = """
            namespace Test
            struct OldLoop<T> { 0: nullable<OldLoop<vector<T>>> next; }
            struct NewLoop<T> { 0: nullable<NewLoop<vector<T>>> next; }
            struct Old { 0: int32 first; 1: OldLoop<int32> later; }
            struct New { 0: NewLoop<int32> later; 1: string first; }
            """;
        var old = await Parse(prefix + "struct Record { 0: Old value; }");
        var current = await Parse(prefix + "struct Record { 1: New value; }");

        var result = _checker.Compare(old, current);

        result.ExitCode.Should().Be(1);
        result.Changes.Should().HaveCount(3);
        result.Changes.Should().ContainSingle(change => change.Id == DiagnosticIds.FieldType
            && change.Category == ChangeCategory.BreakingText && change.Severity == ChangeSeverity.Error
            && change.Location == "Test.Record.value");
        result.Changes.Should().NotContain(change => change.Id == DiagnosticIds.IncompleteDefinition);
    }

    [Fact]
    public async Task ChangedGenericJsonMappingsAreCheckedAtConcreteNestedUses()
    {
        const string schema = """
            namespace Test
            struct Box<T, U> { 0: T value; }
            struct Wrapper<V> { 0: Box<V, string> box; }
            struct Record { 0: Wrapper<int32> wrapped; }
            """;
        var old = await Parse(schema);
        var current = await Parse(schema.Replace("0: T value;", "1: U value;"));

        _checker.Compare(old, current).Changes.Should().ContainSingle(c => c.Severity == ChangeSeverity.Error
            && c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingText
            && c.Location == "Test.Record.wrapped.box.value");
    }

    [Theory]
    [InlineData("0: int32 value;", "", "", "0: string value;")]
    [InlineData("", "0: int32 value;", "0: string value;", "")]
    [InlineData("[JsonName(\"key\")] 0: int32 value;", "", "", "[JsonName(\"key\")] 0: string renamed;")]
    public async Task SimpleJsonMatchesFieldsAcrossInheritanceLevels(string oldBase, string oldFields, string newBase, string newFields)
    {
        var old = await Parse($"namespace Test struct Base {{ {oldBase} }} struct Record : Base {{ {oldFields} }}");
        var current = await Parse($"namespace Test struct Base {{ {newBase} }} struct Record : Base {{ {newFields} }}");

        _checker.Compare(old, current).Changes.Should().ContainSingle(c => c.Severity == ChangeSeverity.Error
            && c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingText);
    }

    [Theory]
    [InlineData("int32", false)]
    [InlineData("string", true)]
    public async Task AddedDerivedJsonNamesAlsoMatchInheritedFields(string type, bool breaking)
    {
        const string prefix = "namespace Test struct Base { 0: int32 value; } ";
        var old = await Parse(prefix + "struct Record : Base {}");
        var current = await Parse(prefix + $"struct Record : Base {{ [JsonName(\"value\")] 0: {type} added; }}");

        _checker.Compare(old, current).HasBreakingChanges.Should().Be(breaking);
        _checker.Compare(current, old).HasBreakingChanges.Should().Be(breaking);
    }

    [Theory]
    [InlineData("int32", false)]
    [InlineData("string", true)]
    public async Task ShadowedGenericJsonWritersAreCheckedAtConcreteUses(string type, bool breaking)
    {
        var schema = $$"""
            namespace Test
            struct Base<T> { 0: T value; }
            struct Derived<T, U> : Base<T> {}
            struct Record { 0: Derived<int32, {{type}}> item; }
            """;
        var old = await Parse(schema);
        var current = await Parse(schema.Replace("Base<T> {}", "Base<T> { [JsonName(\"value\")] 1: U extra; }"));

        foreach (var (previous, next) in new[] { (old, current), (current, old) })
        {
            var result = _checker.Compare(previous, next);
            result.HasBreakingChanges.Should().Be(breaking);
            if (breaking)
            {
                result.Changes.Should().ContainSingle(c => c.Severity == ChangeSeverity.Error
                    && c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingText
                    && c.Location.StartsWith("Test.Record.item.", StringComparison.Ordinal));
            }
        }
    }

    [Theory]
    [InlineData("int32", false)]
    [InlineData("string", true)]
    public async Task ChangedOrdinalPayloadsCheckInheritedJsonNameCollisions(string type, bool breaking)
    {
        var prefix = $$"""
            namespace Test
            struct Base { 0: int32 value; }
            struct Old : Base {}
            struct New : Base { [JsonName("value")] 0: {{type}} added; }
            """;
        var old = await Parse(prefix + "struct Record { 0: Old payload; }");
        var current = await Parse(prefix + "struct Record { 1: New payload; }");

        _checker.Compare(old, current).HasBreakingChanges.Should().Be(breaking);
    }

    [Fact]
    public async Task UnchangedShadowedJsonNamesDoNotCreateDiagnostics()
    {
        const string schema = """
            namespace Test
            struct Base { 0: int32 value; }
            struct Record : Base { [JsonName("value")] 0: string shadowed; }
            """;
        var old = await Parse(schema);
        var current = await Parse(schema);

        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task InheritedJsonTypeChangesAreReportedOnlyAtTheDefinition()
    {
        const string schema = """
            namespace Before
            struct Base { 0: int32 value; }
            struct Derived : Base {}
            struct Record { 0: Derived first; 1: vector<Derived> second; }
            """;
        var old = await Parse(schema);
        var current = await Parse(schema.Replace("Before", "After").Replace("0: int32 value;", "1: string value;"));

        _checker.Compare(old, current).Changes.Should().ContainSingle(c => c.Severity == ChangeSeverity.Error
            && c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingText
            && c.Location == "Before.Base.value");
    }

    [Fact]
    public async Task InheritedGenericJsonMappingsUseConcreteBaseArguments()
    {
        const string schema = "namespace Test struct Base<T> { 0: T value; } struct Record : Base<int32> {}";
        var old = await Parse(schema);
        var current = await Parse(schema.Replace("0: T value;", "1: string value;"));

        _checker.Compare(old, current).Changes.Should().ContainSingle(c => c.Severity == ChangeSeverity.Error
            && c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingText
            && c.Location == "Test.Record.base.value");
    }

    [Fact]
    public async Task SameOrdinalTypeChangesDoNotDuplicateJsonDiagnostics()
    {
        var old = await Parse("namespace Test struct Record { 0: int32 value; }");
        var current = await Parse("namespace Test struct Record { 0: string value; }");

        _checker.Compare(old, current).Changes.Should().ContainSingle(c =>
            c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingWire);
    }

    [Fact]
    public async Task JsonTypeCollisionDiagnosticsRemainSuppressible()
    {
        var old = await Parse("namespace Test struct Record { 0: int32 value; }");
        var current = await Parse("namespace Test struct Record { 1: string value; }");
        var result = _checker.Compare(old, current, new CompatibilityOptions
        {
            SuppressedDiagnosticIds = new HashSet<string> { DiagnosticIds.FieldType }
        });

        result.ExitCode.Should().Be(0);
        result.Changes.Should().HaveCount(3);
        result.Changes.Should().ContainSingle(c => c.IsSuppressed
            && c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingText);
    }

    [Fact]
    public async Task XmlAndCustomAttributesAreIgnored()
    {
        var old = await Parse("namespace Test [xmlns(\"urn:old\")] [Custom(\"old\")] struct Record { [Generator(\"old\")] 0: int32 value; }");
        var current = await Parse("namespace Test [xmlns(\"urn:new\")] [Custom(\"new\")] struct Record { [Generator(\"new\")] 0: int32 value; }");
        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task JsonNameMatchingRemainsCaseSensitive()
    {
        var old = await Parse("namespace Test [xmlns(\"urn:test\")] struct Record { 0: int32 value; }");
        var current = await Parse("namespace Test [xmlns(\"URN:TEST\")] struct Record { 0: int32 Value; }");
        _checker.Compare(old, current).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.TextName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[xmlns(\"urn:stable\")]")]
    public async Task XmlAttributesDoNotAffectGenericWirePromotions(string attribute)
    {
        var text = $"namespace Test {attribute} struct Box<T> {{ 0: T value; }} struct Record {{ 0: Box<int8> box; }}";
        var old = await Parse(text);
        var current = await Parse(text.Replace("Box<int8>", "Box<int16>"));
        var result = _checker.Compare(old, current);
        result.HasBreakingChanges.Should().BeFalse();
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType && c.Severity == ChangeSeverity.Warning);
    }

    [Fact]
    public async Task NamespaceOrderAndCSharpNamespacesAreNotWireChanges()
    {
        var old = await Parse("namespace csharp App namespace Wire struct Record { 0: int32 value; }");
        var reordered = await Parse("namespace Wire namespace csharp App struct Record { 0: int32 value; }");
        _checker.Compare(old, reordered).Changes.Should().BeEmpty();

        var current = await Parse("namespace csharp NewApp namespace Wire struct Record { 0: int32 value; }");
        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task StructRenameDoesNotInventJsonTypeMetadata()
    {
        var old = await Parse("namespace Test struct Old { 0: int32 value; }");
        var current = await Parse("namespace Test struct New { 0: int32 value; }");
        var changes = _checker.Compare(old, current).Changes;
        changes.Should().Contain(c => c.Id == DiagnosticIds.DeclarationRemoved && c.Category == ChangeCategory.Compatible);
        changes.Should().NotContain(c => c.Category == ChangeCategory.BreakingWire || c.Category == ChangeCategory.BreakingText);
        _checker.Compare(old, current).HasBreakingChanges.Should().BeFalse();
    }

    [Fact]
    public async Task GenericParameterNamesConstraintsAndUnusedArityAreIgnored()
    {
        var old = await Parse("namespace Test struct Record<T> { 0: T value; }");
        var renamed = await Parse("namespace Test struct Record<U> { 0: U value; }");
        _checker.Compare(old, renamed).Changes.Should().BeEmpty();

        var arity = await Parse("namespace Test struct Record<T, U> { 0: T value; }");
        _checker.Compare(old, arity).Changes.Should().BeEmpty();

        var constraint = await Parse("namespace Test struct Record<T : value> { 0: T value; }");
        _checker.Compare(old, constraint).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task GenericInstantiationClassifiesUsedLayout_NotUnusedArguments()
    {
        const string text = "namespace Test struct Box<T> { 0: T value; } struct Record { 0: Box<int8> box; }";
        var old = await Parse(text);
        var current = await Parse(text.Replace("Box<int8>", "Box<int16>"));
        _checker.Compare(old, current).HasBreakingChanges.Should().BeFalse();

        var breaking = await Parse(text.Replace("Box<int8>", "Box<string>"));
        _checker.Compare(old, breaking).HasBreakingChanges.Should().BeTrue();

        var unusedOld = await Parse(text.Replace("0: T value;", ""));
        var unusedNew = await Parse(text.Replace("0: T value;", "").Replace("Box<int8>", "Box<string>"));
        _checker.Compare(unusedOld, unusedNew).Changes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("struct Box<T, U> { 0: T value; }", "Box<int32, string>")]
    [InlineData("struct Box<U, T> { 0: T value; }", "Box<string, int32>")]
    [InlineData("struct Box<T : value> { 0: T value; }", "Box<int32>")]
    public async Task UnusedGenericArityAndConstraintEditsDoNotInventWireErrors(string declaration, string use)
    {
        var old = await Parse("namespace Test struct Box<T> { 0: T value; } struct Record { 0: Box<int32> box; }");
        var current = await Parse($"namespace Test {declaration} struct Record {{ 0: {use} box; }}");
        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("int32", false)]
    [InlineData("string", true)]
    public async Task GenericTemplateChangesUseActualPayloadTypes(string type, bool breaking)
    {
        var old = await Parse($"namespace Test struct Box<T> {{ 0: T value; }} struct Record {{ 0: Box<{type}> box; }}");
        var current = await Parse($"namespace Test struct Box<T> {{ 0: int32 value; }} struct Record {{ 0: Box<{type}> box; }}");
        var result = _checker.Compare(old, current);
        result.HasBreakingChanges.Should().Be(breaking);
        if (breaking)
        {
            result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType && c.Location == "Test.Record.box.value");
        }
        else
        {
            result.Changes.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ChangingWhichGenericArgumentIsSerializedIsDetectedThroughNestedUses()
    {
        const string schema = """
            namespace Test
            struct Box<T, U> { 0: T value; }
            struct Wrapper<W> { 0: Box<W, string> box; }
            struct Record { 0: Wrapper<int32> wrapped; }
            """;
        var old = await Parse(schema);
        var current = await Parse(schema.Replace("0: T value;", "0: U value;"));
        _checker.Compare(old, current).Changes.Should().ContainSingle(c =>
            c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingWire && c.Location == "Test.Record.wrapped.box.value");
    }

    [Fact]
    public async Task RecursiveGenericComparisonDoesNotConflateDifferentInstantiations()
    {
        const string text = """
            namespace Test
            struct Node<T, U> { 0: nullable<Node<U, string>> next; 1: T value; }
            struct Record { 0: Node<int32, int32> node; }
            """;
        var old = await Parse(text);
        var current = await Parse(text.Replace("1: T value;", "1: U value;"));
        _checker.Compare(old, current).Changes.Should().ContainSingle(c =>
            c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingWire && c.Location == "Test.Record.node.next.value");
    }

    [Theory]
    [InlineData("int32", false)]
    [InlineData("T", true)]
    public async Task GenericBaseArgumentsOnlyMatterWhenTheyChangePayload(string fieldType, bool breaking)
    {
        var text = $"namespace Test struct Base<T> {{ 0: {fieldType} value; }} struct Record : Base<int32> {{}}";
        var old = await Parse(text);
        var current = await Parse(text.Replace("Base<int32>", "Base<string>"));
        var result = _checker.Compare(old, current);
        result.HasBreakingChanges.Should().Be(breaking);
        if (breaking)
        {
            result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.BaseType && c.Category == ChangeCategory.BreakingWire);
        }
        else
        {
            result.Changes.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task NominallyDifferentBasesWithIdenticalPayloadsAreCompatible()
    {
        const string declarations = "namespace Test struct First { 0: required int32 value; } struct Second { 0: required int32 value; } ";
        var old = await Parse(declarations + "struct Record : First {}");
        var current = await Parse(declarations + "struct Record : Second {}");
        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("int32", false)]
    [InlineData("string", true)]
    public async Task StructRenamesCompareReferencedPayloads_NotDeclarationIdentity(string type, bool breaking)
    {
        var old = await Parse("namespace Test struct Old { 0: int32 value; } struct Record { 0: Old payload; }");
        var current = await Parse($"namespace Test struct New {{ 0: {type} value; }} struct Record {{ 0: New payload; }}");
        var result = _checker.Compare(old, current);
        result.HasBreakingChanges.Should().Be(breaking);
        if (breaking)
        {
            result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType && c.Location == "Test.Record.payload.value");
        }
        else
        {
            result.Changes.Should().OnlyContain(c => c.Category == ChangeCategory.Compatible);
        }
    }

    [Theory]
    [InlineData("0: int32 renamed;", "BOND0301")]
    [InlineData("0: int32 value = 1;", "BOND0201")]
    [InlineData("0: required int32 value;", "BOND0003")]
    [InlineData("0: int32 value = nothing;", "BOND0202")]
    public async Task RenamedPayloadsStillCheckJsonNamesDefaultsAndPresence(string field, string diagnostic)
    {
        var old = await Parse("namespace Test struct Old { 0: int32 value; } struct Record { 0: Old payload; }");
        var current = await Parse($"namespace Test struct New {{ {field} }} struct Record {{ 0: New payload; }}");
        _checker.Compare(old, current).Changes.Should().Contain(c => c.Id == diagnostic && c.Severity == ChangeSeverity.Error);
    }

    [Fact]
    public async Task RecursiveGenericRenamesRemainFiniteAndCompatible()
    {
        const string schema = "namespace Test struct Old<T> { 0: nullable<Old<T>> next; 1: T value; } struct Record { 0: Old<int32> root; }";
        var old = await Parse(schema);
        var current = await Parse(schema.Replace("Old", "New"));
        _checker.Compare(old, current).HasBreakingChanges.Should().BeFalse();
    }

    [Fact]
    public async Task NamespaceMovesPreservePayloadChecksWithoutRepeatedParentErrors()
    {
        const string schema = "namespace Before struct Inner { 0: int32 value; } struct Record { 0: Inner a; 1: vector<Inner> b; }";
        var old = await Parse(schema);
        var moved = await Parse(schema.Replace("Before", "After"));
        _checker.Compare(old, moved).Changes.Should().BeEmpty();

        var changed = await Parse(schema.Replace("Before", "After").Replace("int32", "string"));
        _checker.Compare(old, changed).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType && c.Location == "Before.Inner.value");
    }

    [Fact]
    public void GenericConstraintValidationStillRejectsMalformedPublicAst()
    {
        var constrained = Structure() with
        {
            Name = "Box",
            TypeParameters = [new TypeParam("T", TypeConstraint.Value)]
        };
        var schema = Root(constrained, Structure(Field(type: new BondType.TypeReference(constrained, [BondType.String.Instance]))));
        _checker.Compare(Root(), schema).Changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.InvalidSchema && c.Description.Contains("value constraint"));
    }

    [Fact]
    public void SignedIntegerGenericArgumentsAreNotLost()
    {
        var old = Root(Structure(Field(type: new BondType.UnresolvedType(["Sized"], [new BondType.IntTypeArg(-3)]))));
        var current = Root(Structure(Field(type: new BondType.UnresolvedType(["Sized"], [new BondType.IntTypeArg(3)]))));
        _checker.Compare(old, current, new CompatibilityOptions { AllowUnresolvedTypes = true }).Changes.Should().Contain(c =>
            c.Id == DiagnosticIds.FieldType && c.Description.Contains("-3") && c.Description.Contains("<3>"));
    }

    [Fact]
    public async Task EnumAliasesAndRenamedTypedDefaultsCompareTheirNumbers()
    {
        var old = await Parse("namespace Test enum State { A = 1, Alias = 1 } struct Record { 0: State state = A; }");
        var current = await Parse("namespace Test enum State { A = 1, Alias = 1 } struct Record { 0: State state = Alias; }");
        _checker.Compare(old, current).Changes.Should().BeEmpty();

        var removed = await Parse("namespace Test enum State { Alias = 1 } struct Record { 0: State state = Alias; }");
        _checker.Compare(old, removed).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.EnumMemberRemoved && c.Category == ChangeCategory.Compatible);
        _checker.Compare(old, removed).HasBreakingChanges.Should().BeFalse();
    }

    [Theory]
    [InlineData("int32", "State")]
    [InlineData("State", "int32")]
    [InlineData("State", "Other")]
    public async Task EnumRepresentationChangesAreSemanticWarnings_NotWireFailures(string before, string after)
    {
        const string prefix = "namespace Test enum State { A = 0 } enum Other { B = 1 } ";
        var old = await Parse(prefix + $"struct Record {{ 0: required {before} state; }}");
        var current = await Parse(prefix + $"struct Record {{ 0: required {after} state; }}");
        var result = _checker.Compare(old, current);
        result.HasBreakingChanges.Should().BeFalse();
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.EnumTypeSemantics && c.Severity == ChangeSeverity.Warning);
    }

    [Theory]
    [InlineData("int8")]
    [InlineData("int16")]
    public async Task SmallIntegerToEnumIsCompatibleWithTaggedRollout(string before)
    {
        var old = await Parse($"namespace Test enum State {{ A = 0 }} struct Record {{ 0: required {before} state; }}");
        var current = await Parse("namespace Test enum State { A = 0 } struct Record { 0: required State state; }");
        _checker.Compare(old, current).HasBreakingChanges.Should().BeFalse();
        _checker.Compare(old, current).Changes.Should().Contain(c => c.Id == DiagnosticIds.EnumTypeSemantics && c.Severity == ChangeSeverity.Warning);
    }

    [Theory]
    [InlineData("State", "int64")]
    [InlineData("vector<State>", "list<int64>")]
    [InlineData("map<State, vector<State>>", "map<int64, list<int64>>")]
    [InlineData("Value<State>", "Value<int64>")]
    public async Task EnumToInt64WideningPreservesPromotionAndSemanticWarnings(string before, string after)
    {
        const string prefix = "namespace Test enum State { A = 0 } using Value<T> = T; ";
        var old = await Parse(prefix + $"struct Record {{ 0: required {before} value; }}");
        var current = await Parse(prefix + $"struct Record {{ 0: required {after} value; }}");
        var result = _checker.Compare(old, current);

        result.HasBreakingChanges.Should().BeFalse();
        result.Changes.Should().HaveCount(2).And.OnlyContain(c =>
            c.Category == ChangeCategory.Compatible && c.Severity == ChangeSeverity.Warning);
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType
            && c.Recommendation!.Contains("consumers before producers"));
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.EnumTypeSemantics);
    }

    [Theory]
    [InlineData("int64", "State")]
    [InlineData("State", "int16")]
    [InlineData("State", "uint64")]
    [InlineData("uint32", "State")]
    [InlineData("vector<int64>", "vector<State>")]
    public async Task EnumNarrowingAndUnsignedConversionsRemainBreaking(string before, string after)
    {
        const string prefix = "namespace Test enum State { A = 0 } ";
        var old = await Parse(prefix + $"struct Record {{ 0: required {before} value; }}");
        var current = await Parse(prefix + $"struct Record {{ 0: required {after} value; }}");

        _checker.Compare(old, current).Changes.Should().ContainSingle(c =>
            c.Id == DiagnosticIds.FieldType && c.Category == ChangeCategory.BreakingWire && c.Severity == ChangeSeverity.Error);
    }

    [Fact]
    public void EnumExplicitHighHexIsInt32Normalized_ButImplicitOverflowIsInvalid()
    {
        var declaration = new EnumDeclaration
        {
            Name = "State",
            Namespaces = Structure().Namespaces,
            Attributes = [],
            Constants = [new("A", uint.MaxValue), new("B", null)]
        };
        var normalized = declaration with
        {
            Constants = [new("A", -1), new("B", 0)]
        };
        _checker.Compare(Root(declaration), Root(normalized)).Changes.Should().BeEmpty();

        var overflow = declaration with
        {
            Constants = [new("A", int.MaxValue), new("B", null)]
        };
        _checker.Compare(Root(), Root(overflow)).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.EnumOverflow && c.Category == ChangeCategory.InvalidSchema);
    }

    [Fact]
    public async Task DiagnosticsAreDeterministicSuppressibleAndRollUpUnsuppressedSeverity()
    {
        var old = await Parse("namespace Test struct Z { 1: int32 second; 0: int32 first; } struct A {}");
        var current = await Parse("namespace Test struct A { 0: required int32 added; } struct Z { 0: string first; 1: string second; }");
        var result = _checker.Compare(old, current);
        result.Changes.Select(c => (c.Location, c.Id, c.Description)).Should().Equal(
            result.Changes.OrderBy(c => c.Location, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal)
                .ThenBy(c => c.Description, StringComparer.Ordinal).Select(c => (c.Location, c.Id, c.Description)));
        result.Changes.Should().OnlyContain(c => DiagnosticIds.All.Contains(c.Id));
        result.MaxSeverity.Should().Be(ChangeSeverity.Error);
        result.ExitCode.Should().Be(1);

        var suppressed = _checker.Compare(old, current, new CompatibilityOptions
        {
            SuppressedDiagnosticIds = new HashSet<string> { DiagnosticIds.FieldType, DiagnosticIds.RequiredFieldAdded }
        });
        suppressed.Changes.Should().HaveCount(result.Changes.Count).And.OnlyContain(c => c.IsSuppressed);
        suppressed.ExitCode.Should().Be(0);
        suppressed.MaxSeverity.Should().Be(ChangeSeverity.Info);

        ((Action)(() => _checker.Compare(old, current, new CompatibilityOptions { SuppressedDiagnosticIds = new HashSet<string> { "BOND9999" } })))
            .Should().Throw<ArgumentException>().WithMessage("*BOND9999*");
    }

    [Fact]
    public async Task ScopeIsAlwaysTaggedBinaryAndSimpleJson_WithNoProtocolOrApiSwitches()
    {
        typeof(CompatibilityOptions).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "IncludeImports", "AllowUnresolvedTypes", "SuppressedDiagnosticIds");
        typeof(CompatibilityOptions).GetMethod("ForProtocols").Should().BeNull();
        Enum.GetNames<ChangeCategory>().Should().NotContain("BreakingSource");

        var old = await Parse("namespace Test struct Record { 0: optional int32 value; }");
        var current = await Parse("namespace Test struct Record { 0: required int32 renamed; }");
        var result = _checker.Compare(old, current);
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.OptionalToRequired && c.Category == ChangeCategory.BreakingWire);
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.TextName && c.Category == ChangeCategory.BreakingText);
        result.HasBreakingChanges.Should().BeTrue();
    }

    [Theory]
    [InlineData("Child", "bonded<Child>")]
    [InlineData("bonded<Child>", "Child")]
    [InlineData("vector<Child>", "vector<bonded<Child>>")]
    [InlineData("Box<Child>", "Box<bonded<Child>>")]
    public async Task BondedTransitionsPreserveTaggedAndJsonPayloads(string before, string after)
    {
        const string declarations = "namespace Test struct Child {} struct Box<T> { 0: T value; } ";
        var old = await Parse(declarations + $"struct Record {{ 0: {before} value; }}");
        var current = await Parse(declarations + $"struct Record {{ 0: {after} value; }}");
        _checker.Compare(old, current).HasBreakingChanges.Should().BeFalse();
    }

    [Fact]
    public async Task ListVectorChangeIsCompatibleRegardlessOfGeneratedCollectionType()
    {
        var old = await Parse("namespace Test struct Record { 0: vector<int32> values; }");
        var current = await Parse("namespace Test struct Record { 0: list<int32> values; }");
        _checker.Compare(old, current).HasBreakingChanges.Should().BeFalse();
        _checker.Compare(old, current).Changes.Should().ContainSingle(change =>
            change.Id == DiagnosticIds.FieldType && change.Category == ChangeCategory.Compatible);
    }

    [Theory]
    [InlineData("blob", "list<int16>")]
    [InlineData("blob", "vector<int32>")]
    [InlineData("blob", "list<int64>")]
    [InlineData("map<string, blob>", "map<string, vector<int16>>")]
    [InlineData("vector<blob>", "list<vector<int16>>")]
    [InlineData("Bytes", "Values<int16>")]
    public async Task BlobElementsUseRecursiveSignedPromotions(string before, string after)
    {
        const string prefix = "namespace Test using Bytes = blob; using Values<T> = vector<T>; ";
        var old = await Parse(prefix + $"struct Record {{ 0: required {before} value; }}");
        var current = await Parse(prefix + $"struct Record {{ 0: required {after} value; }}");
        var result = _checker.Compare(old, current);

        result.HasBreakingChanges.Should().BeFalse();
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType
            && c.Category == ChangeCategory.Compatible && c.Severity == ChangeSeverity.Warning
            && c.Recommendation!.Contains("consumers before producers"));
    }

    [Theory]
    [InlineData("blob", "list<int8>")]
    [InlineData("blob", "vector<int8>")]
    [InlineData("list<int8>", "blob")]
    [InlineData("vector<int8>", "blob")]
    [InlineData("map<string, vector<int8>>", "map<string, blob>")]
    public async Task BlobAndSignedByteSequencesAreRepresentationEquivalent(string before, string after)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: required {before} value; }}");
        var current = await Parse($"namespace Test struct Record {{ 0: required {after} value; }}");

        _checker.Compare(old, current).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType
            && c.Category == ChangeCategory.Compatible && c.Severity == ChangeSeverity.Info);
    }

    [Theory]
    [InlineData("list<int16>", "blob")]
    [InlineData("vector<int64>", "blob")]
    [InlineData("blob", "list<uint8>")]
    [InlineData("blob", "vector<uint16>")]
    [InlineData("vector<uint8>", "blob")]
    [InlineData("blob", "set<int16>")]
    [InlineData("blob", "string")]
    public async Task BlobNarrowingUnsignedAndNonListConversionsRemainBreaking(string before, string after)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: required {before} value; }}");
        var current = await Parse($"namespace Test struct Record {{ 0: required {after} value; }}");

        _checker.Compare(old, current).Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType
            && c.Category == ChangeCategory.BreakingWire && c.Severity == ChangeSeverity.Error);
    }

    [Fact]
    public async Task BlobToEnumSequencePreservesBothPromotionWarnings()
    {
        var old = await Parse("namespace Test enum State { A = 0 } struct Record { 0: required blob value; }");
        var current = await Parse("namespace Test enum State { A = 0 } struct Record { 0: required list<State> value; }");
        var result = _checker.Compare(old, current);

        result.HasBreakingChanges.Should().BeFalse();
        result.Changes.Should().HaveCount(2).And.OnlyContain(c => c.Severity == ChangeSeverity.Warning);
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.FieldType);
        result.Changes.Should().ContainSingle(c => c.Id == DiagnosticIds.EnumTypeSemantics);
    }

    [Theory]
    [InlineData("string", "wstring")]
    [InlineData("string", "nullable<string>")]
    public async Task WireEncodingChangesBreakEvenWhenGeneratedClrTypeIsUnchanged(string before, string after)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: {before} value; }}");
        var current = await Parse($"namespace Test struct Record {{ 0: {after} value; }}");
        var changes = _checker.Compare(old, current).Changes;
        changes.Should().Contain(change => change.Category == ChangeCategory.BreakingWire);
        changes.Should().ContainSingle(change => change.Id == DiagnosticIds.FieldType);
    }

    [Fact]
    public async Task MetadataFieldsUseTheirEffectiveRequiredOptionalModifier()
    {
        var old = await Parse("namespace Test struct Record { 0: optional bond_meta::name name; }");
        var current = await Parse("namespace Test struct Record { 0: required bond_meta::name name; }");
        _checker.Compare(old, current).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumValueChangesRemainWireSemanticErrors()
    {
        var old = await Parse("namespace Test enum State { Value = 1 }");
        var current = await Parse("namespace Test enum State { Value = 2 }");
        _checker.Compare(old, current).Changes.Should().ContainSingle(change =>
            change.Id == DiagnosticIds.EnumValue && change.Category == ChangeCategory.BreakingWire);
    }

    [Theory]
    [InlineData("float")]
    [InlineData("double")]
    public async Task SignedZeroDefaultChangesPreserveTheirSemanticDifference(string type)
    {
        var old = await Parse($"namespace Test struct Record {{ 0: {type} value = -0.0; }}");
        var current = await Parse($"namespace Test struct Record {{ 0: {type} value = 0.0; }}");
        _checker.Compare(old, current).Changes.Should().ContainSingle(change =>
            change.Id == DiagnosticIds.DefaultValue && change.Category == ChangeCategory.BreakingWire);
    }
}
