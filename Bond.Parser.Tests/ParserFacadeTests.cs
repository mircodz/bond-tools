using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.Parser;
using Bond.Parser.Syntax;
using FluentAssertions;

namespace Bond.Parser.Tests;

public class ParserFacadeTests
{
    private async Task<ParseResult> Parse(string input, ImportResolver? importResolver = null)
    {
        return await ParserFacade.ParseStringAsync(input, importResolver);
    }

    private static Task<(string, string)> MockImportResolver(string currentFile, string importPath)
    {
        return Task.FromResult((importPath, "namespace Mock"));
    }

    #region Comments

    [Fact]
    public async Task SingleLineComments_AreParsed()
    {
        var input = """
            namespace Test
            // This is a comment
            struct User {
                0: required string id; // Field comment
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        result.Ast!.Declarations.Should().ContainSingle();
    }

    [Fact]
    public async Task MultiLineComments_AreParsed()
    {
        var input = """
            namespace Test
            /* This is a
               multi-line comment */
            struct User {
                0: required string id; /* inline */
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        result.Ast!.Declarations.Should().ContainSingle();
    }

    [Fact]
    public async Task LeadingComments_AreAttachedToDeclarationsAndFields()
    {
        var input = """
            namespace Test
            // first line
            // second line
            struct User {
                // id doc
                0: required string id;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var structDecl = (StructDeclaration)result.Ast!.Declarations[0];
        structDecl.LeadingTrivia.Select(t => t.Text)
            .Should().Equal("// first line", "// second line");
        structDecl.LeadingTrivia.Should().OnlyContain(t => t.Kind == TriviaKind.LineComment);

        structDecl.Fields[0].LeadingTrivia.Select(t => t.Text)
            .Should().Equal("// id doc");
    }

    [Fact]
    public async Task TrailingComments_AreAttachedOnSameLine()
    {
        var input = """
            namespace Test
            struct User {
                0: required string id; // trailing field
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = ((StructDeclaration)result.Ast!.Declarations[0]).Fields[0];
        field.TrailingTrivia.Should().NotBeNull();
        field.TrailingTrivia!.Text.Should().Be("// trailing field");
    }

    [Fact]
    public async Task BlockComments_AreCapturedAsBlockTrivia()
    {
        var input = """
            namespace Test
            /* a block doc */
            struct User {
                0: required string id;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var structDecl = (StructDeclaration)result.Ast!.Declarations[0];
        structDecl.LeadingTrivia.Should().ContainSingle()
            .Which.Kind.Should().Be(TriviaKind.BlockComment);
    }

    [Fact]
    public async Task NoComments_YieldsEmptyTrivia()
    {
        var input = """
            namespace Test
            struct User {
                0: required string id;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var structDecl = (StructDeclaration)result.Ast!.Declarations[0];
        structDecl.LeadingTrivia.Should().BeEmpty();
        structDecl.TrailingTrivia.Should().BeNull();
        structDecl.Fields[0].LeadingTrivia.Should().BeEmpty();
    }

    #endregion

    #region String Escapes

    [Fact]
    public async Task StringWithEscapedQuotes_IsParsed()
    {
        var input = """
            namespace Test
            struct User {
                0: required string name = "John \"Doe\"";
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.String>();
    }

    [Fact]
    public async Task StringWithBackslash_IsParsed()
    {
        var input = """
            namespace Test
            struct User {
                0: required string path = "C:\\Users\\test";
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task StringWithNewline_IsParsed()
    {
        var input = """
            namespace Test
            struct User {
                0: required string text = "line1\nline2";
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task StringWithTab_IsParsed()
    {
        var input = """
            namespace Test
            struct User {
                0: required string text = "col1\tcol2";
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    #endregion

    #region Optional Semicolons

    [Fact]
    public async Task NamespaceWithoutSemicolon_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        result.Ast!.Namespaces.Should().ContainSingle();
    }

    [Fact]
    public async Task NamespaceWithSemicolon_IsParsed()
    {
        var input = """
            namespace Test;
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        result.Ast!.Namespaces.Should().ContainSingle();
    }

    [Fact]
    public async Task StructWithoutSemicolon_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task StructWithSemicolon_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required string id; };
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task FieldWithoutSemicolon_Fails()
    {
        var input = """
            namespace Test
            struct User {
                0: required string id
                1: required string name;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task EnumConstantCommaSeparated_IsParsed()
    {
        var input = """
            namespace Test
            enum Status {
                Active = 0,
                Inactive = 1
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var enumDecl = result.Ast!.Declarations[0] as EnumDeclaration;
        enumDecl!.Constants.Should().HaveCount(2);
    }

    [Fact]
    public async Task EnumConstantSemicolonSeparated_IsParsed()
    {
        var input = """
            namespace Test
            enum Status {
                Active = 0;
                Inactive = 1;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    #endregion

    #region Field Modifiers

    [Fact]
    public async Task RequiredField_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Modifier.Should().Be(FieldModifier.Required);
    }

    [Fact]
    public async Task OptionalField_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: optional string name; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Modifier.Should().Be(FieldModifier.Optional);
    }

    [Fact]
    public async Task RequiredOptionalField_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required_optional string name; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Modifier.Should().Be(FieldModifier.RequiredOptional);
    }

    #endregion

    #region Default Values

    [Fact]
    public async Task IntegerDefault_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required int32 age = 25; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.Integer>()
            .Which.Value.Should().Be(25);
    }

    [Fact]
    public async Task NegativeIntegerDefault_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required int32 value = -100; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.Integer>()
            .Which.Value.Should().Be(-100);
    }

    [Fact]
    public async Task FloatDefault_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required float rate = 3.14; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.Float>();
    }

    [Fact]
    public async Task BooleanTrueDefault_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required bool active = true; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.Bool>()
            .Which.Value.Should().BeTrue();
    }

    [Fact]
    public async Task BooleanFalseDefault_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required bool active = false; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.Bool>()
            .Which.Value.Should().BeFalse();
    }

    [Fact]
    public async Task NothingDefault_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: optional vector<string> tags = nothing; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.Nothing>();
    }

    [Fact]
    public async Task EnumDefault_IsParsed()
    {
        var input = """
            namespace Test
            enum Status { Active = 0 }
            struct User { 0: required Status status = Active; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[1] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.Enum>()
            .Which.Identifier.Should().Be("Active");
    }

    [Fact]
    public async Task StringDefault_CapitalizedTypeName_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required String name = "foo"; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.DefaultValue.Should().BeOfType<Default.String>()
            .Which.Value.Should().Be("foo");
    }

    [Fact]
    public async Task HexLiteral_IsParsed()
    {
        var input = """
            namespace Test
            enum Values {
                HexPos = 0xFF,
                HexNeg = -0xFF
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var enumDecl = result.Ast!.Declarations[0] as EnumDeclaration;
        enumDecl!.Constants.Should().HaveCount(2);
    }

    [Fact]
    public async Task OctalLiteral_IsParsed()
    {
        var input = """
            namespace Test
            enum Values {
                OctPos = 0o123,
                OctNeg = -0o123
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var enumDecl = result.Ast!.Declarations[0] as EnumDeclaration;
        enumDecl!.Constants.Should().HaveCount(2);
    }

    #endregion

    #region Generics

    [Fact]
    public async Task GenericStruct_IsParsed()
    {
        var input = """
            namespace Test
            struct Box<T> { 0: required T value; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var structDecl = result.Ast!.Declarations[0] as StructDeclaration;
        structDecl!.TypeParameters.Should().ContainSingle()
            .Which.Name.Should().Be("T");
    }

    [Fact]
    public async Task GenericStructWithMultipleParams_IsParsed()
    {
        var input = """
            namespace Test
            struct Map<K, V> {
                0: required K key;
                1: required V value;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var structDecl = result.Ast!.Declarations[0] as StructDeclaration;
        structDecl!.TypeParameters.Should().HaveCount(2);
    }

    [Fact]
    public async Task GenericStructWithValueConstraint_IsParsed()
    {
        var input = """
            namespace Test
            struct NumericBox<T : value> { 0: required T value; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var structDecl = result.Ast!.Declarations[0] as StructDeclaration;
        structDecl!.TypeParameters.Should().ContainSingle()
            .Which.Constraint.Should().Be(TypeConstraint.Value);
    }

    #endregion

    #region Container Types

    [Fact]
    public async Task VectorType_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required vector<string> tags; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Type.Should().BeOfType<BondType.Vector>();
    }

    [Fact]
    public async Task ListType_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required list<string> tags; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Type.Should().BeOfType<BondType.List>();
    }

    [Fact]
    public async Task SetType_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required set<string> uniqueTags; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Type.Should().BeOfType<BondType.Set>();
    }

    [Fact]
    public async Task MapType_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required map<string, int32> metadata; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Type.Should().BeOfType<BondType.Map>();
    }

