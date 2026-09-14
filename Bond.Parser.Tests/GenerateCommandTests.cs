using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.CLI;
using Bond.Parser.CodeGeneration;

namespace Bond.Parser.Tests;

public sealed class GenerateCommandTests : IDisposable
{
    private const string OrderSchema = "namespace Example struct Order { 0: int32 Id; }";
    private readonly string _root;
    private string OutputDirectory => Path.Combine(_root, "generated output");

    public GenerateCommandTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }
        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the repository for CLI test artifacts.");
        }
        _root = Path.Combine(directory.FullName, "out", "generate-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("-o", false)]
    [InlineData("-o", true)]
    [InlineData("--output-dir", false)]
    [InlineData("--output-dir", true)]
    public async Task GeneratesOneFilePerExplicitInput_WithAliasesEqualsAndSpaces(string option, bool equals)
    {
        var order = Write("schema files/order = one.bond", OrderSchema);
        var item = Write("schema files/item.bond", "namespace Example struct Item { 0: string Name; }");
        var args = new List<string> { "csharp", order };
        args.AddRange(equals ? [option + "=" + OutputDirectory] : new[] { option, OutputDirectory });
        args.Add(item);

        var result = await Run(args.ToArray());

        AssertSuccess(result);
        var paths = Directory.GetFiles(OutputDirectory).OrderBy(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "item.g.cs", "order = one.g.cs" }, paths.Select(Path.GetFileName));
        foreach (var path in paths)
        {
            Assert.StartsWith(CSharpGenerator.GeneratedHeader + "\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Contains(path, result.Output);
        }
    }

    public static IEnumerable<object[]> InvalidArguments =>
    [
        [Array.Empty<string>(), "language is required"],
        [new[] { "csharp", "-o", "OUTPUT" }, "input file is required"],
        [new[] { "csharp", "INPUT" }, "'--output-dir' is required"],
        [new[] { "rust", "INPUT", "-o", "OUTPUT" }, "Unsupported language"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "--wat" }, "Unknown option"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "--protocol", "compact" }, "Unknown option"],
        [new[] { "csharp", "INPUT", "-o" }, "requires a value"],
        [new[] { "csharp", "INPUT", "--output-dir=" }, "requires a non-empty value"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "-I" }, "requires a value"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "--import-dir=" }, "requires a non-empty value"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "--error-format" }, "requires a value"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "--error-format=" }, "requires a non-empty value"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "--error-format=yaml" }, "Unsupported error format"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "--output-dir=OUTPUT" }, "may only be specified once"],
        [new[] { "csharp", "INPUT", "-o", "OUTPUT", "--error-format=text", "--error-format=text" }, "may only be specified once"],
        [new[] { "csharp", "INPUT", "-o", "--help" }, "requires a value"]
    ];

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task RejectsInvalidArgumentsWithoutWriting(string[] arguments, string expected)
    {
        var input = Write("order.bond", OrderSchema);
        var args = arguments.Select(a => a.Replace("INPUT", input).Replace("OUTPUT", OutputDirectory)).ToArray();

        var result = await Run(args);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains(expected, result.Error);
        Assert.Contains("bond(0,0): error BOND1001:", result.Error);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArgumentErrorsHonorJsonFormatEvenAfterUnknownOption(bool equals)
    {
        var args = new List<string> { "csharp", "--unknown" };
        args.AddRange(equals ? ["--error-format=json"] : new[] { "--error-format", "json" });

        var result = await Run(args.ToArray());

        using var document = AssertJsonError(result);
        Assert.Contains(document.RootElement.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("message").GetString()!.Contains("Unknown option", StringComparison.Ordinal));
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task MissingOptionValueDoesNotConsumeFollowingOption()
    {
        var result = await Run("csharp", "-o", "--error-format=json");

        using var document = AssertJsonError(result);
        Assert.Contains(document.RootElement.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("message").GetString()!.Contains("requires a value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public async Task ScopedHelpIsSuccessfulAndDocumentsModelOnlyRequirements(string help)
    {
        var general = await Run(help);
        AssertSuccess(general);
        Assert.Contains("Usage: bond generate csharp", general.Output);

        var csharp = await Run("csharp", help);
        AssertSuccess(csharp);
        Assert.Contains("model-only", csharp.Output);
        Assert.Contains("Bond.Runtime.CSharp", csharp.Output);
        Assert.Contains("--output-dir", csharp.Output);
        Assert.Contains("--import-dir", csharp.Output);
        Assert.Contains("--error-format", csharp.Output);
        Assert.Contains("generics, inheritance, views, aliases", csharp.Output);
        Assert.Contains("Services produce no C# RPC types", csharp.Output);
        Assert.DoesNotContain("--protocol", csharp.Output);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task UnsupportedLanguageIsNotHiddenByHelp()
    {
        var result = await Run("java", "--help");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Unsupported language", result.Error);
    }

    [Fact]
    public async Task SeparatorAcceptsInputsAfterOptions()
    {
        var input = Write("-order.bond", OrderSchema);

        var result = await Run("csharp", "-o", OutputDirectory, "--", input);

        AssertSuccess(result);
        Assert.True(File.Exists(Path.Combine(OutputDirectory, "-order.g.cs")));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--unknown.bond")]
    public async Task SeparatorStopsOptionAndHelpParsing(string input)
    {
        var result = await Run("csharp", "-o", OutputDirectory, "--", input);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.DoesNotContain("Unknown option", result.Error);
        Assert.DoesNotContain("Usage:", result.Error);
        Assert.Contains("error BOND1001:", result.Error);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task RequiresBondExtension()
    {
        var input = Write("order.txt", OrderSchema);

        var result = await Run("csharp", input, "-o", OutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(".bond", result.Error);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Theory]
    [InlineData("namespace Example struct Broken { 0: int32 Value }")]
    [InlineData("namespace Example struct Broken { 0: Missing Value; }")]
    [InlineData("namespace Example struct Broken { 0: int32 Value; } @")]
    public async Task PreflightsAllSelectedSchemasBeforeCreatingOutput(string badSchema)
    {
        var good = Write("order.bond", OrderSchema);
        var bad = Write("bad.bond", badSchema);

        var result = await Run("csharp", good, bad, "-o", OutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("error BOND1001:", result.Error);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task FailedLaterInputDoesNotModifyExistingGeneratedOutput()
    {
        var good = Write("order.bond", OrderSchema);
        var bad = Write("bad.bond", "namespace Example struct Broken {");
        var output = Write("generated output/order.g.cs", CSharpGenerator.GeneratedHeader + "\nprevious version\n");
        var previous = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);

        var result = await Run("csharp", good, bad, "-o", OutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(previous, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(OutputDirectory));
    }

    [Fact]
    public async Task MissingLaterInputDoesNotWriteAnyOutputs()
    {
        var good = Write("order.bond", OrderSchema);
        var missing = Path.Combine(_root, "missing.bond");

        var result = await Run("csharp", good, missing, "-o", OutputDirectory, "--error-format=json");

        using var document = AssertJsonError(result);
        Assert.Equal(missing, document.RootElement.GetProperty("errors")[0].GetProperty("file").GetString());
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Theory]
    [InlineData("order.bond")]
    [InlineData("ORDER.bond")]
    public async Task RejectsCaseInsensitiveOutputNameCollisions(string secondName)
    {
        var first = Write("first/order.bond", OrderSchema);
        var second = Write("second/" + secondName, OrderSchema);

        var result = await Run("csharp", first, second, "-o", OutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("collision", result.Error);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task RepeatedExplicitInputIsACollision()
    {
        var input = Write("order.bond", OrderSchema);

        var result = await Run("csharp", input, input, "-o", OutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("collision", result.Error);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task RejectsExistingCaseInsensitiveNameCollision()
    {
        var input = Write("order.bond", OrderSchema);
        var existing = Write("generated output/ORDER.g.cs", CSharpGenerator.GeneratedHeader + "\nold\n");

        var result = await Run("csharp", input, "-o", OutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("collision", result.Error);
        Assert.Equal(CSharpGenerator.GeneratedHeader + "\nold\n", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("// handwritten\nclass Item {}\n")]
    [InlineData("// <auto-generated by bond-tools /> but not the marker line\n")]
    [InlineData("")]
    public async Task RefusesNonGeneratedFilesBeforeWritingOtherOutputs(string existingCode)
    {
        var order = Write("order.bond", OrderSchema);
        var item = Write("item.bond", "namespace Example struct Item {}");
        var existing = Write("generated output/item.g.cs", existingCode);

        var result = await Run("csharp", order, item, "-o", OutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("non-generated", result.Error);
        Assert.Equal(existingCode, await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(OutputDirectory, "order.g.cs")));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ReplacesPreviouslyGeneratedFiles(string newline)
    {
        var input = Write("order.bond", OrderSchema);
        var path = Write("generated output/order.g.cs", CSharpGenerator.GeneratedHeader + newline + "old");

        var result = await Run("csharp", input, "-o", OutputDirectory);

        AssertSuccess(result);
        Assert.Matches(@"\bclass\s+@?Order\b", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OutputIsDeterministicAndIdenticalFilesKeepTheirTimestamp()
    {
        var input = Write("order.bond", OrderSchema);
        AssertSuccess(await Run("csharp", input, "-o", OutputDirectory));
        var path = Path.Combine(OutputDirectory, "order.g.cs");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);

        AssertSuccess(await Run("csharp", input, "-o", OutputDirectory));

        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        var secondDirectory = Path.Combine(_root, "second output");
        AssertSuccess(await Run("csharp", input, "-o", secondDirectory));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(secondDirectory, "order.g.cs"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("\r", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OutputDirectoryEntriesAreValidatedBeforeAnyWrites()
    {
        var order = Write("order.bond", OrderSchema);
        var item = Write("item.bond", "namespace Example struct Item {}");
        Directory.CreateDirectory(Path.Combine(OutputDirectory, "item.g.cs"));

        var result = await Run("csharp", order, item, "-o", OutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("directory", result.Error);
        Assert.False(File.Exists(Path.Combine(OutputDirectory, "order.g.cs")));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    public async Task OutputIoFailuresAreReported(string format)
    {
        var input = Write("order.bond", OrderSchema);
        var blocker = Write("blocked", "keep");
        var output = Path.Combine(blocker, "generated");

        var result = await Run("csharp", input, "-o", output, "--error-format", format);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Equal("keep", await File.ReadAllTextAsync(blocker, TestContext.Current.CancellationToken));
        if (format == "json")
        {
            using var document = AssertJsonError(result);
            Assert.Equal(output, document.RootElement.GetProperty("errors")[0].GetProperty("file").GetString());
        }
        else
        {
            Assert.Contains(output + "(0,0): error BOND1001:", result.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportsPreferLocalFilesThenSearchDirectoriesInOrder(bool local)
    {
        var root = Write("schemas/order.bond",
            "import \"shared.bond\"\nnamespace Example struct Order { 0: Dependency.Shared Item; }");
        var first = Path.Combine(_root, "first imports");
        var second = Path.Combine(_root, "second imports");
        Write("first imports/shared.bond", local ? "invalid" : "namespace Dependency struct Shared {}");
        Write("second imports/shared.bond", "invalid");
        if (local)
        {
            Write("schemas/shared.bond", "namespace Dependency struct Shared {}");
        }

        var result = await Run("csharp", root, "-o", OutputDirectory, "-I", first, "--import-dir=" + second);

        AssertSuccess(result);
        Assert.Single(Directory.GetFiles(OutputDirectory));
        Assert.DoesNotMatch(@"\bclass\s+@?Shared\b", await File.ReadAllTextAsync(Path.Combine(OutputDirectory, "order.g.cs"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RepeatedImportDirectoriesCanFallBackToLaterDirectory()
    {
        var root = Write("schemas/order.bond",
            "import \"shared.bond\"\nnamespace Example struct Order { 0: Dependency.Shared Item; }");
        var missing = Path.Combine(_root, "missing imports");
        Write("available imports/shared.bond", "namespace Dependency struct Shared {}");

        var result = await Run("csharp", root, "-o", OutputDirectory,
            "--import-dir", missing, "-I=" + Path.Combine(_root, "available imports"));

        AssertSuccess(result);
        Assert.Single(Directory.GetFiles(OutputDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecursiveImportsAreRelativeToTheirOwnFilesAndOnlyExplicitRootsAreGenerated(bool selectImports)
    {
        var root = Write("schemas/order.bond",
            "import \"types/shared.bond\"\nnamespace Example struct Order { 0: Dependency.Shared Item; }");
        var shared = Write("imports/types/shared.bond",
            "import \"nested/leaf.bond\"\nnamespace Dependency struct Shared { 0: Dependency.Leaf Item; }");
        var leaf = Write("imports/types/nested/leaf.bond", "namespace Dependency struct Leaf {}");
        Write("imports/nested/leaf.bond", "invalid");
        var args = new List<string> { "csharp", root, "-o", OutputDirectory, "-I", Path.Combine(_root, "imports") };
        if (selectImports) args.AddRange([shared, leaf]);

        var result = await Run(args.ToArray());

        AssertSuccess(result);
        Assert.Equal(selectImports ? 3 : 1, Directory.GetFiles(OutputDirectory).Length);
        var rootCode = await File.ReadAllTextAsync(Path.Combine(OutputDirectory, "order.g.cs"), TestContext.Current.CancellationToken);
        Assert.DoesNotMatch(@"\bclass\s+@?Shared\b", rootCode);
        Assert.DoesNotMatch(@"\bclass\s+@?Leaf\b", rootCode);
        if (selectImports)
        {
            Assert.True(File.Exists(Path.Combine(OutputDirectory, "shared.g.cs")));
            Assert.True(File.Exists(Path.Combine(OutputDirectory, "leaf.g.cs")));
        }
    }

    [Fact]
    public async Task InvalidEarlierImportIsNotSilentlyReplacedByLaterSearchDirectory()
    {
        var root = Write("schemas/order.bond", "import \"shared.bond\"\n" + OrderSchema);
        Write("first/shared.bond", "invalid");
        Write("second/shared.bond", "namespace Dependency struct Shared {}");

        var result = await Run("csharp", root, "-o", OutputDirectory,
            "-I", Path.Combine(_root, "first"), "-I", Path.Combine(_root, "second"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task MissingImportsFailWithoutGeneratingEarlierRoots()
    {
        var good = Write("order.bond", OrderSchema);
        var bad = Write("bad.bond", "import \"absent.bond\"\nnamespace Example struct Bad {}");

        var result = await Run("csharp", good, bad, "-o", OutputDirectory, "--error-format=json");

        using var document = AssertJsonError(result);
        Assert.Contains("absent.bond", result.Error);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Theory]
    [InlineData("namespace Dependency struct Bad {} @")]
    [InlineData("namespace Dependency struct Bad {")]
    public async Task ImportedSyntaxErrorsHaveStructuredFileLocations(string schema)
    {
        var root = Write("order.bond", "import \"bad.bond\"\n" + OrderSchema);
        var imported = Write("bad.bond", schema);

        var result = await Run("csharp", root, "-o", OutputDirectory, "--error-format=json");

        using var document = AssertJsonError(result);
        var error = document.RootElement.GetProperty("errors")[0];
        Assert.Equal(imported, error.GetProperty("file").GetString());
        Assert.Equal(1, error.GetProperty("line").GetInt32());
        Assert.True(error.GetProperty("column").GetInt32() > 0);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task UnrepresentableGenerationUsesTheSameJsonDiagnosticShape()
    {
        var input = Write("invalid.bond", "namespace Example struct Same { 0: int32 Same; }");

        var result = await Run("csharp", input, "-o", OutputDirectory, "--error-format=json");

        using var document = AssertJsonError(result);
        Assert.Equal(input, document.RootElement.GetProperty("errors")[0].GetProperty("file").GetString());
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Fact]
    public async Task ServicesDoNotBlockGeneratingTheirDataModels()
    {
        var input = Write("service.bond",
            "namespace Example struct Request { 0: int32 id; } service Api { void Ping(Request); }");
        var result = await Run("csharp", input, "-o", OutputDirectory);
        AssertSuccess(result);
        var code = await File.ReadAllTextAsync(Path.Combine(OutputDirectory, "service.g.cs"),
            TestContext.Current.CancellationToken);
        Assert.Contains("class Request", code);
        Assert.DoesNotContain("class Api", code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonParseDiagnosticsAreOnlyOnStderrWithConsistentFields(bool equals)
    {
        var input = Write("invalid.bond", "namespace Example\nstruct Broken {\n  0: int32 Id\n}");
        var args = new List<string> { "csharp", input, "-o", OutputDirectory };
        args.AddRange(equals ? ["--error-format=json"] : new[] { "--error-format", "json" });

        var result = await Run(args.ToArray());

        using var document = AssertJsonError(result);
        var error = document.RootElement.GetProperty("errors")[0];
        Assert.Equal(input, error.GetProperty("file").GetString());
        Assert.True(error.GetProperty("line").GetInt32() > 0);
        Assert.True(error.GetProperty("column").GetInt32() >= 0);
        Assert.False(Directory.Exists(OutputDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitTextFormatUsesCompilerStyleDiagnostics(bool equals)
    {
        var input = Write("invalid.bond", "namespace Example struct Broken {");
        var args = new List<string> { "csharp", input, "-o", OutputDirectory };
        args.AddRange(equals ? ["--error-format=text"] : new[] { "--error-format", "text" });

        var result = await Run(args.ToArray());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(input) + @"\(\d+,\d+\): error BOND1001: .+", result.Error);
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await GenerateCommand.RunAsync(args, stdout, stderr, TestContext.Current.CancellationToken);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static void AssertSuccess((int ExitCode, string Output, string Error) result)
    {
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Empty(result.Error);
    }

    private static JsonDocument AssertJsonError((int ExitCode, string Output, string Error) result)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        var document = JsonDocument.Parse(result.Error);
        var root = document.RootElement;
        Assert.Equal(new[] { "error", "errors", "message" }, root.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("error").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("message").GetString()));
        Assert.NotEmpty(root.GetProperty("errors").EnumerateArray());
        foreach (var error in root.GetProperty("errors").EnumerateArray())
        {
            Assert.Equal(new[] { "column", "file", "line", "message" }, error.EnumerateObject().Select(p => p.Name).Order());
            Assert.False(string.IsNullOrEmpty(error.GetProperty("file").GetString()));
            Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
            Assert.True(error.GetProperty("line").GetInt32() >= 0);
            Assert.True(error.GetProperty("column").GetInt32() >= 0);
        }
        return document;
    }
}
