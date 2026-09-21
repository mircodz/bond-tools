using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.CLI;

namespace Bond.Parser.Tests;

public sealed class CompatibilityCommandTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        "compatibility-command-tests", Guid.NewGuid().ToString("N")));

    public CompatibilityCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task RuleIdsAreDiscoverableWithoutInputSchemas()
    {
        var result = await Breaking("--list-rules");

        AssertSuccess(result);
        Assert.Contains("BOND0002", result.Output);
        Assert.Contains("BOND0301", result.Output);
        Assert.DoesNotContain("BOND0001", result.Output);
        Assert.DoesNotContain("BOND0302", result.Output);
        Assert.DoesNotContain("BOND0401", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task HelpExposesOnlyTheFixedProtocolScope()
    {
        var result = await Breaking("--help");

        AssertSuccess(result);
        Assert.Contains("Compact/Fast Binary and Simple JSON", result.Output);
        Assert.DoesNotContain("--protocols", result.Output);
        Assert.DoesNotContain("--ignore-source-changes", result.Output);
    }

    [Fact]
    public async Task CSharpNamespacesAndXmlAttributesDoNotAffectTheVerdict()
    {
        var old = Write("old.bond", """
            namespace Contracts
            namespace csharp Old.Api
            [xmlns("urn:old")]
            struct Item { 0: int32 id; }
            """);
        var current = Write("new.bond", """
            namespace Contracts
            namespace csharp New.Api
            [xmlns("urn:new")]
            struct Item { 0: int32 id; }
            """);

        AssertSuccess(await Breaking(current, "--against", old));
    }

    [Fact]
    public async Task UnusedGenericArgumentsDoNotIntroduceApiOnlyFailures()
    {
        var old = Write("old.bond", """
            namespace Example
            struct Box<T> { 0: int32 value; }
            struct Root { 0: Box<string> box; }
            """);
        var current = Write("new.bond", """
            namespace Example
            struct Box<T, U : value> { 0: int32 value; }
            struct Root { 0: Box<int32, bool> box; }
            """);

        AssertSuccess(await Breaking(current, "--against", old));
    }

    [Fact]
    public async Task ComparisonsNeverModifyEitherSchema()
    {
        const string previous = "namespace Example struct Item { 0: int32 id; 7: string name; }";
        const string current = "namespace Example struct Item { 0: int32 id; }";
        var old = Write("old.bond", previous);
        var input = Write("item.bond", current);
        var timestamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(old, timestamp);
        File.SetLastWriteTimeUtc(input, timestamp);

        AssertSuccess(await Breaking(input, "--against", old));

        Assert.Equal(previous, File.ReadAllText(old));
        Assert.Equal(current, File.ReadAllText(input));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(old));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(input));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public async Task SuppressionAndVerdictAreExplicit()
    {
        var old = Write("old.bond", "namespace Example struct Item { 0: int32 id; }");
        var current = Write("new.bond", "namespace Example struct Item { 0: int32 id; 1: string label; }");
        AssertSuccess(await Breaking("--against", old, current));

        Write("new.bond", "namespace Example struct Item { 0: int32 id; 1: required string label; }");
        var breaking = await Breaking(current, "--against", old, "--error-format=json");

        Assert.Equal(1, breaking.ExitCode);
        using var failed = JsonDocument.Parse(breaking.Output);
        Assert.False(failed.RootElement.GetProperty("compatible").GetBoolean());
        Assert.Equal(1, failed.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Contains(failed.RootElement.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("id").GetString() == "BOND0102");

        var suppressed = await Breaking(current, "--against", old,
            "--suppress", "BOND0102", "--error-format=json");

        Assert.Equal(0, suppressed.ExitCode);
        using var accepted = JsonDocument.Parse(suppressed.Output);
        Assert.True(accepted.RootElement.GetProperty("compatible").GetBoolean());
        Assert.Contains(accepted.RootElement.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("id").GetString() == "BOND0102" && change.GetProperty("suppressed").GetBoolean());

        Assert.Equal(2, (await Breaking(current, "--against", old, "--suppress=BOND9999")).ExitCode);
    }

    [Fact]
    public async Task PinnedJsonNamesAllowRenamesWhileChangedJsonNamesBreak()
    {
        var old = Write("old.bond", """
            namespace Example struct Item { [JsonName("wire_name")] 0: string name; }
            """);
        var current = Write("new.bond", """
            namespace Example struct Item { [JsonName("wire_name")] 0: string label; }
            """);
        AssertSuccess(await Breaking(current, "--against", old));

        Write("new.bond", """
            namespace Example struct Item { [JsonName("new_name")] 0: string name; }
            """);
        var changed = await Breaking(current, "--against", old, "--error-format=json");

        Assert.Equal(1, changed.ExitCode);
        using var result = JsonDocument.Parse(changed.Output);
        Assert.Contains(result.RootElement.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("id").GetString() == "BOND0301");
    }

    [Fact]
    public async Task CommentsAliasRenamesAndFieldOrderingDoNotChangeTheWireContract()
    {
        var previous = Write("old.bond", """
            namespace Example
            using Id = int64;
            struct Item { 1: string name; 0: Id id; }
            """);
        var current = Write("new.bond", """
            namespace Example
            using Identifier = int64;
            // The same wire contract.
            struct Item {
                0: Identifier id;
                1: string name;
            }
            """);

        AssertSuccess(await Breaking(current, "--against", previous));
    }

    [Fact]
    public async Task GitComparisonUsesHistoricalImportedFiles()
    {
        var input = Write("schema files/root.bond", """
            import "shared.bond"
            namespace Example struct Root { 0: Item item; }
            """);
        Write("schema files/shared.bond", "namespace Example struct Item { 0: int32 value; }");
        await CommitReference();

        Write("schema files/shared.bond", "namespace Example struct Item { 0: string value; }");
        var result = await Breaking(input, "--against", ".git#branch=main", "--error-format=json");

        Assert.Equal(1, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Contains(json.RootElement.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("id").GetString() == "BOND0002");
    }

    [Fact]
    public async Task GitComparisonDoesNotReplaceMissingHistoricalImportsWithWorkingFiles()
    {
        var input = Write("root.bond", "import \"missing.bond\"\nnamespace Example struct Root {}");
        await CommitReference();

        Write("missing.bond", "namespace Example struct Imported {}");
        var result = await Breaking(input, "--against", ".git#branch=main", "--error-format=json");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing.bond", result.Error);
    }

    [Fact]
    public async Task MissingGitReturnsStructuredDiagnostics()
    {
        var input = Write("root.bond", "namespace Example struct Root {}");
        var emptyPath = Path.Combine(_root, "empty-path");
        Directory.CreateDirectory(emptyPath);
        var assembly = typeof(CompatibilityCommandTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["PATH"] = emptyPath;

        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(Program).Assembly.Location,
            "breaking", input, "--against", ".git#branch=HEAD", "--error-format=json"
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, process.ExitCode);
        Assert.Empty(await output);
        using var json = JsonDocument.Parse(await error);
        Assert.Equal("schema_error", json.RootElement.GetProperty("error").GetString());
        Assert.Contains("Could not start git",
            json.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("--protocols=unsupported")]
    [InlineData("--protocols=,,,")]
    [InlineData("--protocols=compact")]
    [InlineData("--protocols=simple")]
    [InlineData("--protocols=xml")]
    [InlineData("--ignore-source-changes")]
    [InlineData("--suppress=;;;")]
    [InlineData("--unknown")]
    public async Task InvalidOptionsFailWithStructuredDiagnostics(string option)
    {
        var input = Write("item.bond", "namespace Example struct Item {}");
        var result = await Breaking(input, "--against", input, option, "--error-format=json");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        using var json = JsonDocument.Parse(result.Error);
        Assert.Equal("schema_error", json.RootElement.GetProperty("error").GetString());
    }

    private async Task CommitReference()
    {
        await Git("init", "-b", "main");
        await Git("add", ".");
        await Git("-c", "user.name=Schema Tests", "-c", "user.email=tests@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-m",
            "schema reference\n\nCo-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>");
    }

    private async Task Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static Task<CommandResult> Breaking(params string[] args) =>
        Run((output, error) => BreakingCommand.RunAsync(args, output, error, TestContext.Current.CancellationToken));

    private static async Task<CommandResult> Run(Func<TextWriter, TextWriter, Task<int>> command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await command(output, error);
        return new CommandResult(code, output.ToString(), error.ToString());
    }

    private static void AssertSuccess(CommandResult result) => Assert.True(result.ExitCode == 0, result.Error + result.Output);
    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
