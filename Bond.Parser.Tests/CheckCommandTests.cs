using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.CLI;

namespace Bond.Parser.Tests;

public sealed class CheckCommandTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        "check-command-tests", Guid.NewGuid().ToString("N")));

    public CheckCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ValidSchemasProduceNoOutputOrFiles()
    {
        const string schema = "namespace Example struct Item { 0: int32 id; }";
        var input = Write("item.bond", schema);
        var timestamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(input, timestamp);

        var result = await Run(input);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Empty(result.Error);
        Assert.Equal(schema, File.ReadAllText(input));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(input));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("namespace Example struct Item { 0: int32 id }")]
    [InlineData("namespace Example struct Item { 0: Missing id; }")]
    [InlineData("namespace Example struct Item { 0: int32 id; } @")]
    [InlineData("namespace Example struct Item { 0: int32 id; 0: string name; }")]
    public async Task InvalidSchemasReportLocationsWithoutDumpingTheAst(string schema)
    {
        var input = Write("item.bond", schema);
        var result = await Run(input, "--error-format=json");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        using var json = JsonDocument.Parse(result.Error);
        var error = json.RootElement.GetProperty("errors")[0];
        Assert.Equal(input, error.GetProperty("file").GetString());
        Assert.True(error.GetProperty("line").GetInt32() > 0);
        Assert.True(error.GetProperty("column").GetInt32() > 0);
        Assert.False(json.RootElement.TryGetProperty("declarations", out _));
    }

    [Fact]
    public async Task ChecksTransitiveImportsUsingTheSameSearchOrderAsOtherCommands()
    {
        var input = Write("schemas/root.bond",
            "import \"shared.bond\"\nnamespace Example struct Root { 0: Imported value; }");
        Write("imports/shared.bond",
            "import \"nested/value.bond\"\nnamespace Example struct Imported { 0: Value value; }");
        var leaf = Write("imports/nested/value.bond", "namespace Example using Value = int32;");
        var imports = Path.Combine(_root, "imports");

        Assert.Equal(0, (await Run("-I", Path.Combine(_root, "missing"), input, "--import-dir=" + imports)).ExitCode);

        Write("imports/nested/value.bond", "namespace Example using Value = Missing;");
        var invalid = await Run(input, "-I=" + imports, "--error-format", "json");

        Assert.Equal(1, invalid.ExitCode);
        using var json = JsonDocument.Parse(invalid.Error);
        Assert.Equal(leaf, json.RootElement.GetProperty("errors")[0].GetProperty("file").GetString());

        Write("schemas/shared.bond", "namespace Example struct Imported {}");
        Assert.Equal(0, (await Run(input, "-I", imports)).ExitCode);
    }

    [Fact]
    public async Task MissingSchemasAndImportsAreInvalid()
    {
        Assert.Equal(1, (await Run(Path.Combine(_root, "missing.bond"))).ExitCode);

        var input = Write("root.bond", "import \"missing.bond\"\nnamespace Example struct Root {}");
        var missing = await Run(input);

        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("missing.bond", missing.Error);
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--verbose")]
    [InlineData("--ignore-imports")]
    [InlineData("--against=other.bond")]
    [InlineData("--suppress=BOND0002")]
    [InlineData("--list-rules")]
    [InlineData("--import-dir=")]
    [InlineData("--error-format=yaml")]
    public async Task RejectsUnsupportedAndMalformedOptions(string option)
    {
        var input = Write("item.bond", "namespace Example struct Item {}");
        var result = await Run(input, option);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task RequiresOneInputAndDoesNotConsumeTheNextOptionAsAValue()
    {
        Assert.Equal(2, (await Run()).ExitCode);
        Assert.Equal(2, (await Run("one.bond", "two.bond")).ExitCode);

        var result = await Run("-I", "--error-format=json");

        Assert.Equal(2, result.ExitCode);
        using var json = JsonDocument.Parse(result.Error);
        Assert.Contains("requires a value", result.Error);
    }

    [Fact]
    public async Task HelpDescribesValidationNotAstOutput()
    {
        var result = await Run("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("bond check", result.Output);
        Assert.Contains("syntax, types, and imports", result.Output);
        Assert.DoesNotContain("--json", result.Output);
        Assert.DoesNotContain("--ignore-imports", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task ProgramDispatchesCheckAndRejectsTheRemovedParseCommand()
    {
        var input = Write("item.bond", "namespace Example struct Item {}");
        var check = await RunProgram("check", input);

        Assert.Equal(0, check.ExitCode);
        Assert.Empty(check.Output);
        Assert.Empty(check.Error);

        var help = await RunProgram("--help");
        Assert.Contains("bond check", help.Output);
        Assert.DoesNotContain("bond parse", help.Output);

        Assert.Equal(1, (await RunProgram("parse", input)).ExitCode);
        var oldHelp = await RunProgram("parse", "--help");
        Assert.Equal(1, oldHelp.ExitCode);
        Assert.Contains("unknown command 'parse'", oldHelp.Error);

        Assert.Equal(0, (await RunProgram("format", "--help")).ExitCode);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<CommandResult> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CheckCommand.RunAsync(args, output, error, TestContext.Current.CancellationToken);
        return new(exitCode, output.ToString(), error.ToString());
    }

    private static async Task<CommandResult> RunProgram(params string[] args)
    {
        var tests = typeof(CheckCommandTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(tests, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(tests, ".deps.json"), typeof(Program).Assembly.Location
        })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new(process.ExitCode, await output, await error);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
