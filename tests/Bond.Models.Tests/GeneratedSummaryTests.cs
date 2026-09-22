using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Bond.TestSupport;
using BondTools.Models;

namespace Bond.Models.Tests;

public sealed class GeneratedSummaryTests
{
    [Fact]
    public async Task FormatsCompactNamedValuesWithoutEnablingOtherFeatures()
    {
        var assembly = await Compile("""
            namespace Example
            enum State { Draft, Submitted }
            struct Order {
                0: int32 id;
                1: State status = Draft;
            }
            """);
        var value = New(assembly, "Example.Order");
        Set(value, "id", 42);
        Set(value, "status", Enum.Parse(assembly.GetType("Example.State", true)!, "Submitted"));

        Assert.Equal("Order { id = 42, status = Submitted }", value.ToString());
        Assert.IsAssignableFrom<IGeneratedSummary>(value);
        Assert.False(value is IGeneratedCloneable or IGeneratedEquatable or IGeneratedSchemaProvider or IGeneratedDebugView);
        Assert.Equal(typeof(object), value.GetType().GetMethod("Equals", [typeof(object)])!.DeclaringType);
        Assert.Null(assembly.GetType("Example.OrderSchema"));
    }

    [Fact]
    public async Task FormatsNestedCollectionsNullsStringsAndBlobs()
    {
        var assembly = await Compile("""
            namespace Example
            struct Child { 0: string name; }
            struct Value {
                0: Child child;
                1: vector<int32> numbers;
                2: map<string, int32> scores;
                3: nullable<Child> missing;
                4: blob bytes;
                5: string text;
            }
            """);
        var value = New(assembly, "Example.Value");
        var child = value.GetType().GetProperty("child")!.GetValue(value)!;
        Set(child, "name", "Ada");
        ((IList)value.GetType().GetProperty("numbers")!.GetValue(value)!).Add(7);
        ((IDictionary)value.GetType().GetProperty("scores")!.GetValue(value)!).Add("a", 9);
        Set(value, "bytes", new ArraySegment<byte>([1, 2, 255]));
        Set(value, "text", "line\n\"quoted\"\\tail");

        var text = value.ToString()!;
        Assert.Contains("child = Child { name = \"Ada\" }", text);
        Assert.Contains("numbers = [7]", text);
        Assert.Contains("\"a\"", text);
        Assert.Contains("9", text);
        Assert.Contains("missing = null", text);
        Assert.Contains("\\n", text);
        Assert.Contains("\\\"quoted\\\"", text);
        Assert.DoesNotContain("\n", text);
        Assert.Contains("bytes =", text);
    }

    [Fact]
    public async Task SummariesAreCycleSafeBoundedAndDoNotMistakeSharingForCycles()
    {
        var assembly = await Compile("""
            namespace Example
            struct Node {
                0: string text;
                1: nullable<Node> next;
                2: vector<Node> children;
            }
            """);
        var root = New(assembly, "Example.Node");
        Set(root, "next", root);
        Assert.Contains("cycle", root.ToString()!, StringComparison.OrdinalIgnoreCase);

        Set(root, "next", null);
        var child = New(assembly, "Example.Node");
        Set(child, "text", "shared");
        var children = (IList)root.GetType().GetProperty("children")!.GetValue(root)!;
        children.Add(child);
        children.Add(child);
        Assert.DoesNotContain("cycle", root.ToString()!, StringComparison.OrdinalIgnoreCase);

        for (var index = 0; index < 100; index++)
        {
            children.Add(child);
        }

        Set(root, "text", new string('x', 10000));
        var summary = root.ToString()!;
        Assert.True(summary.Length <= 2048);
        Assert.Contains("...", summary);
        Assert.DoesNotContain(new string('x', 161), summary);
        Assert.True(summary.Split("shared", StringSplitOptions.None).Length <= 17);

        var current = root;
        for (var depth = 0; depth < 20; depth++)
        {
            var next = New(assembly, "Example.Node");
            Set(current, "next", next);
            current = next;
        }

        Assert.True(root.ToString()!.Length <= 2048);
    }

