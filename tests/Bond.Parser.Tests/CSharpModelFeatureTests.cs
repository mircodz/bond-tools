using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using static Bond.TestSupport.GeneratedCode;

namespace Bond.Parser.Tests;

public sealed class CSharpModelFeatureTests
{
    [Fact]
    public async Task CloneCopiesMutableDataAndKeepsTheRuntimeType()
    {
        await Run("""
            namespace Models
            struct Leaf { 0: int32 value; 1: vector<int32> items; }
            struct Tree {
                0: vector<Leaf> leaves;
                1: list<string> names;
                2: set<int64> ids;
                3: map<string, vector<Leaf>> groups;
                4: nullable<Leaf> single;
                5: blob bytes;
                6: vector<blob> chunks;
                7: nullable<vector<int32>> absent;
            }
            """, """
            var leaf = new Models.Leaf { value = 1 };
            leaf.items.Add(1);
            var tree = new Models.Tree { single = leaf, bytes = new ArraySegment<byte>(new byte[] { 9, 1, 2, 9 }, 1, 2) };
            tree.leaves.Add(leaf);
            tree.names.AddLast("a");
            tree.ids.Add(7);
            tree.groups["g"] = new List<Models.Leaf> { leaf };
            tree.chunks.Add(new ArraySegment<byte>(new byte[] { 3 }));

            var copy = tree.Clone();
            Require(copy.Equals(tree), "clone differs");
            Require(!ReferenceEquals(copy.leaves, tree.leaves) && !ReferenceEquals(copy.leaves[0], leaf), "vector shared");
            Require(!ReferenceEquals(copy.leaves[0].items, leaf.items), "nested vector shared");
            Require(!ReferenceEquals(copy.names, tree.names) && !ReferenceEquals(copy.ids, tree.ids), "list or set shared");
            Require(!ReferenceEquals(copy.groups["g"], tree.groups["g"]) && !ReferenceEquals(copy.groups["g"][0], leaf), "map shared");
            Require(!ReferenceEquals(copy.single, leaf), "nullable struct shared");
            Require(copy.bytes.Array != tree.bytes.Array && copy.bytes.SequenceEqual(new byte[] { 1, 2 }), "blob shared");
            Require(copy.chunks[0].Array != tree.chunks[0].Array, "nested blob shared");
            Require(copy.absent == null, "null container was materialized");

            leaf.items.Add(2);
            Require(!copy.Equals(tree), "clone observed a mutation");
            Require(((ICloneable)new Derived()).Clone() is Derived, "runtime type lost");
            """, extra: "class Derived : Models.Leaf { }");
    }

    [Fact]
    public async Task EqualityAndHashFollowValueSemantics()
    {
        await Run("""
            namespace Models
            struct Value {
                0: double number;
                1: float single;
                2: map<string, int32> lookup;
                3: set<int32> flags;
                4: vector<int32> sequence;
                5: int32 absent = nothing;
                6: nullable<int32> maybe;
                7: blob bytes = nothing;
                8: map<string, vector<blob>> nested;
            }
            """, """
            var left = new Models.Value { number = double.NaN, single = -0.0f };
            var right = new Models.Value
            {
                number = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000001)),
                single = 0.0f
            };
            left.lookup.Add("a", 1); left.lookup.Add("b", 2);
            right.lookup.Add("b", 2); right.lookup.Add("a", 1);
            left.flags.UnionWith(new[] { 3, 1, 2 });
            right.flags.UnionWith(new[] { 2, 1, 3 });
            left.sequence.AddRange(new[] { 1, 2 });
            right.sequence.AddRange(new[] { 1, 2 });
            left.nested["x"] = new List<ArraySegment<byte>> { new ArraySegment<byte>(new byte[] { 1, 2 }) };
            right.nested["x"] = new List<ArraySegment<byte>> { new ArraySegment<byte>(new byte[] { 0, 1, 2 }, 1, 2) };
            Require(left.Equals(right) && right.Equals((object)left), "value equality ignored .NET or unordered semantics");
            Require(left.GetHashCode() == right.GetHashCode(), "equal values hashed differently");

            right.sequence.Reverse();
            Require(!left.Equals(right), "sequence order was ignored");
            right.sequence.Reverse();
            right.absent = 0;
            Require(!left.Equals(right), "nothing was confused with zero");
            right.absent = null;
            right.maybe = 0;
            Require(!left.Equals(right), "nullable absence was confused with zero");
            right.maybe = null;
            right.bytes = new ArraySegment<byte>(Array.Empty<byte>());
            Require(!left.Equals(right), "default blob was confused with an empty blob");
            right.bytes = default;
            right.lookup = null;
            Require(!left.Equals(right) && !right.Equals(left), "null map equaled a map");
            right.lookup = new Dictionary<string, int> { ["a"] = 1, ["b"] = 3 };
            Require(!left.Equals(right), "map values were ignored");
            right.lookup["b"] = 2;
            Require(left.Equals(right), "restored values differed");
            Require(!left.Equals(null) && !left.Equals(new Derived()), "model equaled null or another runtime type");

            var large = new Models.Value();
            var reversed = new Models.Value();
            for (var i = 0; i < 20000; i++) { large.lookup["k" + i] = i; reversed.lookup["k" + (19999 - i)] = 19999 - i; }
            Require(large.Equals(reversed) && large.GetHashCode() == reversed.GetHashCode(), "large maps differed");
            Require(new Derived().GetHashCode() == new Derived().GetHashCode(), "subclass hashing failed");
            """, extra: "class Derived : Models.Value { }");
    }

