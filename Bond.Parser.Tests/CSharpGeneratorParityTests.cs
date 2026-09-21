using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Bond.Parser.Syntax;
using static Bond.Parser.Tests.CSharpGeneratorTests;

namespace Bond.Parser.Tests;

public sealed class CSharpGeneratorParityTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Theory]
    [InlineData("aliases")]
    [InlineData("attributes")]
    [InlineData("basic_types")]
    [InlineData("bond_meta")]
    [InlineData("complex_types")]
    [InlineData("defaults")]
    [InlineData("empty")]
    [InlineData("field_modifiers")]
    [InlineData("generics")]
    [InlineData("import")]
    [InlineData("inheritance")]
    public async Task MatchesPinnedGbcModelsAndRuntimeSchemas(string fixture)
    {
        var (generatedAssembly, referenceAssembly) = await CompileFixture(fixture);
        var referenceTypes = referenceAssembly.GetTypes().OrderBy(type => type.FullName).ToArray();
        Assert.Equal(referenceTypes.Select(type => type.FullName),
            generatedAssembly.GetTypes()
                .Where(type => type.IsEnum || type.GetCustomAttribute<global::Bond.SchemaAttribute>() != null)
                .OrderBy(type => type.FullName).Select(type => type.FullName));

        foreach (var reference in referenceTypes)
        {
            var generated = generatedAssembly.GetType(reference.FullName!, throwOnError: true)!;
            if (reference.IsEnum)
            {
                Assert.Equal(Enum.GetNames(reference), Enum.GetNames(generated));
                Assert.Equal(Enum.GetValues(reference).Cast<object>().Select(Convert.ToInt32),
                    Enum.GetValues(generated).Cast<object>().Select(Convert.ToInt32));
                continue;
            }

            Assert.Equal(reference.BaseType?.ToString(), generated.BaseType?.ToString());
            Assert.Equal(PropertySignatures(reference), PropertySignatures(generated));
            Assert.Equal(ConstructorSignatures(reference), ConstructorSignatures(generated));
            Assert.Equal(reference.GetGenericArguments().Select(type => type.GenericParameterAttributes),
                generated.GetGenericArguments().Select(type => type.GenericParameterAttributes));
            if (reference.GetCustomAttribute<global::Bond.SchemaAttribute>() == null)
            {
                continue;
            }

            foreach (var useValueTypes in new[] { false, true })
            {
                var referenceType = CloseGenericType(reference, useValueTypes);
                var generatedType = CloseGenericType(generated, useValueTypes);
                Assert.Equal(SchemaJson(referenceType), SchemaJson(generatedType));
                CompareProtocolBehavior(generatedType, referenceType);
                if (!reference.IsGenericTypeDefinition)
                {
                    break;
                }
            }
        }
    }

    [Fact]
    public async Task MetadataNamesPropagateThroughImportedGenericBaseClasses()
    {
        const string baseSchema = """
            namespace original
            namespace csharp Generated
            struct Base<T> {
                0: bond_meta::name name;
                1: bond_meta::full_name full_name;
                2: T payload;
            }
            """;
        const string derivedSchema = """
            import "base.bond"
            namespace original
            namespace csharp Generated
            struct Derived : Base<int32> { 0: string title = "derived"; }
            struct Leaf : Derived { 0: int32 id; }
            """;
        var parsed = await ParserFacade.ParseContentAsync(derivedSchema, "/schemas/derived.bond",
            (_, _) => Task.FromResult(("/schemas/base.bond", baseSchema)));
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));
        var derived = CSharpGenerator.Generate(parsed.Ast!, "derived.bond");
        Assert.True(derived.Success, string.Join("\n", derived.Errors.Select(error => error.Message)));
        Assert.DoesNotContain("class Base", derived.Code!);
        var assembly = Compile(await Generate(baseSchema), derived.Code!);
        foreach (var name in new[] { "Derived", "Leaf" })
        {
            var type = assembly.GetType("Generated." + name, true)!;
            var value = Activator.CreateInstance(type)!;
            Assert.Equal(name, type.GetProperty("name")!.GetValue(value));
            Assert.Equal("original." + name, type.GetProperty("full_name")!.GetValue(value));
            Assert.Equal(0, type.GetProperty("payload")!.GetValue(value));
            Assert.Equal("derived", type.GetProperty("title")!.GetValue(value));
            Assert.NotEmpty(SchemaJson(type));
        }
    }

    [Fact]
    public async Task ViewsKeepSelectedFieldOrdinalsAttributesAndDefaults()
    {
        var code = await Generate("""
            namespace Example
            struct Original {
                0: int32 first = 3;
                [JsonName("label")] 9: required string second = "nine";
            }
            struct Small view_of Original { second }
            """);
        var type = Compile(code).GetType("Example.Small", true)!;
        var property = Assert.Single(type.GetProperties());
        Assert.Equal("second", property.Name);
        var value = Activator.CreateInstance(type)!;
        Assert.Equal("nine", property.GetValue(value));
        var schema = SchemaJson(type);
        Assert.Contains("\"id\":9", schema);
        Assert.Contains("label", schema);
        Assert.Contains("nine", schema);
    }

    [Fact]
    public async Task GenericAliasesAreSubstitutedInConstraintsAndDefaults()
    {
        var code = await Generate("""
            namespace Example
            using Wrapper<T> = T;
            using Maybe<T : value> = nullable<T>;
            using Array<N, T> = vector<T>;
            enum State { Idle = 3 }
            struct Generic<T : value> {
                0: Maybe<T> maybe_value;
                1: Array<10, T> values;
                2: Wrapper<State> state = Idle;
                3: Wrapper<int64> count = 17;
                4: Wrapper<int32> absent = nothing;
            }
            """);
        var type = Compile(code).GetType("Example.Generic`1", true)!.MakeGenericType(typeof(int));
        var value = Activator.CreateInstance(type)!;
        Assert.Null(type.GetProperty("maybe_value")!.GetValue(value));
        Assert.Null(type.GetProperty("absent")!.GetValue(value));
        Assert.Equal(17L, type.GetProperty("count")!.GetValue(value));
        Assert.Equal(3, Convert.ToInt32(type.GetProperty("state")!.GetValue(value)));
        Assert.IsType<List<int>>(type.GetProperty("values")!.GetValue(value));
        Assert.NotEmpty(SchemaJson(type));
    }

    [Fact]
    public async Task DefaultAliasMappingUsesTheUnderlyingBondType()
    {
        var schema = await File.ReadAllTextAsync(Path.Combine(Fixtures, "nullable_alias.bond"),
            TestContext.Current.CancellationToken);
        var type = Compile(await Generate(schema)).GetType("test.foo", true)!;
        Assert.Equal(typeof(long?), type.GetProperty("l")!.PropertyType);
        Assert.Equal(typeof(long?), type.GetProperty("t")!.PropertyType);
        Assert.Null(type.GetProperty("t")!.GetValue(Activator.CreateInstance(type)));
        Assert.NotEmpty(SchemaJson(type));
    }

    [Fact]
    public async Task SignedArgumentsUppercaseLiteralsAndUnicodeIdentifiersCompile()
    {
        var schema = """
            namespace \u0394
            using Array<N, T> = vector<T>;
            enum Kind { First = +0X2A, Second = -0O12 }
            struct \u03a4 {
                0: Array<-0X10, int32> negative;
                1: Array<+0O10, int32> positive;
                2: Kind kind = First;
            }
            """.Replace("\\u0394", "\u0394", StringComparison.Ordinal)
                .Replace("\\u03a4", "\u03a4", StringComparison.Ordinal);
        var parsed = await ParserFacade.ParseStringAsync(schema);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));
        var structure = Assert.Single(parsed.Ast!.Declarations.OfType<StructDeclaration>());
        var negative = Assert.IsType<BondType.TypeReference>(structure.Fields[0].Type);
        var positive = Assert.IsType<BondType.TypeReference>(structure.Fields[1].Type);
        Assert.Equal(-16, Assert.IsType<BondType.IntTypeArg>(negative.TypeArguments[0]).Value);
        Assert.Equal(8, Assert.IsType<BondType.IntTypeArg>(positive.TypeArguments[0]).Value);
        var code = await Generate(schema);
        var assembly = Compile(code);
        var type = assembly.GetType("\u0394.\u03a4", true)!;
        var value = Activator.CreateInstance(type)!;
        Assert.IsType<List<int>>(type.GetProperty("negative")!.GetValue(value));
        Assert.IsType<List<int>>(type.GetProperty("positive")!.GetValue(value));
        Assert.Equal(42, Convert.ToInt32(type.GetProperty("kind")!.GetValue(value)));
        Assert.NotEmpty(SchemaJson(type));
    }

    [Fact]
    public async Task ServicesAreNonEmittingAndDoNotPreventModelGeneration()
    {
        var code = await Generate("""
            namespace Example
            struct Request { 0: int32 id; }
            struct Response { 0: string value; }
            service Api { Response Call(Request); }
            """);
        var assembly = Compile(code);
        Assert.NotNull(assembly.GetType("Example.Request"));
        Assert.NotNull(assembly.GetType("Example.Response"));
        Assert.Null(assembly.GetType("Example.Api"));
    }

    [Theory]
    [InlineData("struct Node { 0: Node next; }")]
    [InlineData("struct Left { 0: Right next; } struct Right { 0: Left next; }")]
    public async Task RecursiveDeclarationsGenerateLikeGbcWithoutInstantiatingInfiniteDefaults(string declarations)
    {
        Compile(await Generate("namespace Example " + declarations));
    }

    private static Type CloseGenericType(Type type, bool useValueTypes)
    {
        if (!type.IsGenericTypeDefinition)
        {
            return type;
        }

        var arguments = type.GetGenericArguments().Select(parameter =>
        {
            var useValueType = useValueTypes || parameter.GenericParameterAttributes.HasFlag(
                GenericParameterAttributes.NotNullableValueTypeConstraint);
            return useValueType ? typeof(int) : typeof(string);
        }).ToArray();
        return type.MakeGenericType(arguments);
    }

    private static IEnumerable<string> PropertySignatures(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => $"{property.Name}:{property.PropertyType}:{property.CanRead}:{property.CanWrite}")
            .OrderBy(signature => signature, StringComparer.Ordinal);

    private static IEnumerable<string> ConstructorSignatures(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(constructor =>
            {
                var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType.ToString());
                return $"{constructor.IsPublic}:{constructor.IsFamily}:{string.Join(",", parameters)}";
            })
            .OrderBy(signature => signature, StringComparer.Ordinal);

    private static void CompareProtocolBehavior(Type generated, Type reference)
    {
        var generatedValue = Activator.CreateInstance(generated)!;
        var referenceValue = Activator.CreateInstance(reference)!;
        foreach (var (protocol, version) in new (string, ushort)[]
        {
            ("compact", 1), ("compact", 2), ("fast", 1), ("simple", 1), ("simple", 2), ("json", 0), ("xml", 0)
        })
        {
            byte[]? expected = null;
            byte[]? actual = null;
            var referenceError = Record.Exception(() => expected = Write(reference, referenceValue, protocol, version, false));
            var generatedError = Record.Exception(() => actual = Write(generated, generatedValue, protocol, version, false));
            Assert.Equal(referenceError?.GetType(), generatedError?.GetType());
            if (referenceError != null)
            {
                Assert.IsType<ArgumentException>(referenceError);
                continue;
            }

            Assert.Equal(expected, actual);

            object? referenceRead = null;
            object? generatedRead = null;
            referenceError = Record.Exception(() => referenceRead = Read(reference, actual!, protocol, version, false));
            generatedError = Record.Exception(() => generatedRead = Read(generated, expected!, protocol, version, false));
            Assert.Equal(referenceError?.GetType(), generatedError?.GetType());
            if (referenceError != null)
            {
                Assert.IsType<NotImplementedException>(referenceError);
                continue;
            }

            Assert.Equal(expected, Write(generated, generatedRead!, protocol, version, false));
            Assert.Equal(actual, Write(reference, referenceRead!, protocol, version, false));
        }
    }

    private static async Task<(Assembly Generated, Assembly Reference)> CompileFixture(string fixture)
    {
        ImportResolver resolver = async (currentFile, importPath) =>
        {
            var relative = importPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentFile)!, relative));
            var path = File.Exists(local) ? local : Path.Combine(Fixtures, "imports", relative);
            return (path, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        };

        var parsed = await ParserFacade.ParseFileAsync(Path.Combine(Fixtures, fixture + ".bond"), resolver,
            TestContext.Current.CancellationToken);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));

        var result = CSharpGenerator.Generate(parsed.Ast!, fixture + ".bond");
        Assert.True(result.Success, string.Join("\n", result.Errors.Select(error => error.Message)));

        var reference = await File.ReadAllTextAsync(
            Path.Combine(Fixtures, "CodegenReference", fixture + ".cs.txt"), TestContext.Current.CancellationToken);
        var supplementary = fixture switch
        {
            "complex_types" => "namespace tests { [Bond.Schema] public partial class Bar {} }",
            "import" => "namespace empty { [Bond.Schema] public partial class Empty {} }",
            _ => ""
        };
        return (Compile(result.Code!, supplementary), Compile(reference, supplementary));
    }
}
