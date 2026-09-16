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
        Enumerable.Range(0, 16).Select(value => new object[] { (CSharpModelFeatures)value });

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
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Equality),
            type.GetMethod("Equals", [typeof(object)])!.DeclaringType == type);
        Assert.Equal(features.HasFlag(CSharpModelFeatures.Equality),
            type.GetMethod("GetHashCode", Type.EmptyTypes)!.DeclaringType == type);
        if (features == CSharpModelFeatures.None)
            Assert.DoesNotContain("BondTools.Models", result.Code!);
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
    [InlineData("--model-features=all", CSharpModelFeatures.All)]
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
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("--clone=false")]
    [InlineData("--model-features=unknown")]
    [InlineData("--namespace=Example")]
    [InlineData("--type-map=Example.Value=System.String;bad")]
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