    [Fact]
    public async Task LazyPayloadsAndUnknownClrToStringMethodsAreNeverInvoked()
    {
        var assembly = await Compile("""
            namespace Example
            struct Child {}
            struct Holder {
                0: bonded<Child> first;
                1: vector<bonded<Child>> values;
            }
            struct Box<T> { 0: T value; }
            """, """
            public sealed class CountingBonded : Bond.IBonded<Example.Child>
            {
                public static int Calls;
                public Example.Child Deserialize() { Calls++; throw new System.Exception("materialized"); }
                public T Deserialize<T>() { Calls++; throw new System.Exception("materialized"); }
                public void Serialize<T>(T writer) { Calls++; throw new System.Exception("serialized"); }
                public Bond.IBonded<T> Convert<T>() { Calls++; throw new System.Exception("converted"); }
                public override string ToString() { Calls++; throw new System.Exception("formatted"); }
            }
            public sealed class External
            {
                public static int Calls;
                public override string ToString() { Calls++; throw new System.Exception("formatted"); }
                public override int GetHashCode() { Calls++; throw new System.Exception("hashed"); }
            }
            """);
        var value = New(assembly, "Example.Holder");
        var bonded = New(assembly, "CountingBonded");
        Set(value, "first", bonded);
        ((IList)value.GetType().GetProperty("values")!.GetValue(value)!).Add(bonded);

        Assert.NotEmpty(value.ToString()!);
        Assert.Equal(0, bonded.GetType().GetField("Calls")!.GetValue(null));

        var external = New(assembly, "External");
        var box = Activator.CreateInstance(assembly.GetType("Example.Box`1", true)!.MakeGenericType(external.GetType()))!;
        Set(box, "value", external);
        Assert.Contains("External", box.ToString()!);
        Assert.Equal(0, external.GetType().GetField("Calls")!.GetValue(null));
    }

    [Fact]
    public async Task HandlesInheritedFieldsAndToStringNameCollisions()
    {
        var assembly = await Compile("""
            namespace Example
            struct Base { 0: int32 id; }
            struct Derived : Base { 0: string id; }
            struct Awkward { 0: int32 ToString; 1: int32 SummaryName; 2: int32 GetSummaryFields; }
            struct ToString { 0: int32 id; }
            struct Generic<ToString> { 0: ToString value; }
            """);
        var derived = New(assembly, "Example.Derived");
        var baseType = assembly.GetType("Example.Base", true)!;
        baseType.GetProperty("id")!.SetValue(derived, 7);
        derived.GetType().GetProperty("id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
            .SetValue(derived, "derived");
        var summary = derived.ToString()!;
        Assert.Contains("Base.id = 7", summary);
        Assert.Contains("Derived.id = \"derived\"", summary);

        var awkward = New(assembly, "Example.Awkward");
        Set(awkward, "ToString", 42);
        Assert.Contains("ToString = 42", ModelSummary.Format(awkward));
        var companion = assembly.GetType("Example.AwkwardOperations", true)!;
        Assert.Equal(ModelSummary.Format(awkward), companion.GetMethod("ToString", [awkward.GetType()])!
            .Invoke(null, [awkward]));
        Assert.Contains("ToString { id = 0 }", ModelSummary.Format(New(assembly, "Example.ToString")));

        var generic = Activator.CreateInstance(assembly.GetType("Example.Generic`1", true)!.MakeGenericType(typeof(int)))!;
        Set(generic, "value", 8);
        Assert.Contains("value = 8", ModelSummary.Format(generic));
    }

    [Fact]
    public async Task NumericOutputDoesNotDependOnCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var assembly = await Compile("namespace Example struct Numbers { 0: double value = 1.25; }");
            var value = New(assembly, "Example.Numbers");
            Assert.Equal("Numbers { value = 1.25 }", value.ToString());

            Set(value, "value", double.NaN);
            Assert.Equal("Numbers { value = NaN }", value.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EnumerationAndOutputHaveHardLimits()
    {
        var values = new InfiniteValues();
        var summary = ModelSummary.Format(values);
        Assert.Equal(16, values.Moves);
        Assert.True(values.Disposed);
        Assert.Contains("...", summary);

        var text = ModelSummary.Format(Enumerable.Repeat(new string('x', 160), 100).ToArray());
        Assert.True(text.Length <= 2048);
        Assert.EndsWith("...", text);
    }

    private sealed class InfiniteValues : IEnumerable, IEnumerator, IDisposable
    {
        public int Moves { get; private set; }
        public bool Disposed { get; private set; }

        public object Current => Moves;

        public IEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            Moves++;
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private static async Task<Assembly> Compile(string schema, string extra = "")
    {
        var parsed = await ParserFacade.ParseStringAsync(schema);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));

        var result = CSharpGenerator.Generate(parsed.Ast!, "summary.bond",
            new CSharpGenerationOptions { ModelFeatures = CSharpModelFeatures.StringRepresentation });
        Assert.True(result.Success, string.Join("\n", result.Errors.Select(error => error.Message)));
        Assert.DoesNotContain("IModelAdapter", result.Code!);
        Assert.DoesNotContain("SchemaDescriptor", result.Code!);

        return GeneratedCode.Compile(result.Code!, extra);
    }

    private static object New(Assembly assembly, string name) =>
        Activator.CreateInstance(assembly.GetType(name, true)!)!;

    private static void Set(object value, string property, object? fieldValue) =>
        value.GetType().GetProperty(property)!.SetValue(value, fieldValue);
}
