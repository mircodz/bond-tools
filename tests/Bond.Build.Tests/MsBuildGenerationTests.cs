using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Bond.Build.Tests;

public sealed class MsBuildGenerationTests(MsBuildPackageFixture packages) : IClassFixture<MsBuildPackageFixture>
{
    [Fact]
    public async Task GeneratesModelsAndOnlyRewritesChangedOutputs()
    {
        var project = packages.CreateConsumer();
        project.Write("Schemas/item.bond", """
            namespace Contracts
            // An item <with> documentation & details.
            struct Item {
                0: int32 id;
                1: vector<int32> values;
            }
            """);
        project.Write("Program.cs", """
            using Application;
            using Bond.IO.Safe;
            using Bond.Protocols;
            var item = new Item { id = 42 };
            item.values.Add(3);
            var copy = item.Clone();
            if (ReferenceEquals(item.values, copy.values) || !item.Equals(copy) || item.ToString() != "Item { id = 42, values = [3] }")
            {
                throw new Exception("Model members are wrong.");
            }

            var output = new OutputBuffer();
            Bond.Serialize.To(new CompactBinaryWriter<OutputBuffer>(output, 2), item);
            var decoded = Bond.Deserialize<Item>.From(new CompactBinaryReader<InputBuffer>(new InputBuffer(output.Data), 2));
            Console.WriteLine(item.Equals(decoded) ? "consumer succeeded" : "round trip failed");
            """);
        project.Configure(
            new XElement("PropertyGroup",
                new XElement("BondClone", "true"), new XElement("BondEquality", "true"), new XElement("BondToString", "true"),
                new XElement("BondUsings", "System.Collections.Generic"),
                new XElement("BondNamespaceMappings", "Contracts=Application")),
            new XElement("ItemGroup", new XElement("Bond", new XAttribute("Include", "Schemas/item.bond"))));

        (await project.Build()).AssertSuccess();
        var output = Assert.Single(project.GeneratedFiles());
        var code = File.ReadAllText(output);
        Assert.Contains("using System.Collections.Generic;", code);
        var documentation = XDocument.Load(project.Binary("Consumer.xml"));
        Assert.Equal("An item <with> documentation & details.",
            documentation.Descendants("member").Single(member => (string?)member.Attribute("name") == "T:Application.Item")
                .Element("summary")!.Value.Trim());
        Assert.Contains("consumer succeeded", (await project.Run()).Output);

        var timestamp = File.GetLastWriteTimeUtc(output);
        File.AppendAllText(Path.Combine(project.Root, "Program.cs"), "\nConsole.WriteLine(\"rebuilt\");\n");
        (await project.Build()).AssertSuccess();
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(output));
        Assert.Contains("rebuilt", (await project.Run()).Output);

