using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Bond.IO.Safe;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Bond.Protocols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bond.TestSupport;

internal static class GeneratedCode
{
    public static async Task<string> Generate(string schema)
    {
        var parsed = await ParserFacade.ParseStringAsync(schema);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));

        var result = CSharpGenerator.Generate(parsed.Ast!, "input.bond",
            new CSharpGenerationOptions { ModelFeatures = CSharpModelFeatures.All });
        Assert.True(result.Success, string.Join("\n", result.Errors.Select(error => error.Message)));
        return result.Code!;
    }

    public static Assembly Compile(params string[] sources)
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
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, path: $"generated_{index}.cs")),
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // Bond resolves alias converters by assembly-qualified name in the default context.
        output.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(output);
    }

    public static string SchemaJson(Type type)
    {
        var schemaType = typeof(global::Bond.Schema<>).MakeGenericType(type);
        var schema = (global::Bond.RuntimeSchema)schemaType.GetProperty("RuntimeSchema")!.GetValue(null)!;
        return Encoding.UTF8.GetString(Write(typeof(global::Bond.SchemaDef), schema.SchemaDef, "json", 0, false));
    }

    public static byte[] Write(Type type, object value, string protocol, ushort version, bool framed)
    {
        var output = new OutputBuffer();
        switch (protocol)
        {
            case "compact":
                var compact = new CompactBinaryWriter<OutputBuffer>(output, version);
                if (framed)
                {
                    compact.WriteVersion();
                }

                new global::Bond.Serializer<CompactBinaryWriter<OutputBuffer>>(type).Serialize(value, compact);
                break;
            case "fast":
                var fast = new FastBinaryWriter<OutputBuffer>(output);
                if (framed)
                {
                    fast.WriteVersion();
                }

                new global::Bond.Serializer<FastBinaryWriter<OutputBuffer>>(type).Serialize(value, fast);
                break;
            case "simple":
                var simple = new SimpleBinaryWriter<OutputBuffer>(output, version);
                if (framed)
                {
                    simple.WriteVersion();
                }

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

    public static object Read(Type type, byte[] bytes, string protocol, ushort version, bool framed)
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
}
