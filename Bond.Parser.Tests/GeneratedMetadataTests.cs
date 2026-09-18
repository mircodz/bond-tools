using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using BondTools.Models;

namespace Bond.Parser.Tests;

using SchemaAttribute = global::BondTools.Models.SchemaAttribute;

public sealed class GeneratedMetadataTests
{
    [Fact]
    public async Task DescriptorsAreOptInAndDoNotEnableOtherModelFeatures()
    {
        var parsed = await ParserFacade.ParseStringAsync("namespace Example struct Item { 0: int32 value; }");
        Assert.True(parsed.Success);
        var plain = CSharpGenerator.Generate(parsed.Ast!, "input.bond");
        Assert.True(plain.Success);
        Assert.DoesNotContain("BondTools.Models", plain.Code!);
        Assert.DoesNotContain("__BondSchemaCatalog", plain.Code!);
        var plainAssembly = CSharpGeneratorTests.Compile(plain.Code!);
        Assert.Null(plainAssembly.GetType("Example.ItemSchema"));

        var generated = CSharpGenerator.Generate(parsed.Ast!, "input.bond", new CSharpGenerationOptions
        {
            ModelFeatures = CSharpModelFeatures.Descriptors
        });
        Assert.True(generated.Success, string.Join("\n", generated.Errors.Select(error => error.Message)));
        Assert.DoesNotContain("IEquatable<", generated.Code!);
        Assert.DoesNotContain("DebuggerTypeProxy", generated.Code!);
        var assembly = CSharpGeneratorTests.Compile(generated.Code!);
        var model = assembly.GetType("Example.Item", throwOnError: true)!;
        Assert.Equal(new[] { typeof(IGeneratedSchemaProvider) }, model.GetInterfaces());
        Assert.Null(model.GetMethod("Clone"));
        Assert.Equal("Example.Item", Read(assembly, "Example.ItemSchema").FullName);
    }