    [Fact]
    public async Task InheritedHiddenAndGenericFieldsParticipate()
    {
        await Run("""
            namespace Models
            struct Item { 0: int32 id; }
            struct Base<T> { 0: string id; 1: vector<T> values; }
            struct Derived : Base<Item> { 0: string id; 1: nullable<Item> item; }
            """, """
            var value = new Models.Derived { id = "derived" };
            ((Models.Base<Models.Item>)value).id = "base";
            value.values.Add(new Models.Item { id = 1 });

            var copy = value.Clone();
            Require(((Models.Base<Models.Item>)copy).id == "base" && copy.id == "derived", "hidden fields not copied");
            Require(!ReferenceEquals(copy.values[0], value.values[0]), "generic struct values were shared");
            Require(copy.Equals(value) && copy.GetHashCode() == value.GetHashCode(), "copy differs");

            ((Models.Base<Models.Item>)copy).id = "other";
            Require(!copy.Equals(value), "hidden base field was ignored");
            Require(value.ToString() == "Derived { Base.id = \"base\", values = [Item { id = 1 }], Derived.id = \"derived\", item = null }",
                value.ToString());

            value.item = new Models.Item { id = 2 };
            var viaBase = ((Models.Base<Models.Item>)value).Clone() as Models.Derived;
            Require(viaBase != null && viaBase.Equals(value) && !ReferenceEquals(viaBase.item, value.item), "base Clone() shared derived fields");
            """);
    }

    [Fact]
    public async Task BondedPayloadsAreSharedByCloneAndComparedByValue()
    {
        await Run("""
            namespace Models
            struct Payload { 0: string text; }
            struct Envelope { 0: bonded<Payload> payload; }
            struct Box<T> { 0: bonded<T> payload; 1: vector<bonded<T>> many; }
            """, """
            Require(new Models.Box<Models.Payload>().Equals(new Models.Box<Models.Payload>()), "generic bonded payloads differed");
            var envelope = new Models.Envelope { payload = new Bond.Bonded<Models.Payload>(new Models.Payload { text = "a" }) };
            var output = new Bond.IO.Safe.OutputBuffer();
            Bond.Serialize.To(new Bond.Protocols.CompactBinaryWriter<Bond.IO.Safe.OutputBuffer>(output), envelope);
            var restored = Bond.Deserialize<Models.Envelope>.From(
                new Bond.Protocols.CompactBinaryReader<Bond.IO.Safe.InputBuffer>(new Bond.IO.Safe.InputBuffer(output.Data)));

            Require(envelope.Equals(restored) && envelope.GetHashCode() == restored.GetHashCode(), "bonded payloads differed");
            Require(ReferenceEquals(envelope.Clone().payload, envelope.payload), "bonded payload was not shared");
            Require(envelope.ToString() == "Envelope { payload = bonded }", envelope.ToString());
            """);
    }

