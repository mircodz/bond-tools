using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Bond.Parser.Syntax;
using static Bond.TestSupport.GeneratedCode;

namespace Bond.Parser.Tests;

public sealed class CSharpStructTests
{
    private const string Schema = """
        namespace Models
        struct Point { 0: double x; 1: double y; }
        struct Address { 0: string city = "Rome"; 1: vector<string> lines; }
        struct Shape {
            0: string name;
            1: vector<Point> points;
            2: Address home;
            3: map<string, Point> named;
            4: list<Address> previous;
        }
        struct Page<T> { 0: T first; 1: vector<T> items; }
        struct Extras { 0: nullable<Point> center; 1: bonded<Point> raw; 2: Page<Address> page; }
        """;

    private const string Upstream = """
        public static class Upstream
        {
            public static byte[] Write<T>(T value)
            {
                var output = new Bond.IO.Safe.OutputBuffer();
                Bond.Serialize.To(new Bond.Protocols.CompactBinaryWriter<Bond.IO.Safe.OutputBuffer>(output), value);
                return output.Data.ToArray();
            }

            public static T Read<T>(byte[] bytes) => Bond.Deserialize<T>.From(
                new Bond.Protocols.CompactBinaryReader<Bond.IO.Safe.InputBuffer>(new Bond.IO.Safe.InputBuffer(bytes)));
        }
        """;

    private static readonly CSharpGenerationOptions Options = new()
    {
        ModelFeatures = CSharpModelFeatures.All,
        Serialization = true,
        Structs = true
    };

    [Fact]
    public async Task StructsWriteTheBytesUpstreamWritesAndReadThemBack()
    {
        var code = await Generate(Schema, Options);
        Assert.Contains("public partial struct Shape", code);
        Assert.Contains("public global::Models.Address home;", code);
        RunScenario(code, """
            Require(typeof(Models.Shape).IsValueType && typeof(Models.Page<Models.Point>).IsValueType, "not structs");

            var shape = new Models.Shape { name = "triangle" };
            shape.points.Add(new Models.Point { x = 1, y = 2 });
            shape.points.Add(new Models.Point { x = 3, y = 4 });
            shape.home.city = "Turin";
            shape.home.lines.Add("1 Main St");
            shape.named["origin"] = new Models.Point();
            shape.previous.AddLast(new Models.Address { city = "Paris" });

            byte[] bytes = shape.Serialize();
            Require(bytes.SequenceEqual(Upstream.Write(shape)), "bytes differ from upstream");
            Models.Shape read = Models.Shape.Deserialize(bytes);
            Require(read.Serialize().SequenceEqual(bytes), "read differs");
            Require(Upstream.Read<Models.Shape>(bytes).Serialize().SequenceEqual(bytes), "upstream read differs");
            Require(read.home.city == "Turin" && read.points[1].y == 4, "values");
            Require(read.ToString().StartsWith("Shape { name = \"triangle\""), "ToString");

            var empty = new Models.Shape();
            Require(empty.Serialize().SequenceEqual(Upstream.Write(empty)), "defaults differ from upstream");
            Require(Models.Shape.Deserialize(empty.Serialize()).Equals(empty) && !empty.Equals(read), "Equals");
            Require(Models.Shape.Deserialize(empty.Serialize()).GetHashCode() == empty.GetHashCode(), "GetHashCode");

            // A type-parameter field holds default(T) until assigned, as upstream's does; reads give it a new value.
            var page = new Models.Page<Models.Address> { first = new Models.Address { city = "Oslo" } };
            page.items.Add(new Models.Address());
            var codec = BondTools.Runtime.BondCodec.Struct<Models.Address>();
            byte[] pageBytes = page.Serialize(codec);
            Require(pageBytes.SequenceEqual(Upstream.Write(page)), "generic bytes differ from upstream");
            var readPage = Models.Page<Models.Address>.Deserialize(pageBytes, codec);
            Require(readPage.first.city == "Oslo" && readPage.items[0].city == "Rome", "generic values");

            // Upstream can't write a nullable C# struct, so these are only read back.
            var extras = new Models.Extras { center = new Models.Point { x = 0.5 } };
            extras.raw = new Bond.Bonded<Models.Point>(new Models.Point { y = 9 });
            extras.page.first = new Models.Address { city = "Lima" };
            Models.Extras readExtras = Models.Extras.Deserialize(extras.Serialize());
            Require(readExtras.Equals(extras) && readExtras.center.Value.x == 0.5 && readExtras.raw.Deserialize().y == 9,
                "nullable, bonded or generic field");
            Require(Models.Extras.Deserialize(extras.Serialize()).Equals(extras), "round trip");
            Require(Models.Extras.Deserialize(new Models.Extras { page = extras.page }.Serialize()).center == null, "null nullable");
            var cleared = readExtras;
            cleared.Clear();
            Require(cleared.center == null && cleared.page.items.Count == 0 && readExtras.center != null, "Clear");
            """, Upstream);
    }

