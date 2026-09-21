using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.CLI;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using BondTools.Models;
using static Bond.Parser.Tests.CSharpGeneratorTests;

namespace Bond.Parser.Tests;

public sealed class CSharpMappingFeatureTests
{
    public static IEnumerable<object[]> FeatureSelections() =>
        Enumerable.Range(0, 32).Select(value => new object[] { (CSharpModelFeatures)value });

    [Theory]
    [MemberData(nameof(FeatureSelections))]
    public async Task FeaturesAreIndependentAndPlainModelsRemainTheDefault(CSharpModelFeatures features)
    {
        var result = await GenerateResult("namespace Example struct Item { 0: int32 id; }",
            new CSharpGenerationOptions { ModelFeatures = features });
        Assert.True(result.Success, Messages(result));
        var assembly = Compile(result.Code!);
        var type = assembly.GetType("Example.Item", true)!;
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Descriptors),
            assembly.GetType("Example.ItemSchema") != null);
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Descriptors),
            typeof(IGeneratedSchemaProvider).IsAssignableFrom(type));
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Cloning),
            typeof(ICloneable).IsAssignableFrom(type));
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Equality),
            typeof(IGeneratedEquatable).IsAssignableFrom(type));
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Debugger),
            type.GetCustomAttribute<DebuggerTypeProxyAttribute>() != null);
        Assert.Equal(features.HasFlag(CSharpModelFeatures.StringRepresentation),
            typeof(IGeneratedSummary).IsAssignableFrom(type));
        Assert.Equal(features.HasFlag(CSharpModelFeatures.StringRepresentation),
            type.GetMethod("ToString", Type.EmptyTypes)!.DeclaringType == type);
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Equality),
            type.GetMethod("Equals", [typeof(object)])!.DeclaringType == type);
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Equality),
            type.GetMethod("GetHashCode", Type.EmptyTypes)!.DeclaringType == type);
        if (features == CSharpModelFeatures.None)
        {
            Assert.DoesNotContain("BondTools.Models", result.Code!);
        }

        var value = Activator.CreateInstance(type)!;
        type.GetProperty("id")!.SetValue(value, 42);
        if (features.HasFlag(CSharpModelFeatures.Cloning))
        {
            var copy = ((ICloneable)value).Clone();
            Assert.NotSame(value, copy);
            Assert.Equal(42, type.GetProperty("id")!.GetValue(copy));
        }
    }

    [Fact]
    public async Task NamespaceMappingsApplyExactlyAndKeepIdlIdentity()
    {
        var result = await GenerateResult("""
            namespace Contracts
            namespace csharp Application
            struct Item { 0: int32 id; }
            """, new CSharpGenerationOptions
        {
            ModelFeatures = CSharpModelFeatures.Descriptors,
            NamespaceMappings = ["Application=Renamed"]
        });
        Assert.True(result.Success, Messages(result));
        var assembly = Compile(result.Code!);
        var type = assembly.GetType("Renamed.Item", true)!;
        var descriptor = ((IGeneratedSchemaProvider)Activator.CreateInstance(type)!).Descriptor;
        Assert.Equal("Contracts.Item", descriptor.FullName);
        Assert.Equal("global::Renamed.Item", descriptor.ClrName);
        Assert.Contains("\"qualified_name\":\"Contracts.Item\"", SchemaJson(type));
    }

    [Theory]
    [InlineData(CSharpModelFeatures.None)]
    [InlineData(CSharpModelFeatures.All)]
    public async Task UsingNamespacesAreEmittedOnceBeforeDeclarations(CSharpModelFeatures features)
    {
        var result = await GenerateResult("namespace Example struct Item { 0: int32 id; }",
            new CSharpGenerationOptions
            {
                ModelFeatures = features,
                UsingNamespaces = ["System.Text", " System.Collections.Generic ", "System.Text", "Example.event"]
            });
        Assert.True(result.Success, Messages(result));
        Assert.NotNull(result.Code);
        Assert.Equal(new[]
        {
            "using System.Text;",
            "using System.Collections.Generic;",
            "using Example.@event;"
        }, result.Code.Split('\n').Where(line => line.StartsWith("using ", StringComparison.Ordinal)));
        Assert.True(result.Code.IndexOf("using System.Text;", StringComparison.Ordinal)
            < result.Code.IndexOf("namespace Example", StringComparison.Ordinal));
        Compile(result.Code + """

            namespace Example.@event { public sealed class Marker {} }
            namespace Consumer {
                public static class Imports {
                    public static List<StringBuilder> Values { get; } = new();
                    public static Marker Value { get; } = new();
                }
            }
            """);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".System")]
    [InlineData("System..Text")]
    [InlineData("System.Text.")]
    [InlineData("System;")]
    [InlineData("System\nclass Injected{}")]
    [InlineData("Alias=System.Text")]
    [InlineData("static System.Math")]
    public async Task InvalidUsingNamespacesFailBeforeEmittingCode(string name)
    {
        var result = await GenerateResult("namespace Example struct Item {}",
            new CSharpGenerationOptions { UsingNamespaces = [name] });
        Assert.False(result.Success);
        Assert.Null(result.Code);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task MappedDefaultsUseExplicitConvertersAndKeepTheWireSchema()
    {
        var result = await GenerateResult("""
            namespace Contracts
            using Timestamp = int64;
            struct Event {
                0: Timestamp created = 0;
                1: Timestamp updated = nothing;
            }
            """, new CSharpGenerationOptions
        {
            ModelFeatures = CSharpModelFeatures.All,
            NamespaceMappings = ["Contracts=Application"],
            TypeMappings = ["Contracts.Timestamp=System.DateTime"]
        });
        Assert.True(result.Success, Messages(result));
        var assembly = Compile(result.Code!, """
            namespace Application
            {
                public static class BondTypeAliasConverter
                {
                    public static System.DateTime Convert(long value, System.DateTime unused) =>
                        System.DateTime.UnixEpoch.AddTicks(value);
                    public static long Convert(System.DateTime value, long unused) =>
                        (value - System.DateTime.UnixEpoch).Ticks;
                }
            }
            namespace Reference
            {
                [Bond.Schema, Bond.Namespace("Contracts")]
                public class Event
                {
                    [Bond.Id(0)] public long created { get; set; } = 0;
                    [Bond.Id(1)] public long? updated { get; set; }
                }
            }
            """);
        var mapped = assembly.GetType("Application.Event", true)!;
        var reference = assembly.GetType("Reference.Event", true)!;
        Assert.Equal(SchemaJson(reference), SchemaJson(mapped));
        var value = Activator.CreateInstance(mapped)!;
        Assert.Equal(DateTime.UnixEpoch, mapped.GetProperty("created")!.GetValue(value));
        Assert.Null(mapped.GetProperty("updated")!.GetValue(value));
        Assert.Equal(Write(reference, Activator.CreateInstance(reference)!, "compact", 2, false),
            Write(mapped, value, "compact", 2, false));
        var clone = ((ICloneable)value).Clone();
        Assert.True(value.Equals(clone));
        Assert.Equal(value.GetHashCode(), clone.GetHashCode());
    }

    [Theory]
    [InlineData("int64", "42")]
    [InlineData("double", "-0.0")]
    [InlineData("bool", "true")]
    [InlineData("string", "\"non-empty\"")]
    public async Task MappedDefaultsThatTheLegacyRuntimeCannotPreserveAreRejected(string type, string value)
    {
        var result = await GenerateResult(
            $"namespace Example using Value = {type}; struct Item {{ 0: Value value = {value}; }}",
            new CSharpGenerationOptions { TypeMappings = ["Example.Value=global::External.Value"] });
        Assert.False(result.Success);
        Assert.Null(result.Code);
        Assert.Contains(result.Errors, error => error.Message.Contains("cannot preserve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenericTypeTemplatesAndLocalAliasNamesAreSupported()
    {
        var result = await GenerateResult("""
            namespace Example
            using Items<T> = vector<T>;
            struct Bag { 0: Items<int32> items; }
            """, new CSharpGenerationOptions
        {
            TypeMappings = ["Items=System.Collections.Generic.LinkedList<{0}>"]
        });
        Assert.True(result.Success, Messages(result));
        var assembly = Compile(result.Code!, """
            namespace Example
            {
                public static class BondTypeAliasConverter
                {
                    public static System.Collections.Generic.LinkedList<T> Convert<T>(
                        System.Collections.Generic.List<T> value,
                        System.Collections.Generic.LinkedList<T> unused) => new(value);
                }
            }
            """);
        var type = assembly.GetType("Example.Bag", true)!;
        Assert.IsType<LinkedList<int>>(type.GetProperty("items")!.GetValue(Activator.CreateInstance(type)));
    }

    [Fact]
    public async Task GenericTypeTemplatesPreserveEscapedTypeParameters()
    {
        var result = await GenerateResult("""
            namespace Example
            using Items<T> = vector<T>;
            struct Bag<var> { 0: Items<var> values; }
            """, new CSharpGenerationOptions
        {
            ModelFeatures = CSharpModelFeatures.All,
            TypeMappings = ["Items=System.Collections.Generic.List<{0}>"]
        });
        Assert.True(result.Success, Messages(result));
        var type = Compile(result.Code!).GetType("Example.Bag`1", true)!.MakeGenericType(typeof(int));
        var value = (ICloneable)Activator.CreateInstance(type)!;
        Assert.IsType<List<int>>(type.GetProperty("values")!.GetValue(value.Clone()));
    }

    [Theory]
    [InlineData("compact", 2)]
    [InlineData("fast", 1)]
    [InlineData("json", 0)]
    public async Task MappedBlobAliasesRetainWireTypesAndRoundTripEmptyAndNestedValues(string protocol, int version)
    {
        var result = await GenerateResult("""
            namespace Example
            using Bytes = blob;
            using Data = Bytes;
            using Chunks = vector<blob>;
            struct Item {
                0: Bytes bytes;
                1: Data alias;
                2: vector<Bytes> chunks;
                3: map<string, vector<Data>> nested;
                4: Chunks converted;
            }
            """, new CSharpGenerationOptions
        {
            TypeMappings = [
                "Example.Bytes=System.Byte[]",
                "Example.Chunks=System.Collections.Generic.List<System.Byte[]>"
            ]
        });
        Assert.True(result.Success, Messages(result));
        Assert.DoesNotContain("BondTools.Models", result.Code!);
        var assembly = Compile(result.Code!, """
            namespace Example
            {
                public static class BondTypeAliasConverter
                {
                    public static byte[] Convert(System.ArraySegment<byte> value, byte[] unused)
                    {
                        if (value.Count == 0)
                        {
                            return System.Array.Empty<byte>();
                        }

                        var bytes = new byte[value.Count];
                        System.Array.Copy(value.Array, value.Offset, bytes, 0, value.Count);
                        return bytes;
                    }

                    public static System.ArraySegment<byte> Convert(byte[] value, System.ArraySegment<byte> unused) =>
                        new(value ?? System.Array.Empty<byte>());

                    public static System.Collections.Generic.List<byte[]> Convert(
                        System.Collections.Generic.List<System.ArraySegment<byte>> value,
                        System.Collections.Generic.List<byte[]> unused) =>
                        value.ConvertAll(item => Convert(item, default(byte[])));

                    public static System.Collections.Generic.List<System.ArraySegment<byte>> Convert(
                        System.Collections.Generic.List<byte[]> value,
                        System.Collections.Generic.List<System.ArraySegment<byte>> unused) =>
                        value.ConvertAll(item => Convert(item, default(System.ArraySegment<byte>)));
                }
            }
            namespace Reference
            {
                [Bond.Schema, Bond.Namespace("Example")]
                public class Item
                {
                    [Bond.Id(0)] public System.ArraySegment<byte> bytes { get; set; }
                    [Bond.Id(1)] public System.ArraySegment<byte> alias { get; set; }
                    [Bond.Id(2)] public System.Collections.Generic.List<System.ArraySegment<byte>> chunks { get; set; } = new();
                    [Bond.Id(3)] public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.ArraySegment<byte>>> nested { get; set; } = new();
                    [Bond.Id(4)] public System.Collections.Generic.List<System.ArraySegment<byte>> converted { get; set; } = new();
                }
            }
            """);
        var mapped = assembly.GetType("Example.Item", true)!;
        var reference = assembly.GetType("Reference.Item", true)!;
        Assert.Equal(typeof(byte[]), mapped.GetProperty("bytes")!.PropertyType);
        var bytesType = mapped.GetProperty("bytes")!.CustomAttributes
            .Single(attribute => attribute.AttributeType == typeof(global::Bond.TypeAttribute));
        var convertedType = mapped.GetProperty("converted")!.CustomAttributes
            .Single(attribute => attribute.AttributeType == typeof(global::Bond.TypeAttribute));
        Assert.Equal(typeof(global::Bond.Tag.blob), bytesType.ConstructorArguments[0].Value);
        Assert.Equal(typeof(List<global::Bond.Tag.blob>), convertedType.ConstructorArguments[0].Value);
        Assert.Equal(SchemaJson(reference), SchemaJson(mapped));
        Assert.Empty(Assert.IsType<byte[]>(mapped.GetProperty("bytes")!.GetValue(Activator.CreateInstance(mapped))));

        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 0, 127, 255 } })
        {
            var value = Activator.CreateInstance(mapped)!;
            mapped.GetProperty("bytes")!.SetValue(value, bytes);
            mapped.GetProperty("alias")!.SetValue(value, bytes);
            mapped.GetProperty("chunks")!.SetValue(value, new List<byte[]> { bytes, Array.Empty<byte>() });
            mapped.GetProperty("nested")!.SetValue(value, new Dictionary<string, List<byte[]>> { ["data"] = [bytes, []] });
            mapped.GetProperty("converted")!.SetValue(value, new List<byte[]> { bytes, Array.Empty<byte>() });

            var expected = Activator.CreateInstance(reference)!;
            var segment = new ArraySegment<byte>(bytes);
            var empty = new ArraySegment<byte>(Array.Empty<byte>());
            reference.GetProperty("bytes")!.SetValue(expected, segment);
            reference.GetProperty("alias")!.SetValue(expected, segment);
            reference.GetProperty("chunks")!.SetValue(expected, new List<ArraySegment<byte>> { segment, empty });
            reference.GetProperty("nested")!.SetValue(expected,
                new Dictionary<string, List<ArraySegment<byte>>> { ["data"] = [segment, empty] });
            reference.GetProperty("converted")!.SetValue(expected, new List<ArraySegment<byte>> { segment, empty });

            var encoded = Write(mapped, value, protocol, (ushort)version, false);
            Assert.Equal(Write(reference, expected, protocol, (ushort)version, false), encoded);
            var copy = Read(mapped, encoded, protocol, (ushort)version, false);
            Assert.Equal(bytes, Assert.IsType<byte[]>(mapped.GetProperty("bytes")!.GetValue(copy)));
            Assert.Equal(bytes, Assert.IsType<byte[]>(mapped.GetProperty("alias")!.GetValue(copy)));
            var chunks = Assert.IsType<List<byte[]>>(mapped.GetProperty("chunks")!.GetValue(copy));
            Assert.Equal(bytes, chunks[0]);
            Assert.Empty(chunks[1]);
            var nested = Assert.IsType<Dictionary<string, List<byte[]>>>(mapped.GetProperty("nested")!.GetValue(copy));
            Assert.Equal(bytes, nested["data"][0]);
            Assert.Empty(nested["data"][1]);
            var converted = Assert.IsType<List<byte[]>>(mapped.GetProperty("converted")!.GetValue(copy));
            Assert.Equal(bytes, converted[0]);
            Assert.Empty(converted[1]);
        }
    }

    [Fact]
    public void ByteArrayAdaptersPreserveSharedStorageWithBlobs()
    {
        var source = new byte[] { 1, 2, 3 };
        var context = new CloneContext();
        var array = ModelAdapters.Value<byte[]>().Clone(source, context);
        var segment = ModelAdapters.Blob.Clone(new ArraySegment<byte>(source, 1, 2), context);
        Assert.NotSame(source, array);
        Assert.Same(array, segment.Array);
        Assert.True(ModelOperations.ValueEquals(source, array));
        Assert.Equal(ModelOperations.ValueHashCode(source), ModelOperations.ValueHashCode(array));
    }

    [Theory]
    [InlineData("namespace", "Example")]
    [InlineData("namespace", "Example=")]
    [InlineData("namespace", "Example=Bad;Name")]
    [InlineData("type", "Example.Value=System.DateTime;bad")]
    [InlineData("type", "Example.Value=System.Collections.Generic.List<{x}>")]
    [InlineData("type", "Example.Value=System.Collections.Generic.List<{1}>")]
    [InlineData("type", "Example.Value=System.Collections.Generic.List<")]
    [InlineData("type", "Example.Value=UnqualifiedType")]
    [InlineData("type", "Example.Value=global::int")]
    public async Task MalformedMappingsFailBeforeEmittingCode(string kind, string specification)
    {
        var options = kind == "namespace"
            ? new CSharpGenerationOptions { NamespaceMappings = [specification] }
            : new CSharpGenerationOptions { TypeMappings = [specification] };
        var result = await GenerateResult(
            "namespace Example using Value = int32; struct Item { 0: Value value; }", options);
        Assert.False(result.Success);
        Assert.Null(result.Code);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("--descriptors", CSharpModelFeatures.Descriptors)]
    [InlineData("--clone", CSharpModelFeatures.Cloning)]
    [InlineData("--clonable", CSharpModelFeatures.Cloning)]
    [InlineData("--equality", CSharpModelFeatures.Equality)]
    [InlineData("--default-equals", CSharpModelFeatures.Equality)]
    [InlineData("--debugger", CSharpModelFeatures.Debugger)]
    [InlineData("--to-string", CSharpModelFeatures.StringRepresentation)]
    public async Task CliFlagsSelectOnlyRequestedFeatures(string flag, CSharpModelFeatures features)
    {
        var root = NewTestDirectory();
        try
        {
            var input = Path.Combine(root, "item.bond");
            var output = Path.Combine(root, "generated");
            await File.WriteAllTextAsync(input, "namespace Example struct Item { 0: int32 id; }",
                TestContext.Current.CancellationToken);

            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            var exitCode = await GenerateCommand.RunAsync(
                ["csharp", input, "-o", output, flag, "--namespace=Example=Application"],
                standardOutput, standardError, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            Assert.Equal("", standardError.ToString());

            var code = await File.ReadAllTextAsync(Path.Combine(output, "item.g.cs"),
                TestContext.Current.CancellationToken);
            var assembly = Compile(code);
            var type = assembly.GetType("Application.Item", true)!;
            Assert.Equal(features.HasFlag(CSharpModelFeatures.Cloning), typeof(ICloneable).IsAssignableFrom(type));
            Assert.Equal(features.HasFlag(CSharpModelFeatures.Descriptors),
                assembly.GetType("Application.ItemSchema") != null);
            Assert.Equal(features.HasFlag(CSharpModelFeatures.Debugger),
                type.GetCustomAttribute<DebuggerDisplayAttribute>() != null);
            Assert.Equal(features.HasFlag(CSharpModelFeatures.Equality),
                typeof(IGeneratedEquatable).IsAssignableFrom(type));
            Assert.Equal(features.HasFlag(CSharpModelFeatures.StringRepresentation),
                typeof(IGeneratedSummary).IsAssignableFrom(type));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CliCombinesExplicitFeaturesWithUsingsAndSeparateTypeMappingsForEveryInput()
    {
        var root = NewTestDirectory();
        try
        {
            var input = Path.Combine(root, "item.bond");
            var otherInput = Path.Combine(root, "other.bond");
            var output = Path.Combine(root, "generated");
            await File.WriteAllTextAsync(input,
                "namespace Example using Timestamp = int64; struct Item { 0: Timestamp created; }",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(otherInput, "namespace Example struct Other { 0: int32 id; }",
                TestContext.Current.CancellationToken);

            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            var exitCode = await GenerateCommand.RunAsync(
                ["csharp", input, otherInput, "-o", output,
                    "--descriptors", "--clone", "--equality", "--debugger",
                    "-u", "System", "--using=System.Collections.Generic", "-u=System",
                    "--using", "System.Text", "--namespace=Example=Application",
                    "--type-map=Example.Timestamp=System.DateTime"],
                standardOutput, standardError, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            Assert.Empty(standardError.ToString());

            var sources = new List<string>();
            foreach (var file in new[] { "item.g.cs", "other.g.cs" })
            {
                var code = await File.ReadAllTextAsync(Path.Combine(output, file),
                    TestContext.Current.CancellationToken);
                Assert.Equal(new[] { "using System;", "using System.Collections.Generic;", "using System.Text;" },
                    code.Split('\n').Where(line => line.StartsWith("using ", StringComparison.Ordinal)));
                sources.Add(code);
            }

            sources.Add("""
                namespace Application {
                    public static class BondTypeAliasConverter {
                        public static System.DateTime Convert(long value, System.DateTime unused) =>
                            System.DateTime.UnixEpoch.AddTicks(value);
                        public static long Convert(System.DateTime value, long unused) =>
                            (value - System.DateTime.UnixEpoch).Ticks;
                    }
                }
                """);

            var assembly = Compile(sources.ToArray());
            foreach (var name in new[] { "Application.Item", "Application.Other" })
            {
                var type = assembly.GetType(name, true)!;
                Assert.True(typeof(IGeneratedModel).IsAssignableFrom(type));
                Assert.True(typeof(ICloneable).IsAssignableFrom(type));
                Assert.NotNull(type.GetCustomAttribute<DebuggerTypeProxyAttribute>());
            }

            var item = assembly.GetType("Application.Item", true)!;
            Assert.Equal(typeof(DateTime), item.GetProperty("created")!.PropertyType);
            Assert.Equal(DateTime.UnixEpoch, item.GetProperty("created")!.GetValue(Activator.CreateInstance(item)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("--clone=false")]
    [InlineData("--to-string=false")]
    [InlineData("--model-features=all")]
    [InlineData("--model-features=none")]
    [InlineData("--namespace=Example")]
    [InlineData("--type-map=Example.Value=System.String;bad")]
    [InlineData("--using=Example.Value=System.String")]
    [InlineData("-u=System;class Injected{}")]
    public async Task CliReportsInvalidFeatureOrMappingAsJsonWithoutWriting(string flag)
    {
        var root = NewTestDirectory();
        try
        {
            var input = Path.Combine(root, "item.bond");
            var output = Path.Combine(root, "generated");
            await File.WriteAllTextAsync(input, "namespace Example struct Item {}",
                TestContext.Current.CancellationToken);

            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            var exitCode = await GenerateCommand.RunAsync(
                ["csharp", input, "-o", output, flag, "--error-format=json"],
                standardOutput, standardError, TestContext.Current.CancellationToken);
            Assert.NotEqual(0, exitCode);
            using var error = JsonDocument.Parse(standardError.ToString());
            Assert.Equal("generation_error", error.RootElement.GetProperty("error").GetString());
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<CSharpGenerationResult> GenerateResult(string schema, CSharpGenerationOptions options)
    {
        var parsed = await ParserFacade.ParseStringAsync(schema);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));
        return CSharpGenerator.Generate(parsed.Ast!, "input.bond", options);
    }

    private static string Messages(CSharpGenerationResult result) =>
        string.Join("\n", result.Errors.Select(error => error.Message));

    private static string NewTestDirectory()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "mapping-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        return root;
    }
}
