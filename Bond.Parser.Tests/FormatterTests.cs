using Bond.Parser.Formatting;
using FluentAssertions;

namespace Bond.Parser.Tests;

public class FormatterTests
{
    private static string TrimEol(string text) => text.TrimEnd('\r', '\n');

    [Fact]
    public void Format_ReindentsAndSpaces()
    {
        var input = "namespace Test struct User{0:required string id;1:optional list<string> tags;}";
        var expected = TrimEol("""
            namespace Test

            struct User {
                0: required string id;
                1: optional list<string> tags;
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_PreservesComments()
    {
        var input = TrimEol("""
            namespace Test
            // user struct
            struct User { /* fields */ 0: required string id; }
            """);

        var expected = TrimEol("""
            namespace Test

            // user struct
            struct User {
                /* fields */ 0: required string id;
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_TopLevelBlankLines()
    {
        var input = TrimEol("""
            import "a.bond";import "b.bond";namespace Test struct A{0:required int32 id;} struct B{0:required int32 id;}
            """);

        var expected = TrimEol("""
            import "a.bond";
            import "b.bond";

            namespace Test

            struct A {
                0: required int32 id;
            }

            struct B {
                0: required int32 id;
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_EmptyStructOnOneLine()
    {
        var input = TrimEol("""
            namespace Test
            struct Empty
            {}
            """);

        var expected = TrimEol("""
            namespace Test

            struct Empty {}
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_AttributesAndEmptyDerivedStruct()
    {
        var input = TrimEol("""
            namespace Test
            [StructAttribute1("one")][StructAttribute2("two")]
            struct DerivedEmpty:Foo
            {};
            """);

        var expected = TrimEol("""
            namespace Test

            [StructAttribute1("one")]
            [StructAttribute2("two")]
            struct DerivedEmpty : Foo {}
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_PreservesLiteralPrefix()
    {
        var input = "namespace Test struct Foo{0: optional wstring name = L\"hi\";}";
        var expected = TrimEol("""
            namespace Test

            struct Foo {
                0: optional wstring name = L"hi";
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_UsingsStayTogether()
    {
        var input = "namespace Test using A = B;using C = D; struct Foo{}";
        var expected = TrimEol("""
            namespace Test

            using A = B;
            using C = D;

            struct Foo {}
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_RemovesStructSemicolon()
    {
        var input = TrimEol("""
            namespace Test
            struct Foo {};

            struct Empty
            {}
            """);

        var expected = TrimEol("""
            namespace Test

            struct Foo {}

            struct Empty {}
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_RemovesEnumSemicolon()
    {
        var input = TrimEol("""
            namespace Test
            enum Color { red, green }
            ;
            """);

        var expected = TrimEol("""
            namespace Test

            enum Color {
                red,
                green,
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_EnumValuesOnNewLines()
    {
        var input = TrimEol("""
            namespace Test
            enum Consts { Zero, One, Three = 3, Four, Six = 6 }
            """);

        var expected = TrimEol("""
            namespace Test

            enum Consts {
                Zero,
                One,
                Three = 3,
                Four,
                Six = 6,
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_DoesNotStripFieldSemicolons()
    {
        var input = TrimEol("""
            namespace Test
            struct Empty {}
            struct WithField { 0: required int32 id; }
            """);

        var expected = TrimEol("""
            namespace Test

            struct Empty {}

            struct WithField {
                0: required int32 id;
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_ForwardDeclaration()
    {
        var input = "namespace Test struct Foo;";
        var expected = TrimEol("""
            namespace Test

            struct Foo;
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_Service()
    {
        var input = "namespace Test service MySvc { void DoStuff(Request req); nothing Event(EventType evt); }";
        var expected = TrimEol("""
            namespace Test

            service MySvc {
                void DoStuff(Request req);
                nothing Event(EventType evt);
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_MultipleNamespaces()
    {
        var input = "namespace cpp Test namespace csharp Test struct Foo{}";
        var expected = TrimEol("""
            namespace cpp Test
            namespace csharp Test

            struct Foo {}
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_NestedGenericTypes()
    {
        var input = "namespace Test struct Foo { 0: optional map<string,vector<nullable<int32>>> data; }";
        var expected = TrimEol("""
            namespace Test

            struct Foo {
                0: optional map<string, vector<nullable<int32>>> data;
            }
            """);

        var result = BondFormatter.Format(input, "<inline>");

        result.Success.Should().BeTrue();
        result.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_PreservesFieldAndEnumTrailingComments()
    {
        var input = """
            namespace Test
            struct Item{
            0:int32 id; // identifier
            1:string name; /* display */ /* label */
            2:int32 value /* before separator */; // after separator
            }
            enum State{
            First=1; // first
            Second /* inline */, /* second */
            Third=3 // last
            }
            """;
        var expected = TrimEol("""
            namespace Test

            struct Item {
                0: int32 id; // identifier
                1: string name; /* display */ /* label */
                2: int32 value; /* before separator */ // after separator
            }

            enum State {
                First = 1, // first
                Second, /* inline */ /* second */
                Third = 3, // last
            }
            """);
        var result = BondFormatter.Format(input, "<inline>");
        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        result.FormattedText.Should().Be(expected);
        var repeated = BondFormatter.Format(result.FormattedText!, "<inline>");
        repeated.Success.Should().BeTrue();
        repeated.FormattedText.Should().Be(expected);
    }

    [Fact]
    public void Format_PreservesOpeningClosingAndEndOfFileComments()
    {
        var input = """
            namespace Test // namespace
            struct Item { // opening
                0: int32 id; // field
                // before closing
            }; // closing
            enum State { // enum opening
                First, // enum member
                // enum closing
            }; // enum end
            // end of file
            """;
        var result = BondFormatter.Format(input, "<inline>");
        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        result.FormattedText.Should().Contain("0: int32 id; // field");
        result.FormattedText.Should().Contain("First, // enum member");
        result.FormattedText.Should().Contain("// before closing");
        result.FormattedText.Should().EndWith("// end of file");
        var repeated = BondFormatter.Format(result.FormattedText!, "<inline>");
        repeated.Success.Should().BeTrue(string.Join("; ", repeated.Errors));
        repeated.FormattedText.Should().Be(result.FormattedText);
    }

    [Theory]
    [InlineData("enum State { First }", "First,")]
    [InlineData("enum State { First; Second; }", "Second,")]
    [InlineData("enum State { First, Second, }", "Second,")]
    public void Format_AlwaysAddsFinalEnumComma(string declaration, string lastMember)
    {
        var result = BondFormatter.Format("namespace Test " + declaration, "<inline>");
        result.Success.Should().BeTrue();
        result.FormattedText.Should().Contain(lastMember + "\n}");
        BondFormatter.Format(result.FormattedText!, "<inline>").FormattedText.Should().Be(result.FormattedText);
    }

    [Theory]
    [InlineData("namespace Test struct Item { 0: int32 /* inside type */ value; }")]
    [InlineData("namespace Test struct Item { 0: int32 value; } @")]
    public void Format_RefusesToDiscardUnpreservedCommentsOrInvalidTokens(string input)
    {
        var result = BondFormatter.Format(input, "<inline>");
        result.Success.Should().BeFalse();
        result.FormattedText.Should().BeNull();
        result.Errors.Should().NotBeEmpty();
    }
}