    [Fact]
    public async Task ReadingIntoAClearedStructReusesItsStorageWithoutAllocating()
    {
        RunScenario(await Generate(Schema, Options), """
            var polygon = new Models.Shape();
            for (int i = 0; i < 100; i++)
            {
                polygon.points.Add(new Models.Point { x = i, y = -i });
            }

            byte[] bytes = polygon.Serialize();
            var target = new Models.Shape { name = "kept" };
            target.home.lines.Add("old");
            List<Models.Point> points = target.points;
            List<string> lines = target.home.lines;

            // Structs are values: the read fills the instance's storage, and returns the instance with the fields read.
            target.Clear();
            target = Models.Shape.Deserialize(bytes, target);
            Require(target.Equals(polygon), "cleared read differs");
            Require(ReferenceEquals(target.points, points) && ReferenceEquals(target.home.lines, lines) && lines.Count == 0,
                "storage was replaced");

            // Nothing is boxed, so this holds whether or not the JIT has optimized the code yet.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                target.Clear();
                target = Models.Shape.Deserialize(bytes, target);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, "reading into a cleared struct allocated " + allocated + " bytes");
            Require(target.Equals(polygon) && ReferenceEquals(target.points, points), "reused read differs");
            """);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task EveryFixtureWithoutBasesCompilesAsStructs(string fixture)
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

        // Imported and forward-declared structs are stand-ins without serialization, so only files without any are
        // serialized.
        var external = parsed.Ast!.Imports.Length != 0 || parsed.Ast.Declarations.OfType<ForwardDeclaration>().Any();
        var options = Options with { Serialization = !external };
        var result = CSharpGenerator.Generate(parsed.Ast!, fixture, options);
        Assert.True(result.Success, string.Join("\n", result.Errors.Select(error => error.Message)));

        var standIns = """
            namespace tests { [Bond.Schema] public partial struct Bar { public void Clear() { } } }
            namespace empty { [Bond.Schema] public partial struct Empty { public void Clear() { } } }
            """;
        foreach (var type in Compile(result.Code!, standIns).GetTypes().Where(type =>
            type.IsValueType && type.GetMethod("Clear", Type.EmptyTypes) != null && !type.IsGenericTypeDefinition &&
            type.Namespace is not ("tests" or "empty")))
        {
            var value = Activator.CreateInstance(type)!;
            var other = Activator.CreateInstance(type)!;
            Assert.True(value.Equals(other), type.FullName);
            Assert.Equal(value.GetHashCode(), other.GetHashCode());
            Assert.StartsWith(type.Name, value.ToString());
            type.GetMethod("Clear", Type.EmptyTypes)!.Invoke(other, null);
            Assert.True(value.Equals(other), type.FullName + " after Clear()");
        }
    }

    // Fixtures with bases or a struct that contains itself, which C# structs can't, and complex_inheritance, which is
    // invalid.
    public static TheoryData<string> Fixtures() => new(Directory
        .GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.bond")
        .Select(path => Path.GetFileName(path))
        .Where(name => name is not ("complex_inheritance.bond" or "schemadef.bond" or "inheritance.bond" or "generics.bond"))
        .Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("struct Base { 0: int32 id; } struct Item : Base { 0: string name; }", "Item")]
    [InlineData("struct Base { 0: int32 id; } struct Item : Base {} struct View view_of Item { id; }", "Item")]
    public async Task StructsCannotInherit(string declarations, string name)
    {
        var parsed = await ParserFacade.ParseStringAsync("namespace Models " + declarations);
        Assert.True(parsed.Success);

        var result = CSharpGenerator.Generate(parsed.Ast!, "input.bond", Options);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Message == $"'{name}' has a base struct, which C# structs cannot have.");
        Assert.True(CSharpGenerator.Generate(parsed.Ast!, "input.bond", Options with { Structs = false }).Success);
    }
}