    [Fact]
    public async Task NullableType_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required nullable<string> middleName; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Type.Should().BeOfType<BondType.Nullable>();
    }

    [Fact]
    public async Task BondedType_IsParsed()
    {
        var input = """
            namespace Test
            struct Envelope { 0: required bonded<User> payload; }
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Type.Should().BeOfType<BondType.Bonded>();
    }

    #endregion

    #region Language-Qualified Namespaces

    [Fact]
    public async Task CppNamespace_IsParsed()
    {
        var input = """
            namespace cpp Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        result.Ast!.Namespaces.Should().ContainSingle()
            .Which.LanguageQualifier.Should().Be(Language.Cpp);
    }

    [Fact]
    public async Task CsNamespace_IsParsed()
    {
        var input = """
            namespace cs Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        result.Ast!.Namespaces.Should().ContainSingle()
            .Which.LanguageQualifier.Should().Be(Language.Cs);
    }

    [Fact]
    public async Task JavaNamespace_IsParsed()
    {
        var input = """
            namespace java Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        result.Ast!.Namespaces.Should().ContainSingle()
            .Which.LanguageQualifier.Should().Be(Language.Java);
    }

    #endregion

    #region Services

    [Fact]
    public async Task ServiceWithMethod_IsParsed()
    {
        var input = """
            namespace Test
            struct Request { 0: required string id; }
            struct Response { 0: required string result; }
            service MyService {
                Response GetData(Request);
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var serviceDecl = result.Ast!.Declarations[2] as ServiceDeclaration;
        serviceDecl!.Methods.Should().ContainSingle()
            .Which.Name.Should().Be("GetData");
    }

    [Fact]
    public async Task ServiceWithVoidMethod_IsParsed()
    {
        var input = """
            namespace Test
            service MyService {
                void Ping(void);
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ServiceWithEventMethod_IsParsed()
    {
        var input = """
            namespace Test
            struct Event { 0: required string type; }
            service MyService {
                nothing OnEvent(Event);
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var serviceDecl = result.Ast!.Declarations[1] as ServiceDeclaration;
        serviceDecl!.Methods.Should().ContainSingle()
            .Which.Should().BeOfType<EventMethod>();
    }

    [Fact]
    public async Task ServiceWithStreamingMethods_IsParsed()
    {
        var input = """
            namespace Test
            struct Request { 0: required string id; }
            struct Response { 0: required string result; }
            service MyService {
                stream Response ServerStreaming(Request);
                Response ClientStreaming(stream Request);
                stream Response DuplexStreaming(stream Request);
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var serviceDecl = result.Ast!.Declarations[2] as ServiceDeclaration;
        serviceDecl!.Methods.Should().HaveCount(3);
    }

    [Fact]
    public async Task StructNamedStream_IsParsed()
    {
        var input = """
            namespace Test
            struct stream { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var structDecl = result.Ast!.Declarations[0] as StructDeclaration;
        structDecl!.Name.Should().Be("stream");
    }

    [Fact]
    public async Task ServiceMethodWithStreamParameter_IsParsed()
    {
        var input = """
            namespace Test
            struct stream { 0: required string id; }
            service MyService {
                stream shouldBeUnary(stream);
                stream stream shouldBeStreaming(stream stream);
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var serviceDecl = result.Ast!.Declarations[1] as ServiceDeclaration;
        serviceDecl!.Methods.Should().HaveCount(2);
    }

    #endregion

    #region Special Field Names

    [Fact]
    public async Task FieldNamedValue_IsParsed()
    {
        var input = """
            namespace Test
            struct User { 0: required string value; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Name.Should().Be("value");
    }

    #endregion

    #region Imports

    [Fact]
    public async Task Import_IsParsed()
    {
        var input = """
            import "common.bond"
            namespace Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input, MockImportResolver);

        result.Success.Should().BeTrue();
        result.Ast!.Imports.Should().ContainSingle()
            .Which.FilePath.Should().Be("common.bond");
    }

    [Fact]
    public async Task MultipleImports_AreParsed()
    {
        var input = """
            import "common.bond"
            import "types.bond"
            namespace Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input, MockImportResolver);

        result.Success.Should().BeTrue();
        result.Ast!.Imports.Should().HaveCount(2);
    }

    [Fact]
    public async Task ImportWithMixedSlashes_IsParsed()
    {
        var input = """
            import "dir1/dir2\empty.bond"
            namespace Test
            struct User { 0: required string id; }
        """;

        var result = await Parse(input, MockImportResolver);

        result.Success.Should().BeTrue();
        result.Ast!.Imports.Should().ContainSingle()
            .Which.FilePath.Should().Be(@"dir1/dir2\empty.bond");
    }

    #endregion

    #region Aliases

    [Fact]
    public async Task TypeAlias_IsParsed()
    {
        var input = """
            namespace Test
            using UserID = string;
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var aliasDecl = result.Ast!.Declarations[0] as AliasDeclaration;
        aliasDecl!.Name.Should().Be("UserID");
        aliasDecl.AliasedType.Should().BeOfType<BondType.String>();
    }

    [Fact]
    public async Task GenericTypeAlias_IsParsed()
    {
        var input = """
            namespace Test
            using StringMap<T> = map<string, T>;
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var aliasDecl = result.Ast!.Declarations[0] as AliasDeclaration;
        aliasDecl!.TypeParameters.Should().ContainSingle();
    }

    #endregion

    #region Forward Declarations

    [Fact]
    public async Task ForwardDeclaration_IsParsed()
    {
        var input = """
            namespace Test
            struct User;
            struct Profile { 0: required User user; }
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    #endregion

    #region Attributes

    [Fact]
    public async Task StructWithAttribute_IsParsed()
    {
        var input = """
            namespace Test
            [Deprecated("Use NewUser instead")]
            struct User { 0: required string id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var structDecl = result.Ast!.Declarations[0] as StructDeclaration;
        structDecl!.Attributes.Should().ContainSingle()
            .Which.QualifiedName.Should().BeEquivalentTo(new[] { "Deprecated" });
    }

    [Fact]
    public async Task FieldWithAttribute_IsParsed()
    {
        var input = """
            namespace Test
            struct User {
                [JsonName("user_id")]
                0: required string id;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = (result.Ast!.Declarations[0] as StructDeclaration)!.Fields[0];
        field.Attributes.Should().ContainSingle();
    }

    #endregion

    #region Semantic validation

    [Fact]
    public async Task DuplicateFieldNames_InStruct_Fails()
    {
        var input = """
            namespace Test
            struct User {
                0: required string name;
                1: required string name;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("name"));
    }

    [Fact]
    public async Task DuplicateMethodNames_InService_Fails()
    {
        var input = """
            namespace Test
            struct Req {}
            service MySvc {
                void Get(Req);
                void Get(Req);
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("Get"));
    }

    [Fact]
    public async Task MapWithContainerKeyType_Fails()
    {
        // Grammar restricts keyType to basicType|userType, so container keys must be expressed
        // via an alias that resolves to a container type — the semantic check catches it.
        var input = """
            namespace Test
            using MyList = list<int32>;
            struct User { 0: required map<MyList, string> data; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Message.ToLower().Contains("key"));
    }

    [Fact]
    public async Task SetWithContainerKeyType_Fails()
    {
        // Grammar restricts keyType to basicType|userType, so container keys must be expressed
        // via an alias that resolves to a container type — the semantic check catches it.
        var input = """
            namespace Test
            using MyVec = vector<int32>;
            struct User { 0: required set<MyVec> tags; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Message.ToLower().Contains("key"));
    }

    [Fact]
    public async Task IntegerDefault_OutOfRangeForInt8_Fails()
    {
        var input = """
            namespace Test
            struct User { 0: required int8 level = 200; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("level"));
    }

    [Fact]
    public async Task StructField_WithNothingDefault_Fails()
    {
        var input = """
            namespace Test
            struct Inner { 0: required int32 id; }
            struct User { 0: optional Inner inner = nothing; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("inner"));
    }

    [Fact]
    public async Task AliasOfAlias_ResolvesCorrectly()
    {
        var input = """
            namespace Test
            using Inner = string;
            using Outer = Inner;
            struct User { 0: required Outer id; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
        var field = result.Ast!.Declarations.OfType<StructDeclaration>().Single().Fields.Single();
        field.Type.Should().BeOfType<BondType.TypeReference>();
    }

    [Fact]
    public async Task CircularImports_DoNotLoopInfinitely()
    {
        const string aPath = "/virtual/a.bond";
        const string bPath = "/virtual/b.bond";
        var files = new System.Collections.Generic.Dictionary<string, string>
        {
            [aPath] = """
                import "b.bond"
                namespace Test
                struct A { 0: required int32 id; }
            """,
            [bPath] = """
                import "a.bond"
                namespace Test
                struct B { 0: required int32 id; }
            """
        };

        ImportResolver resolver = (currentFile, importPath) =>
        {
            var dir = System.IO.Path.GetDirectoryName(currentFile) ?? "/virtual";
            var absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, importPath));
            return Task.FromResult((absolute, files[absolute]));
        };

        var result = await ParserFacade.ParseContentAsync(files[aPath], aPath, resolver);

        // The important invariant: it terminates and returns something
        result.Should().NotBeNull();
    }

    #endregion

    #region Issues

    [Fact]
    public async Task SetWithAlias_IsParsed()
    {
        var input = """
            namespace Test
            using guid = string;
            struct User {
                0: required map<guid, int16> properties;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Alias_IsFileScoped_WhenImportDefinesSameAlias()
    {
        var input = """
                        import "common.bond"
                        namespace Test
                        using ID = string;
                        struct User { 0: required ID id; }
                    """;

        async Task<(string, string)> Resolver(string _, string importPath)
        {
            var content = """
                              namespace Test
                              using ID = int32;
                              struct Other { 0: required ID id; }
                          """;
            return await Task.FromResult((importPath, content));
        }

        var result = await Parse(input, Resolver);

        result.Success.Should().BeTrue();
        var user = result.Ast!.Declarations.OfType<StructDeclaration>().First(d => d.Name == "User");
        var fieldType = user.Fields[0].Type as BondType.TypeReference;
        fieldType.Should().NotBeNull();
        var alias = fieldType!.Declaration.Should().BeOfType<AliasDeclaration>().Subject;
        alias.AliasedType.Should().BeOfType<BondType.String>();
    }

    #endregion

    #region Error locations

    [Fact]
    public async Task DuplicateFieldOrdinal_Error_HasSourceLocation()
    {
        var input = """
            namespace Test
            struct User {
                0: required string id;
                0: required string name;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Line.Should().BeGreaterThan(0);
        error.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UnresolvedType_Error_HasSourceLocation()
    {
        var input = """
            namespace Test
            struct User {
                0: required NoSuchType id;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Line.Should().BeGreaterThan(0);
        error.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RequiredEnumField_WithoutDefault_IsValid()
    {
        var input = """
            namespace Test
            enum Status { Active = 0 }
            struct User { 0: required Status field; }
        """;

        var result = await Parse(input);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task OptionalEnumField_WithoutDefault_FailsWithLocation()
    {
        var input = """
            namespace Test
            enum Status { Active = 0 }
            struct User {
                0: optional Status field;
            }
        """;

        var result = await Parse(input);

        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Message.Should().Contain("must have a default value");
        error.Line.Should().BeGreaterThan(0);
    }

    #endregion
}
