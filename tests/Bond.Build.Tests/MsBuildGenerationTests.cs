using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Bond.Build.Tests;

public sealed class MsBuildGenerationTests(MsBuildPackageFixture packages) : IClassFixture<MsBuildPackageFixture>
{
    [Fact]
    public async Task DemoUsesThePackagedBuildIntegration()
    {
        var project = packages.CreateConsumer();
        project.Write("Demo.bond", packages.ReadExample("Demo.bond"));
        project.Write("Demo.Shared.bond", packages.ReadExample("Demo.Shared.bond"));
        project.Write("Program.cs", packages.ReadExample("Demo/Program.cs"));
        project.Write("BondTypeAliasConverter.cs", packages.ReadExample("Demo/BondTypeAliasConverter.cs"));
        project.Configure(
            new XElement("Bond", new XAttribute("Include", "Demo*.bond"),
                new XAttribute("Descriptors", "true"), new XAttribute("Clone", "true"),
                new XAttribute("Equality", "true"), new XAttribute("Debugger", "true"), new XAttribute("ToString", "true")),
            new XElement("BondNamespaceMapping", new XAttribute("Include", "Demo.Contracts=Demo.Generated")),
            new XElement("BondTypeMapping", new XAttribute("Include", "demo.Timestamp=System.DateTime")),
            new XElement("BondUsing", new XAttribute("Include", "System.Collections.Generic")));

        (await project.Build()).AssertSuccess();
        Assert.Equal(2, project.GeneratedFiles().Length);

        var run = await project.Run();
        run.AssertSuccess();
        Assert.Contains("demo.OrderCreated", run.Output);
        Assert.Contains("Debugger fields: 15", run.Output);
        Assert.Contains("Order {", run.Output);
    }

    [Fact]
    public async Task GeneratesToStringIndependentlyOfOtherModelFeatures()
    {
        var project = packages.CreateConsumer();
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; }");
        project.Write("Program.cs", """
            var item = new Contracts.Item { id = 42 };
            if (item.ToString() != "Item { id = 42 }")
            {
                throw new System.Exception("Unexpected generated summary.");
            }

            System.Console.WriteLine(item);
            """);
        project.Configure(new XElement("Bond", new XAttribute("Include", "item.bond"), new XAttribute("ToString", "true")));

        (await project.Build()).AssertSuccess();
        var output = File.ReadAllText(Assert.Single(project.GeneratedFiles()));
        Assert.Contains("override string ToString()", output);
        Assert.DoesNotContain("SchemaDescriptor", output);
        Assert.DoesNotContain("IModelAdapter", output);