    [Fact]
    public async Task ToStringIsCompactAndCultureInvariant()
    {
        await Run("""
            namespace Models
            enum Color { Red, Green }
            struct Point { 0: double x; 1: Color color = Green; }
            struct Shape {
                0: string name;
                1: vector<Point> points;
                2: map<int32, string> labels;
                3: blob data;
                4: nullable<Point> center;
                5: set<wstring> tags;
            }
            struct Empty {}
            """, """
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("it-IT");
            try
            {
                var shape = new Models.Shape { name = "box", data = new ArraySegment<byte>(new byte[] { 1, 2, 3 }) };
                shape.points.Add(new Models.Point { x = 1.5 });
                shape.labels[1] = null;
                var text = shape.ToString();
                Require(text == "Shape { name = \"box\", points = [Point { x = 1.5, color = Green }], labels = {1 = null}, " +
                    "data = blob[3], center = null, tags = [] }", text);
                Require(new Models.Empty().ToString() == "Empty { }", new Models.Empty().ToString());
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
            """);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task EveryFixtureCompilesWithAllFeatures(string fixture)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        ImportResolver resolver = async (currentFile, importPath) =>
        {
            var relative = importPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentFile)!, relative));
            var path = File.Exists(local) ? local : Path.Combine(directory, "imports", relative);
            return (path, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        };
        var parsed = await ParserFacade.ParseFileAsync(Path.Combine(directory, fixture), resolver,
            TestContext.Current.CancellationToken);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));

        var result = CSharpGenerator.Generate(parsed.Ast!, fixture,
            new CSharpGenerationOptions { ModelFeatures = CSharpModelFeatures.All });
        Assert.True(result.Success, string.Join("\n", result.Errors.Select(error => error.Message)));

        var external = """
            namespace tests { [Bond.Schema] public partial class Bar {} }
            namespace empty { [Bond.Schema] public partial class Empty {} }
            """;
        foreach (var type in Compile(result.Code!, external).GetTypes().Where(type =>
            typeof(ICloneable).IsAssignableFrom(type) && !type.IsGenericTypeDefinition))
        {
            var value = Activator.CreateInstance(type)!;
            var copy = ((ICloneable)value).Clone();
            Assert.NotSame(value, copy);
            Assert.True(value.Equals(copy), type.FullName);
            Assert.Equal(value.GetHashCode(), copy.GetHashCode());
            Assert.StartsWith(type.Name, value.ToString());
        }
    }

    // complex_inheritance is invalid, and schemadef inherits from a type parameter, which C# cannot represent.
    public static TheoryData<string> Fixtures() => new(Directory
        .GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.bond")
        .Select(path => Path.GetFileName(path))
        .Where(name => name is not ("complex_inheritance.bond" or "schemadef.bond"))
        .Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(CSharpModelFeatures.Cloning, "Clone", "struct Base { 0: int32 Clone; } struct Item : Base {}")]
    [InlineData(CSharpModelFeatures.Equality, "Equals", "struct Base { 0: int32 Equals; } struct Item : Base {}")]
    [InlineData(CSharpModelFeatures.Equality, "GetHashCode", "struct Item { 0: int32 GetHashCode; }")]
    [InlineData(CSharpModelFeatures.StringRepresentation, "ToString", "struct Item { 0: int32 ToString; }")]
    [InlineData(CSharpModelFeatures.Cloning, "Clone", "struct Clone { 0: int32 value; }")]
    [InlineData(CSharpModelFeatures.StringRepresentation, "ToString", "struct Item<ToString> { 0: int32 value; }")]
    public async Task NamesCannotShadowGeneratedMembers(CSharpModelFeatures feature, string name, string declarations)
    {
        var parsed = await ParserFacade.ParseStringAsync("namespace Models " + declarations);
        Assert.True(parsed.Success);

        var result = CSharpGenerator.Generate(parsed.Ast!, "input.bond", new CSharpGenerationOptions { ModelFeatures = feature });
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Message.Contains($"generated {name}()", StringComparison.Ordinal));
        Assert.True(CSharpGenerator.Generate(parsed.Ast!, "input.bond").Success);
    }

    private static async Task Run(string schema, string body, string extra = "") =>
        RunScenario(await Generate(schema), body, extra);
}
