using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.Compatibility;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

internal sealed class SchemaCommandOptions
{
    internal string? Input { get; private set; }
    internal string? Against { get; private set; }
    internal List<string> ImportDirectories { get; } = [];
    internal HashSet<string> Suppressions { get; } = new(StringComparer.Ordinal);
    internal bool IgnoreImports { get; private set; }
    internal bool Verbose { get; private set; }
    internal bool Help { get; private set; }
    internal bool ListRules { get; private set; }
    internal string ErrorFormat { get; private set; } = "text";
    internal List<string> Errors { get; } = [];

    internal CompatibilityOptions CreateCompatibilityOptions() =>
        new()
        {
            IncludeImports = !IgnoreImports,
            AllowUnresolvedTypes = IgnoreImports,
            SuppressedDiagnosticIds = Suppressions
        };

    internal static SchemaCommandOptions Parse(string[] args, bool comparison)
    {
        var options = new SchemaCommandOptions();
        var positional = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!positional && argument == "--")
            {
                positional = true;
                continue;
            }

            if (!positional && argument.StartsWith('-'))
            {
                var equals = argument.IndexOf('=');
                var name = equals < 0 ? argument : argument[..equals];
                var isFlag = name is "-h" or "--help"
                    || (comparison && name is "-v" or "--verbose" or "--ignore-imports" or "--list-rules");

                if (isFlag)
                {
                    if (equals >= 0)
                    {
                        options.Errors.Add($"Flag '{name}' does not accept a value.");
                    }
                    else if (name is "-h" or "--help")
                    {
                        options.Help = true;
                    }
                    else if (name is "-v" or "--verbose")
                    {
                        options.Verbose = true;
                    }
                    else if (name == "--ignore-imports")
                    {
                        options.IgnoreImports = true;
                    }
                    else if (name == "--list-rules")
                    {
                        options.ListRules = true;
                    }

                    continue;
                }

                var allowed = name is "-I" or "--import-dir" or "--error-format"
                    || (comparison && name is "--against" or "--suppress");
                if (!allowed)
                {
                    options.Errors.Add($"Unknown option '{name}'.");
                    continue;
                }

                string value;
                if (equals >= 0)
                {
                    value = argument[(equals + 1)..];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    value = args[++i];
                }
                else
                {
                    options.Errors.Add($"Option '{name}' requires a value.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    options.Errors.Add($"Option '{name}' requires a non-empty value.");
                    continue;
                }

                if (name is "--against" or "--error-format" && !seen.Add(name))
                {
                    options.Errors.Add($"Option '{name}' may only be specified once.");
                }

                switch (name)
                {
                    case "-I" or "--import-dir":
                        options.ImportDirectories.Add(value);
                        break;
                    case "--against":
                        options.Against = value;
                        break;
                    case "--suppress":
                        var suppressions = Split(value);
                        if (suppressions.Length == 0)
                        {
                            options.Errors.Add("--suppress requires at least one diagnostic ID.");
                        }
                        else
                        {
                            options.Suppressions.UnionWith(suppressions);
                        }

                        break;
                    case "--error-format":
                        if (value is "text" or "json")
                        {
                            options.ErrorFormat = value;
                        }
                        else
                        {
                            options.Errors.Add("Error format must be text or json.");
                        }

                        break;
                }

                continue;
            }

            if (options.Input != null)
            {
                options.Errors.Add("Only one root schema may be selected.");
            }
            else
            {
                options.Input = argument;
            }
        }

        if (!options.Help && !options.ListRules)
        {
            if (options.Input == null)
            {
                options.Errors.Add("A .bond schema file is required.");
            }

            if (comparison && options.Against == null)
            {
                options.Errors.Add("--against is required.");
            }
        }

        return options;
    }

    internal static string[] Split(string value) =>
        value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static async Task<int> WriteErrors(TextWriter writer, string format, IEnumerable<ParseError> errors,
        int exitCode = 2)
    {
        var values = errors.ToArray();
        if (format == "json")
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                error = "schema_error",
                errors = values.Select(error => new
                {
                    file = error.FilePath,
                    line = error.Line,
                    column = error.Column,
                    message = error.Message
                })
            }));
        }
        else
        {
            foreach (var error in values)
            {
                await writer.WriteLineAsync($"{error.FilePath ?? "bond"}({error.Line},{error.Column}): error: {error.Message}");
            }
        }

        return exitCode;
    }
}
