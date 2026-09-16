using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
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
        var options = ParseArguments(args);
        if (options.Errors.Count > 0)
        {
            return await WriteErrorsAsync(standardError, options.ErrorFormat, options.Errors);
        }
        if (options.Help)
        {
            await standardOutput.WriteLineAsync(
                options.Language is null ? GenerateHelp : CSharpHelp);
            return 0;
        }

        var errors = new List<ParseError>();
        var currentPath = options.OutputDirectory!;
        try
        {
            var outputDirectory = Path.GetFullPath(options.OutputDirectory!);
            var importDirectories = options.ImportDirectories.Select(Path.GetFullPath).ToArray();
            var inputs = new List<(string Input, string Output)>();
            var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in options.Inputs)
            {
                currentPath = input;
                var fullPath = Path.GetFullPath(input);
                var name = Path.GetFileNameWithoutExtension(fullPath);
                if (!string.Equals(Path.GetExtension(fullPath), ".bond", StringComparison.OrdinalIgnoreCase)
                    || name.Length == 0)
                {
                    errors.Add(new ParseError("Input must be a named .bond file.", input, 0, 0));
                    continue;
                }

                var outputName = name + ".g.cs";
                if (!outputNames.Add(outputName))
                {
                    errors.Add(new ParseError($"Output-name collision: '{outputName}'. Input basenames must be unique (case-insensitive).", input, 0, 0));
                }
                inputs.Add((fullPath, Path.Combine(outputDirectory, outputName)));
            }
            if (errors.Count > 0)
            {
                return await WriteErrorsAsync(standardError, options.ErrorFormat, errors);
            }

            ImportResolver resolver = async (currentFile, importPath) =>
            {
                var directories = new[] { Path.GetDirectoryName(currentFile)! }.Concat(importDirectories);
                foreach (var directory in directories)
                {
                    var normalizedImport = importPath.Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar);
                    var candidate = Path.GetFullPath(Path.Combine(directory, normalizedImport));
                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(candidate, cancellationToken);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        continue;
                    }
                    return (candidate, content);
                }
                throw new FileNotFoundException($"Imported file not found: {importPath}", importPath);
            };

            var generated = new List<(string Path, string Code)>();
            foreach (var (input, output) in inputs)
            {
                currentPath = input;
                var content = await File.ReadAllTextAsync(input, cancellationToken);
                var parsed = await ParserFacade.ParseContentAsync(content, input, resolver);
                if (!parsed.Success)
                {
                    errors.AddRange(parsed.Errors);
                    if (parsed.Errors.Count == 0)
                    {
                        errors.Add(new ParseError("Parsing produced no schema.", input, 0, 0));
                    }
                    continue;
                }
                var result = CSharpGenerator.Generate(parsed.Ast!, input, new CSharpGenerationOptions
                {
                    NamespaceMappings = options.NamespaceMappings,
                    TypeMappings = options.TypeMappings,
                    GenerateModelFeatures = options.GenerateModelFeatures
                });
                if (!result.Success)
                {
                    errors.AddRange(result.Errors);
                    if (result.Errors.Count == 0)
                    {
                        errors.Add(new ParseError("C# generation produced no output.", input, 0, 0));
                    }
                    continue;
                }
                generated.Add((output, result.Code!));
            }
            if (errors.Count > 0)
            {
                return await WriteErrorsAsync(standardError, options.ErrorFormat, errors);
            }

            currentPath = outputDirectory;
            if (File.Exists(outputDirectory))
            {
                errors.Add(new ParseError("Output directory is an existing file.", outputDirectory, 0, 0));
            }
            var existingEntries = Directory.Exists(outputDirectory)
                ? Directory.EnumerateFileSystemEntries(outputDirectory).ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                : Array.Empty<string>().ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            var pending = new List<(string Path, string Code)>();
            foreach (var output in generated)
            {
                currentPath = output.Path;
                var matches = existingEntries[Path.GetFileName(output.Path)].ToArray();
                if (matches.Length > 1 || (matches.Length == 1 && !string.Equals(Path.GetFileName(matches[0]), Path.GetFileName(output.Path), StringComparison.Ordinal)))
                {
                    errors.Add(new ParseError("Output-name collision with an existing directory entry (case-insensitive).", output.Path, 0, 0));
                    continue;
                }
                if (matches.Length == 1)
                {
                    var attributes = File.GetAttributes(matches[0]);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    {
                        errors.Add(new ParseError("Output path is a directory or symbolic link; refusing to overwrite it.", output.Path, 0, 0));
                        continue;
                    }
                    var existing = await File.ReadAllTextAsync(output.Path, cancellationToken);
                    if (!HasGeneratedHeader(existing))
                    {
                        errors.Add(new ParseError("Refusing to overwrite a non-generated file.", output.Path, 0, 0));
                        continue;
                    }
                    if (string.Equals(existing, output.Code, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                pending.Add(output);
            }
            if (errors.Count > 0)
            {
                return await WriteErrorsAsync(standardError, options.ErrorFormat, errors);
            }

            // No directory creation or file writes until every selected input and output is valid.
            currentPath = outputDirectory;
            Directory.CreateDirectory(outputDirectory);
            foreach (var output in pending)
            {
                currentPath = output.Path;
                await File.WriteAllTextAsync(output.Path, output.Code, cancellationToken);
            }
            foreach (var output in generated)
            {
                await standardOutput.WriteLineAsync(output.Path);
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errors.Add(new ParseError(ex.Message, currentPath, 0, 0));
            return await WriteErrorsAsync(standardError, options.ErrorFormat, errors);
        }
    }

    private static bool HasGeneratedHeader(string content) =>
        content == CSharpGenerator.GeneratedHeader
        || content.StartsWith(CSharpGenerator.GeneratedHeader + "\n", StringComparison.Ordinal)
        || content.StartsWith(CSharpGenerator.GeneratedHeader + "\r\n", StringComparison.Ordinal);

    private static async Task<int> WriteErrorsAsync(TextWriter writer, string format, IReadOnlyList<ParseError> errors)
    {
        if (format == "json")
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                error = "generation_error",
                message = "C# generation failed.",
                errors = errors.Select(e => new
                {
                    line = e.Line,
                    column = e.Column,
                    message = e.Message,
                    file = e.FilePath ?? "bond"
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

    private sealed class Options
    {
        public string? Language { get; set; }
        public List<string> Inputs { get; } = [];
        public string? OutputDirectory { get; set; }
        public List<string> ImportDirectories { get; } = [];
        public List<string> NamespaceMappings { get; } = [];
        public List<string> TypeMappings { get; } = [];
        public bool GenerateModelFeatures { get; set; } = true;
        public string ErrorFormat { get; set; } = "text";
        public bool Help { get; set; }
        public List<ParseError> Errors { get; } = [];
    }

    private static Options ParseArguments(string[] args)
    {
        var options = new Options();
        var positionalOnly = false;
        var seenOutput = false;
        var seenFormat = false;
        var seenFeatures = false;
        void Error(string message) => options.Errors.Add(new ParseError(message, "bond", 0, 0));

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
            if (!positionalOnly && argument.StartsWith('-'))
            {
                var equals = argument.IndexOf('=');
                var name = equals < 0 ? argument : argument[..equals];
                if (name is not ("-o" or "--output-dir" or "-I" or "--import-dir" or "--error-format"
                    or "-n" or "--namespace" or "-u" or "--using" or "--type-map" or "--model-features"))
                {
                    Error($"Unknown option '{name}'.");
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
                    Error($"Option '{name}' requires a value.");
                    continue;
                }
                if (value.Length == 0)
                {
                    Error($"Option '{name}' requires a non-empty value.");
                    continue;
                }
                switch (name)
                {
                    case "-o" or "--output-dir":
                        if (seenOutput) Error("Option '--output-dir' may only be specified once.");
                        seenOutput = true;
                        options.OutputDirectory = value;
                        break;
                    case "-I" or "--import-dir":
                        options.ImportDirectories.Add(value);
                        break;
                    case "-n" or "--namespace":
                        options.NamespaceMappings.Add(value);
                        break;
                    case "-u" or "--using" or "--type-map":
                        options.TypeMappings.Add(value);
                        break;
                    case "--model-features":
                        if (seenFeatures) Error("Option '--model-features' may only be specified once.");
                        seenFeatures = true;
                        if (value is "all" or "none") options.GenerateModelFeatures = value == "all";
                        else Error($"Unsupported model features '{value}'; expected 'all' or 'none'.");
                        break;
                    case "--error-format":
                        if (seenFormat) Error("Option '--error-format' may only be specified once.");
                        seenFormat = true;
                        if (value is "text" or "json") options.ErrorFormat = value;
                        else Error($"Unsupported error format '{value}'; expected 'text' or 'json'.");
                        break;
                }
                continue;
            }
            if (options.Language is null) options.Language = argument;
            else options.Inputs.Add(argument);
        }
        if (options.Language is not null && !options.Language.Equals("csharp", StringComparison.OrdinalIgnoreCase))
        {
            Error($"Unsupported language '{options.Language}'; only 'csharp' is supported.");
        }
        if (!options.Help)
        {
            if (options.Language is null) Error("A language is required: bond generate csharp.");
            if (options.Inputs.Count == 0) Error("At least one explicit .bond input file is required.");
            if (options.OutputDirectory is null) Error("Option '-o'/'--output-dir' is required.");
        }
        return options;
    }

    private const string GenerateHelp = """
        Usage: bond generate csharp <file.bond>... -o <output-dir> [options]

        Generate model-only C# code requiring Bond.Runtime.CSharp and BondTools.Models.
        Only csharp is supported.
        Run bond generate csharp --help for options and limitations.
        """;

    private const string CSharpHelp = """
        Usage: bond generate csharp <file.bond>... -o <output-dir> [options]

        Generate model-only C# code requiring Bond.Runtime.CSharp and BondTools.Models.
        One <input-basename>.g.cs is generated per explicit input; imports are resolved
        but are not generated unless explicitly selected.

        Options:
          -o, --output-dir <directory>  Required output directory
          -I, --import-dir <directory>  Import search directory (repeatable, searched in order)
          -n, --namespace <from=to>     Map an exact C# namespace (repeatable)
          --type-map <alias=CLR-type>  Map an IDL alias to a CLR type (repeatable; aliases: -u, --using)
          --model-features <all|none>  Metadata, cloning, equality, and debugger support (default: all)
          --error-format <text|json>    Diagnostics on stderr (default: text)
          -h, --help                   Show this help
          --                           Treat remaining arguments as positional inputs

        Imports are searched relative to the importing file before import directories.
        Options accept both --option value and --option=value.
        Generates structs and enums, including generics, inheritance, views, aliases,
        and bond_meta fields. Services produce no C# RPC types, matching gbc.
        No protocol selection or serializer code is generated.
        --model-features=none preserves plain model output and does not require BondTools.Models.
        Type mappings use qualified IDL alias names (or a local alias name) and qualified CLR
        types. Generic templates use {0}, {1}, etc. Supply BondTypeAliasConverter.Convert
        overloads in each consuming model namespace for conversions in both directions.
        Custom mapped defaults are converted explicitly; mappings do not change wire types.

        Example:
          bond generate csharp schemas/order.bond -o out/generated
        """;
}
