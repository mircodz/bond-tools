using System;
using System.Collections.Generic;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

internal sealed class GenerateOptions
{
    internal string? Language { get; private set; }
    internal List<string> Inputs { get; } = [];
    internal string? OutputDirectory { get; private set; }
    internal List<string> ImportDirectories { get; } = [];
    internal List<string> UsingNamespaces { get; } = [];
    internal List<string> NamespaceMappings { get; } = [];
    internal List<string> TypeMappings { get; } = [];
    internal CSharpModelFeatures ModelFeatures { get; private set; }
    internal string ErrorFormat { get; private set; } = "text";
    internal bool Help { get; private set; }
    internal List<ParseError> Errors { get; } = [];

    internal CSharpGenerationOptions CreateGenerationOptions() => new()
    {
        UsingNamespaces = UsingNamespaces,
        NamespaceMappings = NamespaceMappings,
        TypeMappings = TypeMappings,
        ModelFeatures = ModelFeatures
    };

    internal static GenerateOptions Parse(string[] args)
    {
        var options = new GenerateOptions();
        var positionalOnly = false;
        var seenOutput = false;
        var seenFormat = false;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!positionalOnly && argument == "--")
            {
                positionalOnly = true;
                continue;
            }

            if (!positionalOnly && argument is "-h" or "--help")
            {
                options.Help = true;
                continue;
            }

            if (positionalOnly || !argument.StartsWith('-'))
            {
                if (options.Language is null)
                {
                    options.Language = argument;
                }
                else
                {
                    options.Inputs.Add(argument);
                }

                continue;
            }

            var option = CommandLineOption.Parse(argument);
            var feature = option.Name switch
            {
                "--descriptors" => CSharpModelFeatures.Descriptors,
                "--clone" or "--clonable" => CSharpModelFeatures.Cloning,
                "--equality" or "--default-equals" => CSharpModelFeatures.Equality,
                "--debugger" => CSharpModelFeatures.Debugger,
                "--to-string" => CSharpModelFeatures.StringRepresentation,
                _ => CSharpModelFeatures.None
            };
            if (feature != CSharpModelFeatures.None)
            {
                if (option.InlineValue is not null)
                {
                    options.Error($"Flag '{option.Name}' does not take a value.");
                }
                else
                {
                    options.ModelFeatures |= feature;
                }

                continue;
            }

            if (option.Name is not ("-o" or "--output-dir" or "-I" or "--import-dir" or "--error-format"
                or "-n" or "--namespace" or "-u" or "--using" or "--type-map"))
            {
                options.Error($"Unknown option '{option.Name}'.");
                continue;
            }

            if (!option.TryReadValue(args, ref i, out var value))
            {
                options.Error($"Option '{option.Name}' requires a value.");
                continue;
            }

            if (value.Length == 0)
            {
                options.Error($"Option '{option.Name}' requires a non-empty value.");
                continue;
            }

            switch (option.Name)
            {
                case "-o" or "--output-dir":
                    if (seenOutput)
                    {
                        options.Error("Option '--output-dir' may only be specified once.");
                    }

                    seenOutput = true;
                    options.OutputDirectory = value;
                    break;
                case "-I" or "--import-dir":
                    options.ImportDirectories.Add(value);
                    break;
                case "-n" or "--namespace":
                    options.NamespaceMappings.Add(value);
                    break;
                case "-u" or "--using":
                    options.UsingNamespaces.Add(value);
                    break;
                case "--type-map":
                    options.TypeMappings.Add(value);
                    break;
                case "--error-format":
                    if (seenFormat)
                    {
                        options.Error("Option '--error-format' may only be specified once.");
                    }

                    seenFormat = true;
                    if (value is "text" or "json")
                    {
                        options.ErrorFormat = value;
                    }
                    else
                    {
                        options.Error($"Unsupported error format '{value}'; expected 'text' or 'json'.");
                    }

                    break;
            }
        }

        if (options.Language is not null && !options.Language.Equals("csharp", StringComparison.OrdinalIgnoreCase))
        {
            options.Error($"Unsupported language '{options.Language}'; only 'csharp' is supported.");
        }

        if (!options.Help)
        {
            if (options.Language is null)
            {
                options.Error("A language is required: bond generate csharp.");
            }

            if (options.Inputs.Count == 0)
            {
                options.Error("At least one explicit .bond input file is required.");
            }

            if (options.OutputDirectory is null)
            {
                options.Error("Option '-o'/'--output-dir' is required.");
            }
        }

        return options;
    }

    private void Error(string message) => Errors.Add(new ParseError(message, "bond", 0, 0));
}
