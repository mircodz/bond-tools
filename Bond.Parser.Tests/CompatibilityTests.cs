using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.Compatibility;
using Bond.Parser.Parser;
using FluentAssertions;

namespace Bond.Parser.Tests;

public class CompatibilityTests
{
    private readonly CompatibilityChecker _checker = new();

    private async Task<Syntax.Bond> ParseSchema(string input)
    {
        var result = await ParserFacade.ParseStringAsync(input);
        result.Success.Should().BeTrue($"parsing should succeed but got errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
        return result.Ast!;
    }

    #region Field Changes - Breaking

    [Fact]
    public async Task AddingRequiredField_IsBreaking()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required string id; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User {
                0: required string id;
                1: required string email;
            }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required string id; }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            struct User { 1: required string id; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        // Changing ordinals is detected as remove + add
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required int32 age; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required int32 status = 0; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: optional string name; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required string name; }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            struct User { 0: optional string name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.ToLower().Contains("modifier"));
    }

    #endregion

    [Fact]
    public async Task RenamingField_IsBreakingText()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: optional string name; }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            struct User { 0: optional string full_name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingText &&
            c.Description.Contains("name") &&
            c.Description.Contains("full_name"));
    }

    #region Field Changes - Compatible

    [Fact]
    public async Task AddingOptionalField_IsCompatible()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required string id; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User {
                0: required string id;
                1: optional string email;
            }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: optional string name; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required_optional string name; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required int32 value; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required float value; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required vector<string> tags; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required int32 status; }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            enum Status { Active = 0 }
            struct User { 0: required Status status; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("int32"));
    }

    #endregion

    #region Enum Changes

    [Fact]
    public async Task AddingEnumConstant_IsCompatible()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            enum Status { Active = 0 }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            enum Status { Active = 0, Inactive = 1 }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            enum Status { Active = 0, Inactive = 1 }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            enum Status { Active = 0 }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.Compatible &&
            c.Description.Contains("removed") &&
            c.Description.Contains("Inactive"));
    }

    #endregion

    #region Inheritance Changes

    [Fact]
    public async Task ChangingBaseStruct_IsBreaking()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            struct Base1 { 0: required string id; }
            struct Base2 { 0: required int32 id; }
            struct User : Base1 { 1: required string name; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct Base { 0: required string id; }
            struct User { 1: required string name; }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            struct Base { 0: required string id; }
            struct User : Base { 1: required string name; }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("Inheritance"));
    }

    #endregion

    #region Declaration Changes

    [Fact]
    public async Task RemovingDeclarationAlone_IsCompatible()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required string id; }
            struct Profile { 0: required string bio; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct User { 0: required string id; }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            struct Status { 0: required int32 value; }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            enum Status { Active = 0 }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("kind changed"));
    }

    #endregion

    #region No Changes

    [Fact]
    public async Task IdenticalSchemas_NoChanges()
    {
        var schema = await ParseSchema("""
            namespace Test
            struct User {
                0: required string id;
                1: optional string name;
            }
        """);

        var changes = _checker.CheckCompatibility(schema, schema);

        changes.Should().BeEmpty();
    }

    #endregion

    #region Issues

    [Fact]
    public async Task BreakingCheck_WithImports_ResolvesTypes()
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

    #endregion

    #region Service Changes

    [Fact]
    public async Task ServiceInheritanceChange_IsBreaking()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            service Base1 { void Method1(void); }
            service Base2 { void Method2(void); }
            service MySvc : Base1 { void Method3(void); }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            service Base { void BaseMethod(void); }
            service MySvc { void MyMethod(void); }
        """);
        var newSchema = await ParseSchema("""
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
        var oldSchema = await ParseSchema("""
            namespace Test
            service Base { void BaseMethod(void); }
            service MySvc : Base { void MyMethod(void); }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            service Base { void BaseMethod(void); }
            service MySvc { void MyMethod(void); }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().ContainSingle(c =>
            c.Category == ChangeCategory.BreakingWire &&
            c.Description.Contains("Inheritance"));
    }

    #endregion

    #region Enum edge cases

    [Fact]
    public async Task AddingEnumConstantInMiddle_WithoutShiftingValues_IsLegalNumericAlias()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            enum Status { Active = 0, Inactive = 1 }
        """);
        var newSchema = await ParseSchema("""
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
    public async Task UnusedAliasTypeChange_DoesNotChangeWireOrGeneratedApi()
    {
        var oldSchema = await ParseSchema("""
            namespace Test
            using MyType = string;
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            using MyType = int32;
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().BeEmpty();
    }

    [Fact]
    public async Task AliasChange_VectorToList_IsCompatible()
    {
        // vector<T> and list<T> share the same wire encoding; this was previously a
        // false positive because CompareAliases used TypesEqual instead of ClassifyTypeChange.
        var oldSchema = await ParseSchema("""
            namespace Test
            using Items = vector<int32>;
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            using Items = list<int32>;
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().NotContain(c => c.Category == ChangeCategory.BreakingWire);
    }

    [Fact]
    public async Task EnumInsertion_ShiftsImplicitValues_IsBreaking()
    {
        // Classic implicit-reordering case: all constants have implicit values,
        // inserting X in the middle shifts B from 1→2 and C from 2→3.
        var oldSchema = await ParseSchema("""
            namespace Test
            enum Status { A, B, C }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            enum Status { A, X, B, C }
        """);

        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        // B and C changed effective value; X collides with old B=1.
        changes.Should().Contain(c => c.Category == ChangeCategory.BreakingWire);
    }

    [Fact]
    public async Task EnumInsertion_ExplicitSurroundings_NoCollision_IsCompatible()
    {
        // Inserting a constant between explicitly-valued constants that leaves a gap
        // is safe: existing wire values are unchanged and no collision occurs.
        var oldSchema = await ParseSchema("""
            namespace Test
            enum Status { A = 0, B = 5 }
        """);
        var newSchema = await ParseSchema("""
            namespace Test
            enum Status { A = 0, X, B = 5 }
        """);

        // X gets implicit value 1 — no collision with A=0 or B=5, A and B unchanged.
        var changes = _checker.CheckCompatibility(oldSchema, newSchema);

        changes.Should().NotContain(c => c.Category == ChangeCategory.BreakingWire);
    }

    #endregion
}
