using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

public static class CheckCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter standardOutput, TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        var options = SchemaCommandOptions.Parse(args, comparison: false);
        if (options.Errors.Count != 0)
        {
            return await SchemaDiagnostics.WriteAsync(standardError, options.ErrorFormat,
                options.Errors.Select(error => new ParseError(error, null, 0, 0)));
        }

        if (options.Help)
        {
            await standardOutput.WriteLineAsync("""
                Usage: bond check <schema.bond> [options]

                Validate syntax, types, and imports without generating files.

                -I, --import-dir <path>   Import search directory (repeatable)
                --error-format text|json Diagnostics on stderr (default: text)
                -h, --help               Show this help

                Exit codes: 0 valid, 1 invalid schema, 2 invalid usage or I/O failure.
                """);
            return 0;
        }

        var file = options.Input!;
        try
        {
            file = Path.GetFullPath(file);
            var result = await ParserFacade.ParseFileAsync(file,
                SchemaFiles.ImportResolver(options.ImportDirectories, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Success)
            {
                return 0;
            }

            return await SchemaDiagnostics.WriteAsync(standardError, options.ErrorFormat, result.Errors, exitCode: 1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return await SchemaDiagnostics.WriteAsync(standardError, options.ErrorFormat,
                [new ParseError(error.Message, file, 0, 0)]);
        }
    }
}