        File.Delete(output);
        (await project.Build()).AssertSuccess();
        Assert.Equal(code, File.ReadAllText(output));
    }

    [Fact]
    public async Task ImportsAreSearchedNextToTheImportingFileFirst()
    {
        var project = packages.CreateConsumer();
        project.Write("Schemas/root.bond", "import \"wrapper.bond\"\nnamespace Contracts\nstruct Root { 0: Value value; }");
        project.Write("Imports/wrapper.bond", "import \"nested/value.bond\"\nnamespace Contracts");
        project.Write("Imports/nested/value.bond", "namespace Contracts using Value = int32;");
        project.Configure(
            new XElement("PropertyGroup", new XElement("BondImportDirectories", "Imports")),
            new XElement("ItemGroup", new XElement("Bond", new XAttribute("Include", "Schemas/root.bond"))));

        (await project.Build()).AssertSuccess();
        var generated = Assert.Single(project.GeneratedFiles());
        Assert.Contains("public int value", File.ReadAllText(generated));

        project.Write("Imports/nested/value.bond", "namespace Contracts using Value = int64;");
        (await project.Build()).AssertSuccess();
        Assert.Contains("public long value", File.ReadAllText(generated));

        project.Write("Schemas/wrapper.bond", "namespace Contracts using Value = string;");
        (await project.Build()).AssertSuccess();
        Assert.Contains("public string value", File.ReadAllText(generated));
    }

    [Fact]
    public async Task RemovedSchemasAndCleanDeleteOnlyGeneratedOutputs()
    {
        var project = packages.CreateConsumer();
        project.Write("One/model.bond", "namespace One struct First { 0: int32 id; }");
        project.Write("Two/model.bond", "namespace Two struct Second { 0: int32 id; }");
        project.Write("Three/order;$(v1)@('%').bond", "namespace Three struct Third { 0: int32 id; }");
        project.Configure(new XElement("ItemGroup", new XElement("Bond", new XAttribute("Include", "**/*.bond"))));

        (await project.Build()).AssertSuccess();
        var generated = project.GeneratedFiles();
        Assert.Equal(3, generated.Length);

        var removed = generated.Single(path => File.ReadAllText(path).Contains("class Second", StringComparison.Ordinal));
        File.Delete(Path.Combine(project.Root, "Two/model.bond"));
        (await project.Build()).AssertSuccess();
        Assert.False(File.Exists(removed));
        Assert.Equal(2, project.GeneratedFiles().Length);

        var unrelated = Path.Combine(Path.GetDirectoryName(removed)!, "keep.txt");
        File.WriteAllText(unrelated, "not generated");
        (await project.Command("clean", project.ProjectFile, "-c", "Debug", "--nologo", "--verbosity", "minimal")).AssertSuccess();
        Assert.Empty(project.GeneratedFiles());
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task SchemaErrorsFailTheBuildWithSourceLocations()
    {
        var project = packages.CreateConsumer();
        project.Write("invalid.bond", "namespace Contracts\nstruct Invalid { 0: Missing value; }");
        project.Configure(new XElement("ItemGroup", new XElement("Bond", new XAttribute("Include", "invalid.bond"))));

        var result = await project.Build();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("invalid.bond(2,", result.Output);
        Assert.Contains("error BOND1001", result.Output);
    }

    [Fact]
    public async Task BuildsThroughSymbolicLinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links requires elevation on Windows.");
        var project = packages.CreateConsumer();
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; }");
        project.Configure(new XElement("ItemGroup", new XElement("Bond", new XAttribute("Include", "item.bond"))));

        var link = project.Root + " link";
        Directory.CreateSymbolicLink(link, project.Root);
        (await project.Command("build", Path.Combine(link, "Consumer.csproj"), "--nologo", "--verbosity", "minimal")).AssertSuccess();
        Assert.Single(project.GeneratedFiles());
    }

    [Fact]
    public async Task MultiTargetedProjectsGenerateForEachFramework()
    {
        var project = packages.CreateConsumer();
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; 1: map<string, vector<blob>> data; }");
        project.Configure(
            new XElement("PropertyGroup", new XElement("TargetFramework", ""), new XElement("TargetFrameworks", "net8.0;netstandard2.1"),
                new XElement("ImplicitUsings", "disable"),
                new XElement("BondClone", "true"), new XElement("BondEquality", "true"), new XElement("BondToString", "true")),
            new XElement("ItemGroup", new XElement("Bond", new XAttribute("Include", "item.bond"))));

        (await project.Build()).AssertSuccess();
        var outputs = project.GeneratedFiles();
        Assert.Equal(2, outputs.Length);
        Assert.Contains(outputs, path => path.Contains("net8.0", StringComparison.Ordinal));
        Assert.Contains(outputs, path => path.Contains("netstandard2.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PackagedTaskLoadsInDotNet8SdkHost()
    {
        var project = packages.CreateConsumer();
        project.Write("global.json", """{ "sdk": { "version": "8.0.100", "rollForward": "latestFeature" } }""");
        project.Write("item.bond", "namespace Contracts struct Item { 0: int32 id; }");
        project.Configure(new XElement("ItemGroup", new XElement("Bond", new XAttribute("Include", "item.bond"))));

        var sdk = await project.Command("--version");
        sdk.AssertSuccess();
        Assert.StartsWith("8.0.", sdk.Output.Trim());
        (await project.Build()).AssertSuccess();
        Assert.Contains("public int id", File.ReadAllText(Assert.Single(project.GeneratedFiles())));
    }
}

public sealed class MsBuildPackageFixture : IAsyncLifetime
{
    public string Root { get; private set; } = "";
    private string _version = "";
    private string _runtimeVersion = "";

    public async ValueTask InitializeAsync()
    {
        var repository = TestRepository.Root;
        Root = Path.Combine(repository, "out", "build integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        _version = File.ReadAllText(Path.Combine(repository, "version")).Trim();
        _runtimeVersion = XDocument.Load(Path.Combine(repository, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Single(item => (string?)item.Attribute("Include") == "Bond.Runtime.CSharp")
            .Attribute("Version")!.Value;

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name.StartsWith("release", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";
        var result = await Execute(repository, null, "pack", Path.Combine(repository, "Bond.Build", "Bond.Build.csproj"),
            "-c", configuration, "--no-build", "--no-restore", "-o", Path.Combine(Root, "feed"), "--nologo", "--verbosity", "minimal");
        result.AssertSuccess();
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
            // Relative to the project, so builds through a symbolic link also write through it.
            new XDocument(new XElement("Project", new XElement("PropertyGroup",
                new XElement("ArtifactsPath", "$(MSBuildThisFileDirectory)out"))))
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
            new XDocument(new XElement("configuration", packageSources, sourceMapping)).Save(Path.Combine(Root, "NuGet.Config"));
        }

        public void Write(string path, string content)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Configure(params XElement[] groups)
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
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), properties, references, groups))
                .Save(ProjectFile);
        }

        public string[] GeneratedFiles() => Directory.Exists(Artifacts)
            ? Directory.GetFiles(Artifacts, "*.g.cs", SearchOption.AllDirectories)
                .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("bond")).Order().ToArray()
            : [];

        public string Binary(string filename) => Path.Combine(Artifacts, "bin", "Consumer", "debug", filename);
        public Task<CommandResult> Build() => Command("build", ProjectFile, "-c", "Debug", "--nologo", "--verbosity", "minimal");

        public async Task<CommandResult> Run()
        {
            var result = await Command(Binary("Consumer.dll"));
            result.AssertSuccess();
            return result;
        }

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
