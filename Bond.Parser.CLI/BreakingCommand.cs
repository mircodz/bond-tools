using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.Compatibility;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

internal static class BreakingCommand
{
    public static async Task<int> Run(string[] rest)
    {
        var args = Arguments.Parse(rest, ["--ignore-imports", "--list-rules", "--verbose"],
            ["--against", "--suppress", "--import-dir", "--error-format"]);
        if (args.Has("--help"))
        {
            return Program.ShowHelp();
        }

        if (args.Has("--list-rules"))
        {
            foreach (var rule in DiagnosticIds.Rules.OrderBy(rule => rule.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{rule.Key}  {rule.Value}");
            }

            return 0;
        }

        var format = args.ErrorFormat();
        var input = Path.GetFullPath(args.Positional("schema file"));
        var against = args.Required("--against");
        var suppressions = args.All("--suppress")
            .SelectMany(value => value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (suppressions.FirstOrDefault(id => !DiagnosticIds.All.Contains(id)) is { } unknown)
        {
            throw new UsageException($"Unknown diagnostic ID '{unknown}'.");
        }

        var ignoreImports = args.Has("--ignore-imports");
        var parseOptions = new ParseOptions(IgnoreImports: ignoreImports);
        var importDirectories = args.All("--import-dir");
        var current = await ParserFacade.ParseFileAsync(input, DefaultImportResolver.Create(importDirectories), options: parseOptions);
        if (!current.Success)
        {
            return Diagnostics.Report(current.Errors, format, exitCode: 2);
        }

        ParseResult previous;
        try
        {
            previous = against.StartsWith(".git#", StringComparison.Ordinal)
                ? await GitReference.ParseAsync(against, input, importDirectories, parseOptions)
                : await ParserFacade.ParseFileAsync(Path.GetFullPath(against), DefaultImportResolver.Create(importDirectories),
                    options: parseOptions);
        }
        catch (IOException error)
        {
            previous = new ParseResult(null, [new ParseError(error.Message, against, 0, 0)]);
        }

        if (!previous.Success)
        {
            return Diagnostics.Report(previous.Errors, format, exitCode: 2);
        }

        var result = new CompatibilityChecker().Compare(previous.Ast!, current.Ast!, new CompatibilityOptions
        {
            IncludeImports = !ignoreImports,
            AllowUnresolvedTypes = ignoreImports,
            SuppressedDiagnosticIds = suppressions
        });

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
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
            return result.ExitCode;
        }

        var verbose = args.Has("--verbose");
        foreach (var change in result.Changes.Where(change =>
            verbose || (!change.IsSuppressed && change.Severity != ChangeSeverity.Info)))
        {
            var writer = change.Severity == ChangeSeverity.Info || change.IsSuppressed ? Console.Out : Console.Error;
            writer.WriteLine($"{change.Location}: {change.Severity.ToString().ToLowerInvariant()} {change.Id}: {change.Description}" +
                (change.IsSuppressed ? " (suppressed)" : ""));
            if (change.Recommendation != null)
            {
                writer.WriteLine("  " + change.Recommendation);
            }
        }

        return result.ExitCode;
    }

    private static string Category(ChangeCategory category) => category switch
    {
        ChangeCategory.Compatible => "compatible",
        ChangeCategory.BreakingWire => "breaking_wire",
        ChangeCategory.BreakingText => "breaking_text",
        _ => "invalid_schema"
    };
}