        var run = await project.Run();
        run.AssertSuccess();
        Assert.Contains("Item { id = 42 }", run.Output);
    }

    [Fact]
    public async Task PackagedBuildGeneratesDocumentedRichModelsAndSkipsUnchangedInputs()
    {
        var project = packages.CreateConsumer();
        project.Write("Schemas/item.bond", """
            namespace Contracts
            // An item <with> documentation & details.
            struct Item {
                // The item identifier.
                0: int32 id;
                1: vector<int32> values;
            }
            """);
        project.Write("Program.cs", """
            using Application;
            using Bond.IO.Safe;
            using Bond.Protocols;
            using BondTools.Models;
            var item = new Item { id = 42 };
            item.values.Add(3);
            var copy = item.Clone();
            if (ReferenceEquals(item.values, copy.values) || !item.Equals(copy))
            {
                throw new Exception("Cloning did not copy model values.");
            }

            if (ItemSchema.Descriptor.FullName != "Contracts.Item")
            {
                throw new Exception("Namespace mapping changed IDL metadata.");
            }

            var output = new OutputBuffer();
            Bond.Serialize.To(new CompactBinaryWriter<OutputBuffer>(output, 2), item);
            var decoded = Bond.Deserialize<Item>.From(new CompactBinaryReader<InputBuffer>(new InputBuffer(output.Data), 2));
            if (!item.Equals(decoded) || new GeneratedModelDebugView(item).Fields.Length != 2)
            {
                throw new Exception("Generated models did not retain runtime behavior.");
            }

            Console.WriteLine("consumer succeeded");
            """);
        project.Configure(
            new XElement("Bond", new XAttribute("Include", "Schemas/item.bond"),
                new XAttribute("Descriptors", "true"), new XAttribute("Clone", "true"),
                new XAttribute("Equality", "true"), new XAttribute("Debugger", "true")),
            new XElement("BondUsing", new XAttribute("Include", "System.Collections.Generic")),
            new XElement("BondNamespaceMapping", new XAttribute("Include", "Contracts=Application")));

        var first = await project.Build();
        first.AssertSuccess();
        var outputFile = Assert.Single(project.GeneratedFiles());
        var code = await File.ReadAllTextAsync(outputFile, TestContext.Current.CancellationToken);
        Assert.Contains("using System.Collections.Generic;", code);
        Assert.Contains("///", code);
        var documentation = XDocument.Load(project.Binary("Consumer.xml"));
        Assert.Equal("An item <with> documentation & details.",
            documentation.Descendants("member").Single(member => (string?)member.Attribute("name") == "T:Application.Item")
                .Element("summary")!.Value.Trim());

        var run = await project.Run();
        run.AssertSuccess();
        Assert.Contains("consumer succeeded", run.Output);

        var timestamp = File.GetLastWriteTimeUtc(outputFile);
        var second = await project.Build();
        second.AssertSuccess();
        Assert.Contains("up-to-date", second.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(outputFile));
        Assert.Equal(code, await File.ReadAllTextAsync(outputFile, TestContext.Current.CancellationToken));

        var program = Path.Combine(project.Root, "Program.cs");
        File.AppendAllText(program, "\nConsole.WriteLine(\"rebuilt with cached models\");\n");
        var recompiled = await project.Build();
        recompiled.AssertSuccess();
        Assert.Contains("up-to-date", recompiled.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(outputFile));
        var rerun = await project.Run();
        rerun.AssertSuccess();
        Assert.Contains("rebuilt with cached models", rerun.Output);

        File.Delete(outputFile);
        (await project.Build()).AssertSuccess();
        Assert.True(File.Exists(outputFile));
        Assert.Equal(code, await File.ReadAllTextAsync(outputFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportClosureSearchPrecedenceAndOptionsInvalidateGeneration()
    {
        var project = packages.CreateConsumer();
        project.Write("Schemas/root.bond", """
            import "wrapper.bond"
            namespace Contracts
            struct Root { 0: Value value; }
            """);
        project.Write("Imports/wrapper.bond", "import \"nested/value.bond\"\nnamespace Contracts");
        project.Write("Imports/nested/value.bond", "namespace Contracts using Value = int32;");
        project.Configure(
            new XElement("Bond", new XAttribute("Include", "Schemas/root.bond")),
            new XElement("BondImportDirectory", new XAttribute("Include", "Imports")));

        (await project.Build()).AssertSuccess();
        var generated = Assert.Single(project.GeneratedFiles());
        Assert.Contains("public int value", File.ReadAllText(generated));

        project.Write("Imports/nested/value.bond", "namespace Contracts using Value = int64;");
        var changedImport = await project.Build();
        changedImport.AssertSuccess();
        Assert.Contains("public long value", File.ReadAllText(generated));

        project.Write("Schemas/wrapper.bond", "namespace Contracts using Value = string;");
        (await project.Build()).AssertSuccess();
        Assert.Contains("public string value", File.ReadAllText(generated));

        File.Delete(Path.Combine(project.Root, "Schemas/wrapper.bond"));
        (await project.Build()).AssertSuccess();
        Assert.Contains("public long value", File.ReadAllText(generated));

        project.Configure(
            new XElement("Bond", new XAttribute("Include", "Schemas/root.bond"),
                new XAttribute("Descriptors", "true")),
            new XElement("BondImportDirectory", new XAttribute("Include", "Imports")),
            new XElement("BondUsing", new XAttribute("Include", "System.Text")));
        (await project.Build()).AssertSuccess();
        Assert.Contains("class RootSchema", File.ReadAllText(generated));
        Assert.Contains("using System.Text;", File.ReadAllText(generated));
    }

    [Fact]
    public async Task EqualBasenamesRemovalAndCleanOnlyAffectOwnedOutputs()
    {
        var project = packages.CreateConsumer();
        project.Write("One/model.bond", "namespace One struct First { 0: int32 id; }");
        project.Write("Two/model.bond", "namespace Two struct Second { 0: int32 id; }");
        project.Configure(new XElement("Bond", new XAttribute("Include", "**/*.bond")));

        (await project.Build()).AssertSuccess();
        var generated = project.GeneratedFiles();
        Assert.Equal(2, generated.Length);
        Assert.All(generated, path => Assert.StartsWith(project.Artifacts, path));

        var removedOutput = generated.Single(path => File.ReadAllText(path).Contains("class Second", StringComparison.Ordinal));
        var remainingOutput = generated.Single(path => path != removedOutput);
        File.Delete(Path.Combine(project.Root, "Two/model.bond"));
        (await project.Build()).AssertSuccess();
        Assert.False(File.Exists(removedOutput));
        Assert.Equal(remainingOutput, Assert.Single(project.GeneratedFiles()));

        project.Configure();
        (await project.Build()).AssertSuccess();
        Assert.Empty(project.GeneratedFiles());

        project.Configure(new XElement("Bond", new XAttribute("Include", "One/model.bond")));
        (await project.Build()).AssertSuccess();
        var unrelated = Path.Combine(Path.GetDirectoryName(Assert.Single(project.GeneratedFiles()))!, "keep.txt");
        File.WriteAllText(unrelated, "not owned by the generator");
        (await project.Command("clean", project.ProjectFile, "-c", "Debug", "--nologo", "--verbosity", "minimal")).AssertSuccess();
        Assert.Empty(project.GeneratedFiles());
        Assert.Equal("not owned by the generator", File.ReadAllText(unrelated));
    }

    [Theory]
    [InlineData("order%20v1.bond")]
    [InlineData("order%3Bv1.bond")]
    [InlineData("order%25v1.bond")]
    [InlineData("order;v1.bond")]
    [InlineData("order$(Name);@('item').bond")]
    public async Task GeneratedPathsPreserveLiteralMsBuildCharacters(string filename)
    {
        var project = packages.CreateConsumer();
        project.Write(filename, "namespace Contracts struct Item { 0: int32 id; }");
        project.Configure(new XElement("Bond", new XAttribute("Include", "*.bond")));

        (await project.Build()).AssertSuccess();
        var output = Assert.Single(project.GeneratedFiles());
        Assert.Equal(Path.GetFileNameWithoutExtension(filename) + ".g.cs", Path.GetFileName(output));

        var unchanged = await project.Build();
        unchanged.AssertSuccess();
        Assert.Contains("up-to-date", unchanged.Output, StringComparison.OrdinalIgnoreCase);

        (await project.Command("clean", project.ProjectFile, "-c", "Debug", "--nologo", "--verbosity", "minimal")).AssertSuccess();
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task PackagedTaskLoadsInDotNet8SdkHost()
    {
        var project = packages.CreateConsumer();
        project.Write("global.json", """
            {
              "sdk": {
                "version": "8.0.100",
                "rollForward": "latestFeature",
                "allowPrerelease": false
              }
            }
            """);
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; }");
        project.Configure(new XElement("Bond", new XAttribute("Include", "item.bond")));

        var sdk = await project.Command("--version");
        sdk.AssertSuccess();
        Assert.StartsWith("8.0.", sdk.Output.Trim());

        (await project.Build()).AssertSuccess();
        Assert.Contains("public int id", File.ReadAllText(Assert.Single(project.GeneratedFiles())));
    }

    [Fact]
    public async Task GenerationErrorsPreserveExistingOutputsAndRejectUnownedFiles()
    {
        var project = packages.CreateConsumer();
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; }");
        project.Configure(new XElement("Bond", new XAttribute("Include", "*.bond")));
        (await project.Build()).AssertSuccess();
        var output = Assert.Single(project.GeneratedFiles());
        var original = File.ReadAllText(output);
        var generationFiles = project.GenerationFiles();
        Assert.Contains(generationFiles, path => Path.GetFileName(path) == "manifest.json");
        var snapshots = generationFiles.ToDictionary(path => path,
            path => (Contents: File.ReadAllBytes(path), Timestamp: File.GetLastWriteTimeUtc(path)));

        project.Write("item.bond", "namespace Contracts struct Item { 0: string id; }");
        project.Write("invalid.bond", "namespace Contracts struct Invalid { 0: Missing value; }");

        var failure = await project.Build();
        Assert.NotEqual(0, failure.ExitCode);
        Assert.Contains("invalid.bond", failure.Output);
        Assert.Equal(original, File.ReadAllText(output));
        Assert.Single(project.GeneratedFiles());
        Assert.Equal(generationFiles, project.GenerationFiles());
        foreach (var (path, snapshot) in snapshots)
        {
            Assert.Equal(snapshot.Contents, File.ReadAllBytes(path));
            Assert.Equal(snapshot.Timestamp, File.GetLastWriteTimeUtc(path));
        }

        File.Delete(Path.Combine(project.Root, "invalid.bond"));
        File.WriteAllText(output, "user content");

        var overwrite = await project.Build();
        Assert.NotEqual(0, overwrite.ExitCode);
        Assert.Equal("user content", File.ReadAllText(output));
    }

    [Fact]
    public async Task StaleOutputOwnershipErrorsPrecedeSchemaErrorsWithoutChangingGenerationFiles()
    {
        var project = packages.CreateConsumer();
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; }");
        project.Write("stale.bond", "namespace Contracts struct Stale { 0: int32 id; }");
        project.Configure(new XElement("Bond", new XAttribute("Include", "*.bond")));
        (await project.Build()).AssertSuccess();
        var staleOutput = project.GeneratedFiles()
            .Single(path => File.ReadAllText(path).Contains("class Stale", StringComparison.Ordinal));

        File.Delete(Path.Combine(project.Root, "stale.bond"));
        File.WriteAllText(staleOutput, "user content");
        project.Write("item.bond", "namespace Contracts struct Item { 0: Missing id; }");
        var generationFiles = project.GenerationFiles();
        var snapshots = generationFiles.ToDictionary(path => path,
            path => (Contents: File.ReadAllBytes(path), Timestamp: File.GetLastWriteTimeUtc(path)));

        var failure = await project.Build();

        Assert.NotEqual(0, failure.ExitCode);
        Assert.Contains("Refusing to overwrite or remove a non-generated file", failure.Output);
        Assert.Contains(staleOutput, failure.Output);
        Assert.DoesNotContain("not found in symbol table", failure.Output);
        Assert.Equal(generationFiles, project.GenerationFiles());
        foreach (var (path, snapshot) in snapshots)
        {
            Assert.Equal(snapshot.Contents, File.ReadAllBytes(path));
            Assert.Equal(snapshot.Timestamp, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData("Version", "Invalid manifest identity or version.")]
    [InlineData("NullEntries", "Invalid manifest identity or version.")]
    [InlineData("NullEntry", "Invalid manifest output entry.")]
    [InlineData("Output", "Invalid manifest output entry.")]
    [InlineData("DuplicateSource", "Invalid manifest output entry.")]
    [InlineData("DuplicateOutput", "Invalid manifest output entry.")]
    [InlineData("NullDependencies", "Invalid manifest output entry.")]
    [InlineData("NullDependency", "Invalid manifest dependency.")]
    [InlineData("DependencyHash", "Invalid manifest dependency.")]
    [InlineData("DuplicateDependency", "Invalid manifest dependency.")]
    [InlineData("MissingRootHash", "Manifest does not include the root input hash.")]
    public async Task InvalidManifestSectionsFailWithoutChangingGenerationFiles(string field, string message)
    {
        var project = packages.CreateConsumer();
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; }");
        project.Write("other.bond", "namespace Contracts struct Other { 0: int32 id; }");
        project.Configure(new XElement("Bond", new XAttribute("Include", "*.bond")));
        (await project.Build()).AssertSuccess();
        var generationFiles = project.GenerationFiles();
        var manifestPath = generationFiles.Single(path => Path.GetFileName(path) == "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var entries = manifest["Entries"]!.AsArray();
        var dependencies = entries[0]!["Dependencies"]!.AsArray();
        switch (field)
        {
            case "Version":
                manifest["Version"] = 0;
                break;
            case "NullEntries":
                manifest["Entries"] = null;
                break;
            case "NullEntry":
                entries[0] = null;
                break;
            case "Output":
                entries[0]!["Output"] = "different.g.cs";
                break;
            case "DuplicateSource":
                entries.Add(entries[0]!.DeepClone());
                break;
            case "DuplicateOutput":
                entries[1]!["Output"] = entries[0]!["Output"]!.GetValue<string>();
                break;
            case "NullDependencies":
                entries[0]!["Dependencies"] = null;
                break;
            case "NullDependency":
                dependencies[0] = null;
                break;
            case "DependencyHash":
                dependencies[0]!["Hash"] = "not a hash";
                break;
            case "DuplicateDependency":
                dependencies.Add(dependencies[0]!.DeepClone());
                break;
            case "MissingRootHash":
                dependencies[0]!["Hash"] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }

        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var snapshots = generationFiles.ToDictionary(path => path,
            path => (Contents: File.ReadAllBytes(path), Timestamp: File.GetLastWriteTimeUtc(path)));

        var failure = await project.Build();

        Assert.NotEqual(0, failure.ExitCode);
        Assert.Contains(manifestPath, failure.Output);
        Assert.Contains($"Bond manifest '{manifestPath}' is invalid. Run dotnet clean and rebuild, " +
            $"or remove this manifest and rebuild. {message}", failure.Output);
        Assert.Equal(generationFiles, project.GenerationFiles());
        foreach (var (path, snapshot) in snapshots)
        {
            Assert.Equal(snapshot.Contents, File.ReadAllBytes(path));
            Assert.Equal(snapshot.Timestamp, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task LinkedSchemasKeepImportsRelativeAndValidateItemOptions()
    {
        var project = packages.CreateConsumer();
        var linked = Path.Combine(packages.Root, "external schemas", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(linked);
        File.WriteAllText(Path.Combine(linked, "root.bond"),
            "import \"types.bond\"\nnamespace Linked struct Item { 0: Value value; }");
        File.WriteAllText(Path.Combine(linked, "types.bond"), "namespace Linked using Value = int64;");
        var relative = Path.GetRelativePath(project.Root, Path.Combine(linked, "root.bond"));
        project.Configure(new XElement("Bond", new XAttribute("Include", relative),
            new XAttribute("Link", "Schemas/root.bond")));

        (await project.Build()).AssertSuccess();
        var output = Assert.Single(project.GeneratedFiles());
        Assert.Contains("public long value", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(linked, "*.g.cs", SearchOption.AllDirectories));
        var assets = File.ReadAllText(Path.Combine(project.Artifacts, "obj", "Consumer", "project.assets.json"));
        Assert.DoesNotContain("\"BondTools.Models/", assets);

        project.Configure(new XElement("Bond", new XAttribute("Include", relative),
            new XAttribute("Clone", "not-a-boolean")));
        var invalid = await project.Build();
        Assert.NotEqual(0, invalid.ExitCode);
        Assert.Contains("Clone", invalid.Output);
    }

    [Fact]
    public async Task DesignTimeAndTargetFrameworkBuildsHaveIndependentOutputs()
    {
        var project = packages.CreateConsumer();
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; }");
        project.Configure(new XElement("Bond", new XAttribute("Include", "item.bond")));
        (await project.Command("restore", project.ProjectFile, "--nologo", "--verbosity", "minimal")).AssertSuccess();
        (await project.Command("msbuild", project.ProjectFile, "-t:Compile", "-p:DesignTimeBuild=true",
            "-p:BuildingProject=false", "-nologo", "-verbosity:minimal")).AssertSuccess();
        Assert.Single(project.GeneratedFiles());
        (await project.Build()).AssertSuccess();

        var model = XDocument.Load(project.ProjectFile);
        model.Root!.Element("PropertyGroup")!.Element("TargetFramework")!.ReplaceWith(
            new XElement("TargetFrameworks", "net8.0;netstandard2.1"));
        model.Root.Element("PropertyGroup")!.Add(new XElement("LangVersion", "12.0"));
        model.Save(project.ProjectFile);
        (await project.Build()).AssertSuccess();
        var frameworkOutputs = project.GeneratedFiles().Where(path => path.Contains("debug_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, frameworkOutputs.Length);
        Assert.Contains(frameworkOutputs, path => path.Contains("net8.0", StringComparison.Ordinal));
        Assert.Contains(frameworkOutputs, path => path.Contains("netstandard2.1", StringComparison.Ordinal));
    }
}

public sealed class MsBuildPackageFixture : IAsyncLifetime
{
    public string Root { get; private set; } = "";
    private string _repository = "";
    private string _version = "";
    private string _runtimeVersion = "";

    public async ValueTask InitializeAsync()
    {
        _repository = TestRepository.Root;
        Root = Path.Combine(_repository, "out", "build integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        _version = File.ReadAllText(Path.Combine(_repository, "version")).Trim();
        _runtimeVersion = XDocument.Load(Path.Combine(_repository, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Single(item => (string?)item.Attribute("Include") == "Bond.Runtime.CSharp")
            .Attribute("Version")!.Value;

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name.StartsWith("release", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";
        foreach (var project in new[] { "Bond.Build", "Bond.Models" })
        {
            var result = await Execute(_repository, null, "pack",
                Path.Combine(_repository, project, project + ".csproj"), "-c", configuration, "--no-build",
                "--no-restore", "-o", Path.Combine(Root, "feed"), "--nologo", "--verbosity", "minimal");
            result.AssertSuccess();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    public Consumer CreateConsumer() => new(this, _version, _runtimeVersion);
    public string ReadExample(string path) => File.ReadAllText(Path.Combine(_repository, "examples", path));

    public sealed class Consumer
    {
        private readonly MsBuildPackageFixture _fixture;
        private readonly string _version;
        private readonly string _runtimeVersion;
        public string Root { get; }
        public string Artifacts => Path.Combine(Root, "out");
        public string ProjectFile => Path.Combine(Root, "Consumer.csproj");

        internal Consumer(MsBuildPackageFixture fixture, string version, string runtimeVersion)
        {
            _fixture = fixture;
            _version = version;
            _runtimeVersion = runtimeVersion;

            Root = Path.Combine(fixture.Root, "consumer " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            new XDocument(new XElement("Project", new XElement("PropertyGroup",
                new XElement("ArtifactsPath", Artifacts))))
                .Save(Path.Combine(Root, "Directory.Build.props"));
            Write("Directory.Packages.props", "<Project />");

            var packageSources = new XElement("packageSources",
                new XElement("clear"),
                new XElement("add", new XAttribute("key", "local"), new XAttribute("value", Path.Combine(fixture.Root, "feed"))),
                new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json")));
            var sourceMapping = new XElement("packageSourceMapping",
                new XElement("packageSource", new XAttribute("key", "local"),
                    new XElement("package", new XAttribute("pattern", "BondTools.*"))),
                new XElement("packageSource", new XAttribute("key", "nuget.org"),
                    new XElement("package", new XAttribute("pattern", "*"))));

            var nugetConfig = new XDocument(new XElement("configuration", packageSources, sourceMapping));
            nugetConfig.Save(Path.Combine(Root, "NuGet.Config"));
        }

        public void Write(string path, string content)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Configure(params XElement[] items)
        {
            var properties = new XElement("PropertyGroup",
                new XElement("TargetFramework", "net8.0"),
                new XElement("OutputType", File.Exists(Path.Combine(Root, "Program.cs")) ? "Exe" : "Library"),
                new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"),
                new XElement("GenerateDocumentationFile", "true"),
                new XElement("NoWarn", "CS1591"));
            var references = new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Include", "BondTools.Build"), new XAttribute("Version", _version),
                    new XAttribute("PrivateAssets", "all")),
                new XElement("PackageReference", new XAttribute("Include", "Bond.Runtime.CSharp"),
                    new XAttribute("Version", _runtimeVersion)));
            if (items.Any(item => item.Name == "Bond" &&
                new[] { "Descriptors", "Clone", "Equality", "Debugger", "ToString" }.Any(name => (string?)item.Attribute(name) == "true")))
            {
                references.Add(new XElement("PackageReference", new XAttribute("Include", "BondTools.Models"),
                    new XAttribute("Version", _version)));
            }

            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                properties, references, new XElement("ItemGroup", items))).Save(ProjectFile);
        }

        public string[] GeneratedFiles() =>
            GenerationFiles().Where(path => path.EndsWith(".g.cs", StringComparison.Ordinal)).ToArray();

        public string[] GenerationFiles() => Directory.Exists(Artifacts)
            ? Directory.GetFiles(Artifacts, "*", SearchOption.AllDirectories)
                .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("bond")).OrderBy(path => path).ToArray()
            : [];

        public string Binary(string filename) => Path.Combine(Artifacts, "bin", "Consumer", "debug", filename);
        public Task<CommandResult> Build() => Command("build", ProjectFile, "-c", "Debug", "--nologo", "--verbosity", "minimal");
        public Task<CommandResult> Run() => Command(Binary("Consumer.dll"));
        public Task<CommandResult> Command(params string[] args) => Execute(Root, Path.Combine(_fixture.Root, "packages"), args);
    }

    public sealed record CommandResult(int ExitCode, string Output)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0, Output);
    }

    private static async Task<CommandResult> Execute(string directory, string? packages, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
        if (packages != null)
        {
            start.Environment["NUGET_PACKAGES"] = packages;
        }

        var result = await TestProcess.Run(start, TimeSpan.FromMinutes(3));
        return new CommandResult(result.ExitCode, result.Output + result.Error);
    }
}
