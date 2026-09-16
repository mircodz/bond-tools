using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Bond.Protocols;
using Bond.IO.Safe;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bond.Parser.Tests;

public sealed class CSharpGeneratorTests
{
    private static readonly Lazy<Task<(Type Generated, Type Reference)>> Models = new(() => CreateModels(includeBonded: true));
    private static readonly Lazy<Task<(Type Generated, Type Reference)>> TextModels = new(() => CreateModels(includeBonded: false));

    [Fact]
    public async Task RuntimeSchemaMatchesAnIndependentReferenceContract()
    {
        var (generated, reference) = await Models.Value;
        Assert.Equal(SchemaJson(reference), SchemaJson(generated));
    }

    [Theory]
    [InlineData("compact", 1, false)]
    [InlineData("compact", 2, false)]
    [InlineData("fast", 1, false)]
    [InlineData("simple", 1, false)]
    [InlineData("simple", 2, false)]
    [InlineData("compact", 1, true)]
    [InlineData("compact", 2, true)]
    [InlineData("fast", 1, true)]
    [InlineData("simple", 1, true)]
    [InlineData("simple", 2, true)]
    [InlineData("json", 0, false)]
    [InlineData("xml", 0, false)]
    public async Task GeneratedAndReferenceContractsReadEachOthersPayloads(string protocol, int version, bool framed)
    {
        var (generated, reference) = await (protocol is "json" or "xml" ? TextModels.Value : Models.Value);
        var generatedValue = CreateValue(generated);
        var referenceValue = CreateValue(reference);
        var expected = Write(reference, referenceValue, protocol, (ushort)version, framed);
        var actual = Write(generated, generatedValue, protocol, (ushort)version, framed);
        Assert.Equal(expected, actual);
        var fromReference = Read(generated, expected, protocol, (ushort)version, framed);
        var fromGenerated = Read(reference, actual, protocol, (ushort)version, framed);
        Assert.Equal(expected, Write(generated, fromReference, protocol, (ushort)version, framed));
        Assert.Equal(actual, Write(reference, fromGenerated, protocol, (ushort)version, framed));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task TextReadersRetainTheRuntimeLimitationForBondedFields(string protocol)
    {
        var (generated, reference) = await Models.Value;
        foreach (var type in new[] { generated, reference })
        {
            var payload = Write(type, CreateValue(type), protocol, 0, false);
            Assert.Throws<NotImplementedException>(() => Read(type, payload, protocol, 0, false));
        }
    }

    [Fact]
    public async Task NothingRemainsDistinctFromNullableAndRetainsSimpleBinaryLimitations()
    {
        var assembly = Compile(await Generate("""
            namespace Example
            struct Value {
                0: int32 absent = nothing;
                1: nullable<int32> nullable_value;
                2: wstring text = nothing;
                3: blob bytes = nothing;
            }
            """));
        var type = assembly.GetType("Example.Value", throwOnError: true)!;
        var value = Activator.CreateInstance(type)!;
        Assert.Null(type.GetProperty("absent")!.GetValue(value));
        Assert.Null(type.GetProperty("nullable_value")!.GetValue(value));
        Assert.Null(type.GetProperty("text")!.GetValue(value));
        Assert.Null(((ArraySegment<byte>)type.GetProperty("bytes")!.GetValue(value)!).Array);
        var schema = SchemaJson(type);
        Assert.Contains("\"nothing\":true", schema);
        var data = Write(type, value, "compact", 2, false);
        Assert.Equal(data, Write(type, Read(type, data, "compact", 2, false), "compact", 2, false));
        Assert.Throws<ArgumentException>(() => Write(type, value, "simple", 2, false));
    }

    [Fact]
    public async Task OrdinaryIdentifiersAreNotEscaped()
    {
        var parsed = await ParserFacade.ParseFileAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "basic_types.bond"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(parsed.Success);
        var result = CSharpGenerator.Generate(parsed.Ast!, "basic_types.bond");
        Assert.True(result.Success);
        Assert.Contains("namespace tests", result.Code!);
        Assert.Contains("class BasicTypes", result.Code!);
        Assert.Contains("public int _int32", result.Code!);
        Assert.DoesNotContain("@", result.Code!);
        Compile(result.Code!);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("var")]
    [InlineData("dynamic")]
    [InlineData("nint")]
    [InlineData("nuint")]
    public async Task RestrictedTypeNamesAreEscapedWithoutEscapingOrdinaryProperties(string name)
    {
        var code = await Generate($"namespace Example struct {name} {{ 0: int32 value; }}");
        Assert.Contains("class @" + name, code);
        Assert.Contains("public int value", code);
        Compile(code);
    }

    [Fact]
    public async Task EscapesIdentifiersAndPreservesStringDefaults()
    {
        var assembly = Compile(await Generate("""
            namespace event
            struct class {
                0: string event = "C:\\new\\test \"quoted\"\n\t";
                1: string value = "\\n";
            }
            """));
        var type = assembly.GetType("event.class", throwOnError: true)!;
        var value = Activator.CreateInstance(type)!;
        Assert.Equal("C:\\new\\test \"quoted\"\n\t", type.GetProperty("event")!.GetValue(value));
        Assert.Equal("\\n", type.GetProperty("value")!.GetValue(value));
    }

    [Fact]
    public async Task UnicodeEscapesAreDecodedOnceAndPreservedInGeneratedLiterals()
    {
        var code = await Generate("""
            namespace Example
            struct Text {
                0: string unicode = "\u0041\U0001F680\x42";
                1: string escaped = "\\u0041";
                2: wstring surrogate = "\uD800";
                3: string controls = "\0\a\b\f\v";
            }
            """);
        var type = Compile(code).GetType("Example.Text", throwOnError: true)!;
        var value = Activator.CreateInstance(type)!;
        Assert.Equal("A\U0001F680B", type.GetProperty("unicode")!.GetValue(value));
        Assert.Equal("\\u0041", type.GetProperty("escaped")!.GetValue(value));
        Assert.Equal("\uD800", type.GetProperty("surrogate")!.GetValue(value));
        Assert.Equal("\0\a\b\f\v", type.GetProperty("controls")!.GetValue(value));
    }

    [Theory]
    [InlineData(@"\u12")]
    [InlineData(@"\U00110000")]
    [InlineData(@"\x")]
    [InlineData(@"\q")]
    public async Task UnsupportedStringEscapesAreNotSilentlyReinterpreted(string escape)
    {
        var parsed = await ParserFacade.ParseStringAsync(
            $"namespace Example struct Text {{ 0: string text = \"{escape}\"; }}");
        Assert.False(parsed.Success);
        Assert.Contains(parsed.Errors, error => error.Message.Contains("escape", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("Unicode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NumericDefaultsAreCultureIndependentAndKeepSignedZero()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var code = await Generate("""
                namespace Example
                struct Numbers {
                    0: double fraction = 1.25;
                    1: float negative_zero = -0.0;
                    2: int64 minimum = -9223372036854775808;
                    3: uint64 maximum = 18446744073709551615;
                }
                """);
            var type = Compile(code).GetType("Example.Numbers", throwOnError: true)!;
            var value = Activator.CreateInstance(type)!;
            Assert.Equal(1.25, type.GetProperty("fraction")!.GetValue(value));
            Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits((float)type.GetProperty("negative_zero")!.GetValue(value)!));
            Assert.Equal(long.MinValue, type.GetProperty("minimum")!.GetValue(value));
            Assert.Equal(ulong.MaxValue, type.GetProperty("maximum")!.GetValue(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task AliasesAndNestedTypeAnnotationsCompileAndConstructRuntimeSchemas()
    {
        var code = await Generate("""
            namespace Example
            using Text = wstring;
            using Bytes = blob;
            struct Child {}
            struct Aliases {
                0: Text title;
                1: Bytes payload;
                2: nullable<Text> maybe_title;
                3: vector<nullable<int32>> numbers;
                4: list<map<Text, vector<Text>>> nested;
                5: nullable<Child> child;
                6: bonded<Child> deferred;
            }
            """);
        var type = Compile(code).GetType("Example.Aliases", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        Assert.Equal("", type.GetProperty("title")!.GetValue(instance));
        Assert.Null(type.GetProperty("child")!.GetValue(instance));
        Assert.Contains("global::Bond.Tag.wstring", code);
        Assert.NotEmpty(SchemaJson(type));
        Write(type, instance, "compact", 2, false);
    }

    [Fact]
    public async Task RecursiveContainerReferencesDoNotInitializeRecursively()
    {
        var code = await Generate("""
            namespace Example
            struct Node {
                0: vector<Node> children;
                1: nullable<Node> parent;
            }
            """);
        var type = Compile(code).GetType("Example.Node", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        Assert.Null(type.GetProperty("parent")!.GetValue(instance));
        Assert.NotNull(type.GetProperty("children")!.GetValue(instance));
        Write(type, instance, "compact", 1, false);
    }

    [Fact]
    public async Task GenerationIsDeterministicAndIndependentOfInputPath()
    {
        var parsed = await ParserFacade.ParseStringAsync("namespace Example struct Item { 9: int32 z; 1: string a; }");
        Assert.True(parsed.Success);
        var first = CSharpGenerator.Generate(parsed.Ast!, "/one/item.bond");
        var second = CSharpGenerator.Generate(parsed.Ast!, "/another/item.bond");
        Assert.Equal(first.Code, second.Code);
        Assert.DoesNotContain("\r", first.Code!);
        Assert.True(first.Code!.IndexOf("Bond.Id(1)", StringComparison.Ordinal)
            < first.Code.IndexOf("Bond.Id(9)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("enum Invalid { value__ }", "cannot be represented")]
    [InlineData("struct Same { 0: int32 Same; }", "same name")]
    [InlineData("struct Large { 0: float number = 1e100; }", "finite range")]
    public async Task UnsupportedOrUnrepresentableSchemasFailWithoutCode(string declaration, string message)
    {
        var parsed = await ParserFacade.ParseStringAsync("namespace Example\n" + declaration);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));
        var result = CSharpGenerator.Generate(parsed.Ast!, "input.bond");
        Assert.False(result.Success);
        Assert.Null(result.Code);
        Assert.Contains(result.Errors, error => error.Message.Contains(message, StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Errors, error => Assert.Equal("input.bond", error.FilePath));
    }

    [Fact]
    public async Task MissingCSharpNamespaceFailsExplicitly()
    {
        var parsed = await ParserFacade.ParseStringAsync("namespace cpp Example struct Item {}");
        Assert.True(parsed.Success);
        var result = CSharpGenerator.Generate(parsed.Ast!, "item.bond");
        Assert.False(result.Success);
        Assert.Contains("namespace", Assert.Single(result.Errors).Message);
    }

    internal static async Task<string> Generate(string schema)
    {
        var parsed = await ParserFacade.ParseStringAsync(schema);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));
        var result = CSharpGenerator.Generate(parsed.Ast!, "input.bond");
        Assert.True(result.Success, string.Join("\n", result.Errors.Select(error => error.Message)));
        return result.Code!;
    }

    internal static Assembly Compile(params string[] sources)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([
                typeof(global::Bond.SchemaAttribute).Assembly.Location,
                typeof(global::Bond.Serializer<>).Assembly.Location,
                typeof(SimpleJsonWriter).Assembly.Location,
                typeof(global::BondTools.Models.SchemaDescriptor).Assembly.Location
            ]).Distinct(StringComparer.Ordinal);
        var compilation = CSharpCompilation.Create(
            "GeneratedContracts_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return Assembly.Load(output.ToArray());
    }

    private static async Task<(Type Generated, Type Reference)> CreateModels(bool includeBonded)
    {
        var schema = ModelSchema;
        var reference = ReferenceCode;
        if (!includeBonded)
        {
            schema = schema.Replace("13: bonded<Detail> deferred;", "", StringComparison.Ordinal);
            reference = reference.Replace(
                "[Bond.Id(13)] public Bond.IBonded<Detail> deferred { get; set; } = Bond.Bonded<Detail>.Empty;",
                "", StringComparison.Ordinal);
        }
        var assembly = Compile(await Generate(schema), reference);
        return (assembly.GetType("GeneratedContracts.Product", true)!, assembly.GetType("ReferenceContracts.Product", true)!);
    }

    private static object CreateValue(Type type)
    {
        var value = Activator.CreateInstance(type)!;
        type.GetProperty("id")!.SetValue(value, 987);
        type.GetProperty("title")!.SetValue(value, "Unicode: \u03bb \ud83d\ude80");
        type.GetProperty("wide")!.SetValue(value, "Wide: \u03bb \ud83d\ude80");
        type.GetProperty("numbers")!.SetValue(value, new LinkedList<short>([-7, 0, 123]));
        type.GetProperty("tags")!.SetValue(value, new List<string> { "one", "two" });
        type.GetProperty("flags")!.SetValue(value, new HashSet<uint> { 1, 42 });
        type.GetProperty("names")!.SetValue(value, new Dictionary<string, string> { ["key"] = "value" });
        type.GetProperty("payload")!.SetValue(value, new ArraySegment<byte>([0, 127, 255]));
        type.GetProperty("optional_value")!.SetValue(value, 19);
        type.GetProperty("absent")!.SetValue(value, 101);
        return value;
    }

    internal static string SchemaJson(Type type)
    {
        var schemaType = typeof(global::Bond.Schema<>).MakeGenericType(type);
        var schema = (global::Bond.RuntimeSchema)schemaType.GetProperty("RuntimeSchema")!.GetValue(null)!;
        return Encoding.UTF8.GetString(Write(typeof(global::Bond.SchemaDef), schema.SchemaDef, "json", 0, false));
    }

    internal static byte[] Write(Type type, object value, string protocol, ushort version, bool framed)
    {
        var output = new OutputBuffer();
        switch (protocol)
        {
            case "compact":
                var compact = new CompactBinaryWriter<OutputBuffer>(output, version);
                if (framed) compact.WriteVersion();
                new global::Bond.Serializer<CompactBinaryWriter<OutputBuffer>>(type).Serialize(value, compact);
                break;
            case "fast":
                var fast = new FastBinaryWriter<OutputBuffer>(output);
                if (framed) fast.WriteVersion();
                new global::Bond.Serializer<FastBinaryWriter<OutputBuffer>>(type).Serialize(value, fast);
                break;
            case "simple":
                var simple = new SimpleBinaryWriter<OutputBuffer>(output, version);
                if (framed) simple.WriteVersion();
                new global::Bond.Serializer<SimpleBinaryWriter<OutputBuffer>>(type).Serialize(value, simple);
                break;
            case "json":
                using (var text = new StringWriter(CultureInfo.InvariantCulture))
                {
                    var json = new SimpleJsonWriter(text);
                    new global::Bond.Serializer<SimpleJsonWriter>(type).Serialize(value, json);
                    json.Flush();
                    return Encoding.UTF8.GetBytes(text.ToString());
                }
            case "xml":
                using (var text = new StringWriter(CultureInfo.InvariantCulture))
                using (var xmlWriter = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
                {
                    var xml = new SimpleXmlWriter(xmlWriter);
                    new global::Bond.Serializer<SimpleXmlWriter>(type).Serialize(value, xml);
                    xml.Flush();
                    return Encoding.UTF8.GetBytes(text.ToString());
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(protocol));
        }
        return output.Data.ToArray();
    }

    internal static object Read(Type type, byte[] bytes, string protocol, ushort version, bool framed)
    {
        var input = new InputBuffer(bytes);
        if (framed)
        {
            input.ReadUInt16();
            Assert.Equal(version, input.ReadUInt16());
        }
        return protocol switch
        {
            "compact" => new global::Bond.Deserializer<CompactBinaryReader<InputBuffer>>(type)
                .Deserialize(new CompactBinaryReader<InputBuffer>(input, version)),
            "fast" => new global::Bond.Deserializer<FastBinaryReader<InputBuffer>>(type)
                .Deserialize(new FastBinaryReader<InputBuffer>(input)),
            "simple" => new global::Bond.Deserializer<SimpleBinaryReader<InputBuffer>>(type)
                .Deserialize(new SimpleBinaryReader<InputBuffer>(input, version)),
            "json" => new global::Bond.Deserializer<SimpleJsonReader>(type)
                .Deserialize(new SimpleJsonReader(new StringReader(Encoding.UTF8.GetString(bytes)))),
            "xml" => new global::Bond.Deserializer<SimpleXmlReader>(type)
                .Deserialize(new SimpleXmlReader(XmlReader.Create(new StringReader(Encoding.UTF8.GetString(bytes))))),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
    }

    private const string ModelSchema = """
        namespace Contracts
        namespace csharp GeneratedContracts
        enum State { Zero, High = 7 }
        struct Detail { 0: int64 count = 42; }
        [entity.kind("inventory")]
        struct Product {
            0: required int32 id = 23;
            1: string title = "hello";
            2: wstring wide = L"wide";
            3: uint64 number = 18446744073709551615;
            4: double ratio = -0.0;
            5: list<int16> numbers;
            6: vector<string> tags;
            7: set<uint32> flags;
            8: map<string, wstring> names;
            9: blob payload;
            10: nullable<int32> optional_value;
            11: State state = High;
            12: Detail detail;
            13: bonded<Detail> deferred;
            14: required_optional bool active = true;
            [JsonName("renamed")] 15: string original = "text";
            16: int32 absent = nothing;
        }
        """;

    private const string ReferenceCode = """
        namespace ReferenceContracts
        {
            [Bond.Namespace("Contracts")]
            public enum State { Zero, High = 7 }
            [Bond.Schema, Bond.Namespace("Contracts")]
            public class Detail
            {
                [Bond.Id(0)] public long count { get; set; } = 42;
            }
            [Bond.Schema, Bond.Namespace("Contracts"), Bond.Attribute("entity.kind", "inventory")]
            public class Product
            {
                [Bond.Id(0), Bond.Required] public int id { get; set; } = 23;
                [Bond.Id(1)] public string title { get; set; } = "hello";
                [Bond.Id(2), Bond.Type(typeof(Bond.Tag.wstring))] public string wide { get; set; } = "wide";
                [Bond.Id(3)] public ulong number { get; set; } = ulong.MaxValue;
                [Bond.Id(4)] public double ratio { get; set; } = -0.0;
                [Bond.Id(5)] public System.Collections.Generic.LinkedList<short> numbers { get; set; } = new();
                [Bond.Id(6)] public System.Collections.Generic.List<string> tags { get; set; } = new();
                [Bond.Id(7)] public System.Collections.Generic.HashSet<uint> flags { get; set; } = new();
                [Bond.Id(8), Bond.Type(typeof(System.Collections.Generic.Dictionary<string, Bond.Tag.wstring>))]
                public System.Collections.Generic.Dictionary<string, string> names { get; set; } = new();
                [Bond.Id(9)] public System.ArraySegment<byte> payload { get; set; } = new();
                [Bond.Id(10), Bond.Type(typeof(Bond.Tag.nullable<int>))] public int? optional_value { get; set; }
                [Bond.Id(11)] public State state { get; set; } = State.High;
                [Bond.Id(12)] public Detail detail { get; set; } = new();
                [Bond.Id(13)] public Bond.IBonded<Detail> deferred { get; set; } = Bond.Bonded<Detail>.Empty;
                [Bond.Id(14), Bond.RequiredOptional] public bool active { get; set; } = true;
                [Bond.Id(15), Bond.Attribute("JsonName", "renamed")] public string original { get; set; } = "text";
                [Bond.Id(16)] public int? absent { get; set; }
            }
        }
        """;
}