    [Fact]
    public async Task DescriptorsPreserveDeclaredIdentityAttributesFieldsAndDefaults()
    {
        var code = await CSharpGeneratorTests.Generate("""
            namespace Contracts
            namespace csharp Application.Models
            [doc.category("enum")]
            enum State { Unknown = -2, Ready, AlsoReady = -1, Last }
            [doc.category("model")]
            [doc.category("second")]
            struct Record {
                [doc.unit("items")] 9: required uint64 count = 18446744073709551615;
                3: required_optional State state = Ready;
                1: wstring title = "wide \u03bb";
                4: bool enabled = true;
                5: double signed_zero = -0.0;
                6: int64 minimum = -9223372036854775808;
                7: string inferred;
            }
            """);
        var assembly = CSharpGeneratorTests.Compile(code);
        var descriptor = Read(assembly, "Application.Models.RecordSchema");
        Assert.Equal("Record", descriptor.Name);
        Assert.Equal("Contracts", descriptor.Namespace);
        Assert.Equal("Contracts.Record", descriptor.FullName);
        Assert.Equal("global::Application.Models.Record", descriptor.ClrName);
        Assert.Equal(SchemaKind.Struct, descriptor.Kind);
        Assert.Equal(new[] { new SchemaAttribute("doc.category", "model"), new SchemaAttribute("doc.category", "second") },
            descriptor.Attributes);
        Assert.Equal(new ushort[] { 1, 3, 4, 5, 6, 7, 9 }, descriptor.Fields.Select(field => field.Id));
        var title = descriptor.Fields[0];
        Assert.Equal("title", title.Name);
        Assert.Equal(SchemaTypeKind.WString, title.Type.Kind);
        Assert.Equal("wide \u03bb", Assert.IsType<SchemaDefault.String>(title.DefaultValue).Value);
        var state = descriptor.Fields[1];
        Assert.Equal(SchemaFieldModifier.RequiredOptional, state.Modifier);
        Assert.Equal("Ready", Assert.IsType<SchemaDefault.Enum>(state.DefaultValue).Name);
        Assert.True(Assert.IsType<SchemaDefault.Boolean>(descriptor.Fields[2].DefaultValue).Value);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(
            Assert.IsType<SchemaDefault.FloatingPoint>(descriptor.Fields[3].DefaultValue).Value));
        Assert.Equal(new BigInteger(long.MinValue), Assert.IsType<SchemaDefault.Integer>(descriptor.Fields[4].DefaultValue).Value);
        Assert.Null(descriptor.Fields[5].DefaultValue);
        var count = descriptor.Fields[6];
        Assert.Equal(SchemaFieldModifier.Required, count.Modifier);
        Assert.Equal(new BigInteger(ulong.MaxValue), Assert.IsType<SchemaDefault.Integer>(count.DefaultValue).Value);
        Assert.Equal(new SchemaAttribute("doc.unit", "items"), Assert.Single(count.Attributes));
        var enumeration = Assert.IsType<NamedSchemaType>(state.Type).Declaration;
        Assert.Same(Read(assembly, "Application.Models.StateSchema"), enumeration);
        Assert.Equal("Contracts.State", enumeration.FullName);
    }

    [Fact]
    public async Task EnumValuesRetainEffectiveAndDeclaredNumbersWithoutLoadingTheEnum()
    {
        var descriptor = Read(CSharpGeneratorTests.Compile(await CSharpGeneratorTests.Generate("""
            namespace Example
            [kind("state")]
            enum State { Unknown = -2, Ready, Duplicate = -1, Last, Bits = 0xffffffff }
            """)), "Example.StateSchema");
        Assert.Equal(SchemaKind.Enum, descriptor.Kind);
        Assert.Equal(new[]
        {
            new EnumValueDescriptor("Unknown", -2, -2),
            new EnumValueDescriptor("Ready", -1),
            new EnumValueDescriptor("Duplicate", -1, -1),
            new EnumValueDescriptor("Last", 0),
            new EnumValueDescriptor("Bits", -1, 4294967295)
        }, descriptor.EnumValues);
        Assert.Equal(new SchemaAttribute("kind", "state"), Assert.Single(descriptor.Attributes));
        Assert.Empty(descriptor.Fields);
    }

    [Fact]
    public async Task EverySupportedTypeRemainsSymbolicRatherThanCollapsingToItsClrType()
    {
        var descriptor = Read(CSharpGeneratorTests.Compile(await CSharpGeneratorTests.Generate("""
            namespace Example
            struct Child {}
            struct Types {
                0: int8 a;
                1: int16 b;
                2: int32 c;
                3: int64 d;
                4: uint8 e;
                5: uint16 f;
                6: uint32 g;
                7: uint64 h;
                8: float i;
                9: double j;
                10: bool k;
                11: string l;
                12: wstring m;
                13: blob n;
                14: bond_meta::name name;
                15: bond_meta::full_name full_name;
                16: list<string> list_values;
                17: vector<string> vector_values;
                18: set<string> set_values;
                19: map<wstring, vector<int32>> map_values;
                20: nullable<int32> nullable_value;
                21: int32 absent = nothing;
                22: bonded<Child> deferred;
            }
            """)), "Example.TypesSchema");
        Assert.Equal(new[]
        {
            SchemaTypeKind.Int8, SchemaTypeKind.Int16, SchemaTypeKind.Int32, SchemaTypeKind.Int64,
            SchemaTypeKind.UInt8, SchemaTypeKind.UInt16, SchemaTypeKind.UInt32, SchemaTypeKind.UInt64,
            SchemaTypeKind.Float, SchemaTypeKind.Double, SchemaTypeKind.Bool, SchemaTypeKind.String,
            SchemaTypeKind.WString, SchemaTypeKind.Blob, SchemaTypeKind.MetaName, SchemaTypeKind.MetaFullName,
            SchemaTypeKind.List, SchemaTypeKind.Vector, SchemaTypeKind.Set, SchemaTypeKind.Map,
            SchemaTypeKind.Nullable, SchemaTypeKind.Maybe, SchemaTypeKind.Bonded
        }, descriptor.Fields.Select(field => field.Type.Kind));
        var map = Assert.IsType<MapSchemaType>(descriptor.Fields[19].Type);
        Assert.Equal(SchemaTypeKind.WString, map.KeyType.Kind);
        Assert.Equal(SchemaTypeKind.Int32, Assert.IsType<UnarySchemaType>(map.ValueType).ElementType.Kind);
        Assert.Null(descriptor.Fields[20].DefaultValue);
        Assert.Equal(SchemaFieldModifier.Optional, descriptor.Fields[14].Modifier);
        Assert.Equal(SchemaFieldModifier.RequiredOptional, descriptor.Fields[14].EffectiveModifier);
        Assert.Equal("map<wstring, vector<int32>>", descriptor.Fields[19].Type.ToString());
        Assert.Same(SchemaDefault.Nothing.Instance, descriptor.Fields[21].DefaultValue);
        var bonded = Assert.IsType<UnarySchemaType>(descriptor.Fields[22].Type);
        Assert.Equal("Example.Child", Assert.IsType<NamedSchemaType>(bonded.ElementType).Declaration.FullName);
    }

    [Fact]
    public async Task InheritanceAndViewsRetainOwnFieldsAndSourceSelections()
    {
        var assembly = CSharpGeneratorTests.Compile(await CSharpGeneratorTests.Generate("""
            namespace Example
            struct Base<T> { 0: T inherited; }
            [source("not copied")]
            struct Record<T : value> : Base<T> {
                11: string omitted;
                [field("copied")] 5: required_optional int32 number = 42;
                2: T item = nothing;
            }
            [view("own")]
            struct Selected view_of Record { number; item; number; missing; inherited }
            """));
        var descriptor = Read(assembly, "Example.SelectedSchema");
        Assert.True(descriptor.IsView);
        Assert.Equal(new[] { "Record" }, descriptor.ViewTarget);
        Assert.Equal(new[] { "number", "item", "number", "missing", "inherited" }, descriptor.ViewFields);
        Assert.Equal(new[] { "item", "number" }, descriptor.Fields.Select(field => field.Name));
        Assert.Equal(new SchemaAttribute("view", "own"), Assert.Single(descriptor.Attributes));
        Assert.Equal(new SchemaAttribute("field", "copied"), Assert.Single(descriptor.Fields[1].Attributes));
        Assert.Equal(SchemaTypeConstraint.Value, Assert.Single(descriptor.TypeParameters).Constraint);
        var bound = descriptor.Bind(new PrimitiveSchemaType(SchemaTypeKind.Int32));
        Assert.Equal(SchemaTypeKind.Int32, Assert.IsType<UnarySchemaType>(bound.Fields[0].Type).ElementType.Kind);
        var parent = Assert.IsType<NamedSchemaType>(bound.BaseType).Resolve();
        Assert.Equal("Example.Base", parent.FullName);
        Assert.Equal(SchemaTypeKind.Int32, Assert.Single(parent.Fields).Type.Kind);
        Assert.Same(Read(assembly, "Example.BaseSchema"), parent.Definition);
        Assert.Same(descriptor, bound.Definition);
        Assert.IsType<TypeParameterSchemaType>(Assert.IsType<UnarySchemaType>(descriptor.Fields[0].Type).ElementType);
    }

    [Fact]
    public async Task GenericAliasesBindSimultaneouslyAndPreserveSignedErasedArguments()
    {
        var assembly = CSharpGeneratorTests.Compile(await CSharpGeneratorTests.Generate("""
            namespace Example
            using Pair<T, U> = map<T, vector<U>>;
            using Swap<T, U> = Pair<U, T>;
            using Erased<T, N> = T;
            struct Box<T, U> {
                0: Swap<T, U> swapped;
                1: Erased<int32, -32> fixed_number;
            }
            """));
        var descriptor = Read(assembly, "Example.BoxSchema");
        var bound = descriptor.Bind(new PrimitiveSchemaType(SchemaTypeKind.Int32),
            new PrimitiveSchemaType(SchemaTypeKind.String));
        var swap = Assert.IsType<NamedSchemaType>(bound.Fields[0].Type);
        Assert.Equal(SchemaTypeKind.Alias, swap.Kind);
        Assert.Same(Assert.IsType<NamedSchemaType>(descriptor.Fields[0].Type).Declaration, swap.Declaration);
        Assert.Equal("Example.Swap", swap.Declaration.FullName);
        Assert.Null(assembly.GetType("Example.SwapSchema"));
        var pair = Assert.IsType<NamedSchemaType>(swap.Resolve().AliasedType);
        Assert.Equal(new[] { SchemaTypeKind.String, SchemaTypeKind.Int32 }, pair.TypeArguments.Select(type => type.Kind));
        var map = Assert.IsType<MapSchemaType>(pair.Resolve().AliasedType);
        Assert.Equal(SchemaTypeKind.String, map.KeyType.Kind);
        Assert.Equal(SchemaTypeKind.Int32, Assert.IsType<UnarySchemaType>(map.ValueType).ElementType.Kind);
        var erased = Assert.IsType<NamedSchemaType>(bound.Fields[1].Type);
        Assert.Equal(-32, Assert.IsType<IntegerArgumentSchemaType>(erased.TypeArguments[1]).Value);
        Assert.Equal(SchemaTypeKind.Int32, erased.Resolve().AliasedType!.Kind);
        Assert.Throws<ArgumentException>(() => descriptor.Bind(new PrimitiveSchemaType(SchemaTypeKind.Int32)));
        Assert.Throws<ArgumentException>(() => descriptor.Bind(new SchemaType[] { null!, null! }));
        Assert.Throws<ArgumentNullException>(() => descriptor.Bind(null!));
        Assert.IsType<TypeParameterSchemaType>(Assert.IsType<NamedSchemaType>(descriptor.Fields[0].Type).TypeArguments[0]);
    }

    [Fact]
    public async Task RecursiveDescriptorsDoNotInitializeOrConstructModels()
    {
        var code = await CSharpGeneratorTests.Generate("""
            namespace Example
            struct Node<T> {
                0: Node<T> eager;
                1: vector<Other<T>> children;
            }
            struct Other<T> { 0: nullable<Node<T>> parent; }
            """);
        var assembly = CSharpGeneratorTests.Compile(code, """
            namespace Example
            {
                public partial class Node<T>
                {
                    private readonly int constructorTrap = Fail();
                    private static int Fail() => throw new System.InvalidOperationException("Constructor must not run.");
                    static Node() => throw new System.InvalidOperationException("Type initializer must not run.");
                }
            }
            """);
        var descriptor = Read(assembly, "Example.NodeSchema");
        var self = Assert.IsType<NamedSchemaType>(descriptor.Fields[0].Type);
        Assert.Same(descriptor, self.Declaration);
        var bound = descriptor.Bind(new PrimitiveSchemaType(SchemaTypeKind.Int32));
        var other = Assert.IsType<NamedSchemaType>(Assert.IsType<UnarySchemaType>(bound.Fields[1].Type).ElementType).Resolve();
        var parent = Assert.IsType<NamedSchemaType>(Assert.IsType<UnarySchemaType>(other.Fields[0].Type).ElementType);
        Assert.Same(descriptor, parent.Declaration);
        Assert.Equal(SchemaTypeKind.Int32, Assert.Single(parent.Resolve().TypeArguments).Kind);
        Assert.Same(parent.Resolve(), parent.Resolve());
        Assert.DoesNotContain("RuntimeSchema", code);
        Assert.DoesNotContain("System.Reflection", code);
        Assert.DoesNotContain("System.Linq.Expressions", code);
    }

    [Fact]
    public async Task UnresolvedForwardMetadataIsExplicitlyUnknownAndNeverDiscoversClrFields()
    {
        var code = await CSharpGeneratorTests.Generate("""
            namespace Example
            struct External<T>;
            struct Node<T>;
            struct Empty {}
            struct Holder {
                0: nullable<External<int32>> external;
                1: nullable<Node<int32>> node;
                2: nullable<Empty> empty;
            }
            struct Node<T> { 0: T value; }
            """);
        var assembly = CSharpGeneratorTests.Compile(code, """
            namespace Example
            {
                public class External<T>
                {
                    public int OnlyKnownToClr { get; set; }
                    public External() => throw new System.InvalidOperationException("Constructor must not run.");
                    static External() => throw new System.InvalidOperationException("Type initializer must not run.");
                }
            }
            """);
        var holder = Read(assembly, "Example.HolderSchema");
        var external = Assert.IsType<NamedSchemaType>(Assert.IsType<UnarySchemaType>(holder.Fields[0].Type).ElementType);
        Assert.Equal(SchemaTypeKind.Struct, external.Kind);
        Assert.Equal("Example.External", external.FullName);
        Assert.True(external.Declaration.IsForward);
        Assert.Equal("global::Example.External<T>", external.Declaration.ClrName);
        Assert.Empty(external.Declaration.Fields);
        Assert.Null(external.Declaration.BaseType);
        var bound = external.Resolve();
        Assert.True(bound.IsForward);
        Assert.Same(external.Declaration, bound.Definition);
        Assert.Equal(SchemaTypeKind.Int32, Assert.Single(bound.TypeArguments).Kind);
        Assert.True(bound.AsType().Resolve().IsForward);
        Assert.Null(assembly.GetType("Example.ExternalSchema"));
        var node = Assert.IsType<NamedSchemaType>(Assert.IsType<UnarySchemaType>(holder.Fields[1].Type).ElementType).Resolve();
        Assert.False(node.IsForward);
        Assert.Same(Read(assembly, "Example.NodeSchema"), node.Definition);
        Assert.Equal(SchemaTypeKind.Int32, Assert.Single(node.Fields).Type.Kind);
        var empty = Assert.IsType<NamedSchemaType>(Assert.IsType<UnarySchemaType>(holder.Fields[2].Type).ElementType).Declaration;
        Assert.False(empty.IsForward);
        Assert.Empty(empty.Fields);
    }

    [Fact]
    public async Task MappedAliasesDoNotRequireClrTypesForErasedIntegerArguments()
    {
        var code = await GenerateWithImports("""
            namespace Example
            using Identity<T> = T;
            using Symbolic = Identity<-32>;
            using Erased<T> = int32;
            using Values<T, N> = vector<T>;
            struct Holder {
                0: Erased<Symbolic> scalar;
                1: Values<int32, -64> values;
            }
            """, new Dictionary<string, string>(), new CSharpGenerationOptions
        {
            ModelFeatures = CSharpModelFeatures.Descriptors,
            TypeMappings = ["Example.Erased=int", "Example.Values=System.Collections.Generic.List<{0}>"]
        });
        var holder = Read(CSharpGeneratorTests.Compile(code), "Example.HolderSchema");
        var erased = Assert.IsType<NamedSchemaType>(holder.Fields[0].Type);
        Assert.Equal("int", erased.Declaration.ClrName);
        var symbolic = Assert.IsType<NamedSchemaType>(Assert.Single(erased.TypeArguments)).Declaration;
        Assert.Equal("Example.Symbolic", symbolic.FullName);
        Assert.Null(symbolic.ClrName);
        var identity = Assert.IsType<NamedSchemaType>(symbolic.AliasedType);
        Assert.Equal(-32, Assert.IsType<IntegerArgumentSchemaType>(identity.Resolve().AliasedType).Value);
        var values = Assert.IsType<NamedSchemaType>(holder.Fields[1].Type);
        Assert.Equal("global::System.Collections.Generic.List<T>", values.Declaration.ClrName);
        Assert.Equal(-64, Assert.IsType<IntegerArgumentSchemaType>(values.TypeArguments[1]).Value);
        Assert.Equal(SchemaTypeKind.Int32, Assert.IsType<UnarySchemaType>(values.Resolve().AliasedType).ElementType.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportedMetadataUsesOneLocalCatalogWithRichOrPlainImportedModels(bool richImports)
    {
        const string imported = """
            namespace Library
            using Text = wstring;
            enum State { Ready }
            struct Base<T> { 0: T value; }
            struct Node { 0: vector<Node> children; 1: Text title; }
            """;
        var root = await GenerateWithImports("""
            import "library.bond"
            namespace Application
            struct Root : Library.Base<Library.State> {
                0: Library.Node node;
                1: Library.Text title;
            }
            """, new Dictionary<string, string> { ["library.bond"] = imported });
        Assert.DoesNotContain("class NodeSchema", root);
        Assert.DoesNotContain("class BaseSchema", root);
        Assert.DoesNotContain("class TextSchema", root);
        Assert.DoesNotContain("Library.NodeSchema", root);
        Assert.DoesNotContain("Library.BaseSchema", root);
        Assert.DoesNotContain("Library.TextSchema", root);
        var importedCode = await GenerateWithImports(imported, new Dictionary<string, string>(),
            new CSharpGenerationOptions
            {
                ModelFeatures = richImports ? CSharpModelFeatures.Descriptors : CSharpModelFeatures.None
            });
        var assembly = CSharpGeneratorTests.Compile(root, importedCode);
        var descriptor = Read(assembly, "Application.RootSchema");
        var node = Assert.IsType<NamedSchemaType>(descriptor.Fields[0].Type).Declaration;
        Assert.Equal("Library.Node", node.FullName);
        Assert.Equal(new[] { "children", "title" }, node.Fields.Select(field => field.Name));
        Assert.Same(node, Assert.IsType<NamedSchemaType>(Assert.IsType<UnarySchemaType>(node.Fields[0].Type).ElementType).Declaration);
        var title = Assert.IsType<NamedSchemaType>(descriptor.Fields[1].Type).Declaration;
        Assert.Same(Assert.IsType<NamedSchemaType>(node.Fields[1].Type).Declaration, title);
        Assert.Equal(SchemaKind.Alias, title.Kind);
        Assert.Equal(SchemaTypeKind.WString, title.AliasedType!.Kind);
        Assert.Equal("string", title.ClrName);
        var parent = Assert.IsType<NamedSchemaType>(descriptor.BaseType).Resolve();
        Assert.Equal("Library.State", Assert.IsType<NamedSchemaType>(Assert.Single(parent.Fields).Type).Declaration.FullName);
        Assert.Null(assembly.GetType("Library.TextSchema"));
        if (richImports)
        {
            var independent = Read(assembly, "Library.NodeSchema");
            Assert.NotSame(independent, node);
            Assert.Equal(independent.FullName, node.FullName);
            Assert.Equal(independent.ClrName, node.ClrName);
            Assert.Equal(independent.Fields.Select(field => (field.Id, field.Name, field.Type.Kind)),
                node.Fields.Select(field => (field.Id, field.Name, field.Type.Kind)));
        }
        else
        {
            Assert.Null(assembly.GetType("Library.NodeSchema"));
            Assert.Null(assembly.GetType("Library.BaseSchema"));
        }
    }

    [Fact]
    public async Task AliasShadowingKeepsIndependentFileLocalDeclarationIdentity()
    {
        const string imported = """
            namespace Example
            using Value = int32;
            struct Imported { 0: Value value; }
            """;
        var root = await GenerateWithImports("""
            import "library.bond"
            namespace Example
            using Value = string;
            struct Root { 0: Value value; 1: Imported other; 2: Value again; }
            """, new Dictionary<string, string> { ["library.bond"] = imported });
        var assembly = CSharpGeneratorTests.Compile(root, await CSharpGeneratorTests.Generate(imported));
        var descriptor = Read(assembly, "Example.RootSchema");
        var local = Assert.IsType<NamedSchemaType>(descriptor.Fields[0].Type).Declaration;
        var importedModel = Assert.IsType<NamedSchemaType>(descriptor.Fields[1].Type).Declaration;
        var importedAlias = Assert.IsType<NamedSchemaType>(importedModel.Fields[0].Type).Declaration;
        Assert.Equal(local.FullName, importedAlias.FullName);
        Assert.NotSame(local, importedAlias);
        Assert.Equal(SchemaTypeKind.String, local.AliasedType!.Kind);
        Assert.Equal(SchemaTypeKind.Int32, importedAlias.AliasedType!.Kind);
        Assert.Same(local, Assert.IsType<NamedSchemaType>(descriptor.Fields[2].Type).Declaration);
        Assert.Null(assembly.GetType("Example.ValueSchema"));
    }

    [Fact]
    public async Task IdenticalAliasesFromSeparateImportsStillHaveDistinctIdentity()
    {
        const string left = """
            namespace Shared
            using Value = int32;
            struct Left { 0: Value value; }
            """;
        const string right = """
            namespace Shared
            using Value = int32;
            struct Right { 0: Value value; }
            """;
        var root = await GenerateWithImports("""
            import "left.bond"
            import "right.bond"
            namespace Application
            struct Root { 0: Shared.Left left; 1: Shared.Right right; }
            """, new Dictionary<string, string> { ["left.bond"] = left, ["right.bond"] = right });
        var assembly = CSharpGeneratorTests.Compile(root,
            await CSharpGeneratorTests.Generate(left), await CSharpGeneratorTests.Generate(right));
        var descriptor = Read(assembly, "Application.RootSchema");
        var leftModel = Assert.IsType<NamedSchemaType>(descriptor.Fields[0].Type).Declaration;
        var rightModel = Assert.IsType<NamedSchemaType>(descriptor.Fields[1].Type).Declaration;
        var leftAlias = Assert.IsType<NamedSchemaType>(leftModel.Fields[0].Type).Declaration;
        var rightAlias = Assert.IsType<NamedSchemaType>(rightModel.Fields[0].Type).Declaration;
        Assert.Equal(leftAlias.FullName, rightAlias.FullName);
        Assert.Equal(leftAlias.AliasedType!.Kind, rightAlias.AliasedType!.Kind);
        Assert.NotSame(leftAlias, rightAlias);
        Assert.Null(assembly.GetType("Shared.ValueSchema"));
    }

    [Fact]
    public async Task DescriptorCompanionsDoNotCollideWithSourceModelNames()
    {
        var assembly = CSharpGeneratorTests.Compile(await CSharpGeneratorTests.Generate("""
            namespace Example
            struct Item { 0: ItemSchema value; 1: Text text; }
            struct ItemSchema {}
            struct ItemSchema_ {}
            enum State { Ready }
            struct StateSchema {}
            using Text = string;
            struct TextSchema {}
            """));
        Assert.Equal("Item", Read(assembly, "Example.ItemSchema__").Name);
        Assert.Equal("ItemSchema", Read(assembly, "Example.ItemSchemaSchema").Name);
        Assert.Equal("State", Read(assembly, "Example.StateSchema_").Name);
        var item = Read(assembly, "Example.ItemSchema__");
        Assert.Equal("Text", Assert.IsType<NamedSchemaType>(item.Fields[1].Type).Declaration.Name);
        Assert.Null(assembly.GetType("Example.TextSchema_"));
    }

    [Fact]
    public async Task FileLocalCatalogAvoidsSourceTypeAndNamespaceNames()
    {
        var code = await CSharpGeneratorTests.Generate("""
            namespace __BondSchemaCatalog__
            struct __BondSchemaCatalog {}
            struct __BondSchemaCatalog_ {}
            struct Item { 0: __BondSchemaCatalog value; }
            """);
        Assert.Contains("file static class __BondSchemaCatalog___", code);
        var assembly = CSharpGeneratorTests.Compile(code);
        var descriptor = Read(assembly, "__BondSchemaCatalog__.ItemSchema");
        Assert.Equal("__BondSchemaCatalog", Assert.IsType<NamedSchemaType>(descriptor.Fields[0].Type).Declaration.Name);
    }

    [Fact]
    public async Task UnusedAliasesStayPrivateAndCatalogGenerationDoesNotDependOnInputPath()
    {
        var parsed = await ParserFacade.ParseStringAsync("""
            namespace __BondSchemaCatalog
            using Value = int32;
            using Erased<T, N> = T;
            """);
        Assert.True(parsed.Success);
        var options = new CSharpGenerationOptions { ModelFeatures = CSharpModelFeatures.Descriptors };
        var first = CSharpGenerator.Generate(parsed.Ast!, "first/input.bond", options);
        var second = CSharpGenerator.Generate(parsed.Ast!, "second/renamed.bond", options);
        Assert.True(first.Success, string.Join("\n", first.Errors.Select(error => error.Message)));
        Assert.Equal(first.Code, second.Code);
        Assert.Contains("file static class __BondSchemaCatalog_", first.Code!);
        Assert.DoesNotContain("public static class", first.Code!);
        Assert.Empty(CSharpGeneratorTests.Compile(first.Code!).GetExportedTypes());
    }

    [Fact]
    public async Task ExplicitMappingsChangeClrNamesButNotSourceSchemaIdentity()
    {
        var parsed = await ParserFacade.ParseStringAsync("""
            namespace Contracts
            using Timestamp = int64;
            struct Item { 0: int32 id; 1: Timestamp created; }
            """);
        Assert.True(parsed.Success);
        var generated = CSharpGenerator.Generate(parsed.Ast!, "input.bond", new CSharpGenerationOptions
        {
            ModelFeatures = CSharpModelFeatures.Descriptors,
            NamespaceMappings = ["Contracts=Application"],
            TypeMappings = ["Contracts.Timestamp=System.DateTime"]
        });
        Assert.True(generated.Success, string.Join("\n", generated.Errors.Select(error => error.Message)));
        var assembly = CSharpGeneratorTests.Compile(generated.Code!, """
            namespace Application
            {
                public static class BondTypeAliasConverter
                {
                    public static System.DateTime Convert(long value, System.DateTime ignored) => new System.DateTime(value);
                    public static long Convert(System.DateTime value, long ignored) => value.Ticks;
                }
            }
            """);
        var model = Read(assembly, "Application.ItemSchema");
        Assert.Equal("Contracts.Item", model.FullName);
        Assert.Equal("global::Application.Item", model.ClrName);
        var alias = Assert.IsType<NamedSchemaType>(model.Fields[1].Type).Declaration;
        Assert.Equal("Contracts.Timestamp", alias.FullName);
        Assert.EndsWith("System.DateTime", alias.ClrName, StringComparison.Ordinal);
        Assert.Equal(SchemaTypeKind.Int64, alias.AliasedType!.Kind);
    }

    [Fact]
    public async Task NamedDescriptorArgumentsCanBeSuppliedWithoutAnyClrTypeInformation()
    {
        var assembly = CSharpGeneratorTests.Compile(await CSharpGeneratorTests.Generate("""
            namespace Example
            enum State { Ready }
            struct Box<T> { 0: T value; }
            """));
        var state = Read(assembly, "Example.StateSchema");
        var box = Read(assembly, "Example.BoxSchema");
        var bound = box.Bind(state.AsType());
        Assert.Same(state, Assert.IsType<NamedSchemaType>(bound.Fields[0].Type).Resolve());
        Assert.Same(box, bound.AsType().Declaration);
        Assert.Same(state, Assert.IsType<NamedSchemaType>(Assert.Single(bound.AsType().TypeArguments)).Declaration);
        Assert.Throws<ArgumentException>(() => box.AsType());
        Assert.Same(state, state.Bind());
    }

    [Fact]
    public void PublicDescriptorsDefensivelyFreezeEveryInputCollection()
    {
        var number = new PrimitiveSchemaType(SchemaTypeKind.Int32);
        var attributes = new[] { new SchemaAttribute("key", "original") };
        var field = new FieldDescriptor(9, "value", number, attributes: attributes);
        var fields = new[] { field, new FieldDescriptor(1, "first", number) };
        var parameters = new[] { new TypeParameterDescriptor("T") };
        var values = new[] { new EnumValueDescriptor("First", 1) };
        var target = new[] { "Source" };
        var selection = new[] { "value" };
        var descriptor = new SchemaDescriptor("Model", "Example", "Example.Model<T>", SchemaKind.Struct,
            fields, typeParameters: parameters, enumValues: values, attributes: attributes,
            isView: true, viewTarget: target, viewFields: selection);
        var arguments = new SchemaType[] { number };
        var calls = 0;
        var reference = new NamedSchemaType(SchemaTypeKind.Struct, "Model", "Example", () =>
        {
            calls++;
            return descriptor;
        }, arguments);
        var bound = descriptor.Bind(arguments);
        Assert.Equal(0, calls);
        attributes[0] = new SchemaAttribute("key", "changed");
        fields[0] = fields[1];
        parameters[0] = new TypeParameterDescriptor("Changed");
        values[0] = new EnumValueDescriptor("Changed", 2);
        target[0] = selection[0] = "Changed";
        arguments[0] = new PrimitiveSchemaType(SchemaTypeKind.String);
        Assert.Equal("original", Assert.Single(field.Attributes).Value);
        Assert.Equal("original", Assert.Single(descriptor.Attributes).Value);
        Assert.Equal(new ushort[] { 1, 9 }, descriptor.Fields.Select(value => value.Id));
        Assert.Equal("T", Assert.Single(descriptor.TypeParameters).Name);
        Assert.Equal("First", Assert.Single(descriptor.EnumValues).Name);
        Assert.Equal("Source", Assert.Single(descriptor.ViewTarget));
        Assert.Equal("value", Assert.Single(descriptor.ViewFields));
        Assert.Same(number, Assert.Single(reference.TypeArguments));
        Assert.Same(number, Assert.Single(bound.TypeArguments));
        Assert.Same(descriptor, reference.Declaration);
        Assert.Same(descriptor, reference.Declaration);
        Assert.Equal(1, calls);
        AssertFrozen(descriptor.Fields, field);
        AssertFrozen(descriptor.Attributes, attributes[0]);
        AssertFrozen(field.Attributes, attributes[0]);
        AssertFrozen(descriptor.TypeParameters, parameters[0]);
        AssertFrozen(descriptor.EnumValues, values[0]);
        AssertFrozen(descriptor.ViewTarget, "Changed");
        AssertFrozen(descriptor.ViewFields, "Changed");
        AssertFrozen(reference.TypeArguments, arguments[0]);
        AssertFrozen(bound.TypeArguments, arguments[0]);
    }

    private static void AssertFrozen<T>(IReadOnlyList<T> values, T replacement)
    {
        Assert.False(values is T[]);
        var mutable = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(mutable.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutable[0] = replacement);
        Assert.Throws<NotSupportedException>(() => mutable.Add(replacement));
    }

    private static SchemaDescriptor Read(Assembly assembly, string companion)
    {
        var getter = assembly.GetType(companion, throwOnError: true)!.GetProperty("Descriptor")!.GetMethod!;
        return getter.CreateDelegate<Func<SchemaDescriptor>>()();
    }

    private static async Task<string> GenerateWithImports(string source, IReadOnlyDictionary<string, string> imports,
        CSharpGenerationOptions? options = null)
    {
        var parsed = await ParserFacade.ParseStringAsync(source,
            (_, path) => Task.FromResult((path, imports[path])));
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors.Select(error => error.Message)));
        var generated = CSharpGenerator.Generate(parsed.Ast!, "input.bond",
            options ?? new CSharpGenerationOptions { ModelFeatures = CSharpModelFeatures.Descriptors });
        Assert.True(generated.Success, string.Join("\n", generated.Errors.Select(error => error.Message)));
        return generated.Code!;
    }
}
