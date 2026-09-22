using System;
using System.Collections.Generic;
using Bond.Parser.Compatibility;

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

    internal CompatibilityOptions CreateCompatibilityOptions() => new()
    {
        IncludeImports = !IgnoreImports,
        AllowUnresolvedTypes = IgnoreImports,
        SuppressedDiagnosticIds = Suppressions
    };

    internal static SchemaCommandOptions Parse(string[] args, bool comparison)
    {
        var options = new SchemaCommandOptions();
        var positionalOnly = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!positionalOnly && argument == "--")
            {
                positionalOnly = true;
                continue;
            }

            if (positionalOnly || !argument.StartsWith('-'))
            {
                if (options.Input != null)
                {
                    options.Errors.Add("Only one root schema may be selected.");
                }
                else
                {
                    options.Input = argument;
                }

                continue;
            }

            var option = CommandLineOption.Parse(argument);
            var name = option.Name;
            var isFlag = name is "-h" or "--help"
                || (comparison && name is "-v" or "--verbose" or "--ignore-imports" or "--list-rules");

            if (isFlag)
            {
                if (option.InlineValue is not null)
                {
                    options.Errors.Add($"Flag '{name}' does not accept a value.");
                }
                else
                {
                    switch (name)
                    {
                        case "-h" or "--help":
                            options.Help = true;
                            break;
                        case "-v" or "--verbose":
                            options.Verbose = true;
                            break;
                        case "--ignore-imports":
                            options.IgnoreImports = true;
                            break;
                        case "--list-rules":
                            options.ListRules = true;
                            break;
                    }
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

            if (!option.TryReadValue(args, ref i, out var value))
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
}
