using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bond.Parser.Compatibility;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

public static class BreakingCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter standardOutput, TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        var options = SchemaCommandOptions.Parse(args, comparison: true);
        if (options.Errors.Count != 0)
            return await SchemaCommandOptions.WriteErrors(standardError, options.ErrorFormat,
                options.Errors.Select(error => new ParseError(error, null, 0, 0)));
        if (options.Help)
        {
            await standardOutput.WriteLineAsync("""
                Usage: bond breaking <schema.bond> --against <schema.bond|.git#branch=name>

                Checks Compact/Fast Binary and Simple JSON compatibility.

                --suppress <IDs>          Acknowledge diagnostic IDs (repeatable or comma-separated)
                --list-rules              List diagnostic IDs
                --ignore-imports          Compare root declarations without resolving imports
                -I, --import-dir <path>   Import search directory (repeatable)
                --error-format text|json
                -v, --verbose            Include compatible and suppressed changes

                Exit codes: 0 no unsuppressed errors, 1 incompatible, 2 invalid input or usage.
                """);
            return 0;
        }
        if (options.ListRules)
        {
            foreach (var rule in DiagnosticIds.Rules.OrderBy(rule => rule.Key, StringComparer.Ordinal))
                await standardOutput.WriteLineAsync($"{rule.Key}  {rule.Value}");
            return 0;
        }
        var currentPath = options.Input!;
        try
        {
            var compatibility = options.CreateCompatibilityOptions();
            var input = Path.GetFullPath(options.Input!);
            var parseOptions = new ParseOptions(IgnoreImports: options.IgnoreImports);
            var resolver = SchemaFiles.ImportResolver(options.ImportDirectories, cancellationToken);
            var current = await ParserFacade.ParseFileAsync(input, resolver, cancellationToken, parseOptions);
            if (!current.Success)
                return await SchemaCommandOptions.WriteErrors(standardError, options.ErrorFormat, current.Errors);
            var checker = new CompatibilityChecker();
            currentPath = options.Against!;
            ParseResult previous;
            if (options.Against!.StartsWith(".git#", StringComparison.Ordinal))
            {
                var reference = await SchemaFiles.ReadGitReference(options.Against, input,
                    options.ImportDirectories, cancellationToken);
                previous = await ParserFacade.ParseContentAsync(reference.Content, input, reference.Resolver, parseOptions);
            }
            else
                previous = await ParserFacade.ParseFileAsync(options.Against, resolver, cancellationToken, parseOptions);
            if (!previous.Success)
                return await SchemaCommandOptions.WriteErrors(standardError, options.ErrorFormat, previous.Errors);
            var result = checker.Compare(previous.Ast!, current.Ast!, compatibility);
            if (options.ErrorFormat == "json")
            {
                await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    compatible = !result.HasBreakingChanges,
                    severity = result.MaxSeverity.ToString().ToLowerInvariant(),
                    exit_code = result.ExitCode,
                    changes = result.Changes.Select(change => new
                    {
                        id = change.Id,
                        type = Category(change.Category),
                        severity = change.Severity.ToString().ToLowerInvariant(),
                        suppressed = change.IsSuppressed,
                        location = change.Location,
                        description = change.Description,
                        recommendation = change.Recommendation
                    })
                }));
            }
            else
            {
                foreach (var change in result.Changes)
                {
                    if (change.IsSuppressed && !options.Verbose)
                        continue;
                    if (!options.Verbose && change.Severity == ChangeSeverity.Info)
                        continue;
                    var writer = change.Severity == ChangeSeverity.Info || change.IsSuppressed ? standardOutput : standardError;
                    await writer.WriteLineAsync($"{change.Location}: {change.Severity.ToString().ToLowerInvariant()} {change.Id}: {change.Description}" +
                        (change.IsSuppressed ? " (suppressed)" : ""));
                    if (change.Recommendation != null)
                        await writer.WriteLineAsync("  " + change.Recommendation);
                }
            }
            return result.ExitCode;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return await SchemaCommandOptions.WriteErrors(standardError, options.ErrorFormat,
                [new ParseError(error.Message, currentPath, 0, 0)]);
        }
    }

    private static string Category(ChangeCategory category) => category switch
    {
        ChangeCategory.Compatible => "compatible",
        ChangeCategory.BreakingWire => "breaking_wire",
        ChangeCategory.BreakingText => "breaking_text",
        _ => "invalid_schema"
    };
}
