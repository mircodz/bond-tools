using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

public static class GenerateCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        var options = GenerateOptions.Parse(args);
        if (options.Errors.Count > 0)
        {
            return await WriteErrorsAsync(standardError, options.ErrorFormat, options.Errors);
        }

        if (options.Help)
        {
            await standardOutput.WriteLineAsync(options.Language is null ? GenerateHelp : CSharpHelp);
            return 0;
        }

        var generation = new CSharpGeneration(options, cancellationToken);
        try
        {
            var plan = await generation.PrepareAsync();
            if (plan is null)
            {
                return await WriteErrorsAsync(standardError, options.ErrorFormat, generation.Errors);
            }

            await generation.ApplyAsync(plan);
            foreach (var output in plan.Outputs)
            {
                await standardOutput.WriteLineAsync(output.Path);
            }

            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var errors = generation.Errors.Append(new ParseError(error.Message, generation.CurrentPath, 0, 0)).ToArray();
            return await WriteErrorsAsync(standardError, options.ErrorFormat, errors);
        }
    }

    private static async Task<int> WriteErrorsAsync(TextWriter writer, string format, IReadOnlyList<ParseError> errors)
    {
        if (format == "json")
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                error = "generation_error",
                message = "C# generation failed.",
                errors = errors.Select(error => new
                {
                    line = error.Line,
                    column = error.Column,
                    message = error.Message,
                    file = error.FilePath ?? "bond"
                })
            }));
        }
        else
        {
            foreach (var error in errors)
            {
                await writer.WriteLineAsync($"{error.FilePath ?? "bond"}({error.Line},{error.Column}): error BOND1001: {error.Message}");
            }
        }

        return 1;
    }

    private const string GenerateHelp = """
        C# model generation.

        Usage: bond generate csharp <file.bond>... -o <output-dir> [options]

        Generate model-only C# code requiring Bond.Runtime.CSharp.
        Only csharp is supported.
        Run bond generate csharp --help for options and limitations.
        """;

    private const string CSharpHelp = """
        C# model generation.

        Usage: bond generate csharp <file.bond>... -o <output-dir> [options]

        Generate model-only C# code requiring Bond.Runtime.CSharp.
        One <input-basename>.g.cs is generated per explicit input; imports are resolved
        but are not generated unless explicitly selected.

        Options:
          -o, --output-dir <directory>  Required output directory
          -I, --import-dir <directory>  Import search directory (repeatable, searched in order)
          -n, --namespace <from=to>     Map an exact C# namespace (repeatable)
          -u, --using <namespace>       Add a C# using directive (repeatable)
          --type-map <alias=CLR-type>   Map an IDL alias to a CLR type (repeatable)
          --descriptors                 Emit reflection-free schema descriptors
          --clone, --clonable           Emit deep cloning methods
          --equality, --default-equals  Emit structural equality and hashing
          --debugger                    Emit safe debugger displays and field views
          --to-string                   Emit bounded compact ToString() summaries
          --error-format <text|json>    Diagnostics on stderr (default: text)
          -h, --help                    Show this help
          --                            Treat remaining arguments as positional inputs

        Imports are searched relative to the importing file before import directories.
        Options accept both --option value and --option=value.
        Generates structs and enums, including generics, inheritance, views, aliases,
        and bond_meta fields. Services produce no C# RPC types, matching gbc.
        No protocol selection or serializer code is generated.
        Plain models are the default. Enabling any model feature requires BondTools.Models.
        Use the BondTools.Models version shown in the generated header; bond --version
        reports the tool version. Model support is a separate package, not a serializer.
        Individual feature flags are additive. Cloning and equality may materialize bonded<T>
        payloads; debugger inspection never does. External CLR types may require typed adapters.
        Type mappings use qualified IDL alias names (or a local alias name) and qualified CLR
        types. Generic templates use {0}, {1}, etc. Supply BondTypeAliasConverter.Convert
        overloads in each consuming model namespace for conversions in both directions.
        Custom mapped defaults are converted explicitly; mappings do not change wire types.
        The original Bond runtime supports only zero/empty/nothing defaults for custom CLR
        aliases; other mapped scalar defaults are rejected to avoid changing schema semantics.

        Example:
          bond generate csharp schemas/order.bond -o out/generated
        """;
}
