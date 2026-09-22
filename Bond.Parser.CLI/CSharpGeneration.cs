using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

internal sealed record GeneratedFile(string Path, string Code);

internal sealed record GenerationPlan(
    string OutputDirectory,
    IReadOnlyList<GeneratedFile> Outputs,
    IReadOnlyList<GeneratedFile> PendingWrites);

internal sealed class CSharpGeneration(GenerateOptions options, CancellationToken cancellationToken)
{
    private readonly List<ParseError> _errors = [];

    internal IReadOnlyList<ParseError> Errors => _errors;
    internal string CurrentPath { get; private set; } = options.OutputDirectory!;

    internal async Task<GenerationPlan?> PrepareAsync()
    {
        var outputDirectory = Path.GetFullPath(options.OutputDirectory!);
        var importDirectories = options.ImportDirectories.Select(Path.GetFullPath).ToArray();
        var inputs = ResolveInputs(outputDirectory);
        if (_errors.Count != 0)
        {
            return null;
        }

        var resolver = SchemaFiles.ImportResolver(importDirectories, cancellationToken);
        var outputs = await GenerateSourcesAsync(inputs, resolver);
        if (_errors.Count != 0)
        {
            return null;
        }

        var pendingWrites = await PrepareWritesAsync(outputDirectory, outputs);
        return _errors.Count == 0
            ? new GenerationPlan(outputDirectory, outputs, pendingWrites)
            : null;
    }

    internal async Task ApplyAsync(GenerationPlan plan)
    {
        // Preparation validates every input and destination before any file or directory is created.
        CurrentPath = plan.OutputDirectory;
        Directory.CreateDirectory(plan.OutputDirectory);
        foreach (var output in plan.PendingWrites)
        {
            CurrentPath = output.Path;
            await File.WriteAllTextAsync(output.Path, output.Code, cancellationToken);
        }
    }

    private List<(string Input, string Output)> ResolveInputs(string outputDirectory)
    {
        var inputs = new List<(string Input, string Output)>();
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in options.Inputs)
        {
            CurrentPath = input;
            var fullPath = Path.GetFullPath(input);
            var name = Path.GetFileNameWithoutExtension(fullPath);
            if (!string.Equals(Path.GetExtension(fullPath), ".bond", StringComparison.OrdinalIgnoreCase)
                || name.Length == 0)
            {
                _errors.Add(new ParseError("Input must be a named .bond file.", input, 0, 0));
                continue;
            }

            var outputName = name + ".g.cs";
            if (!outputNames.Add(outputName))
            {
                _errors.Add(new ParseError(
                    $"Output-name collision: '{outputName}'. Input basenames must be unique (case-insensitive).",
                    input, 0, 0));
            }

            inputs.Add((fullPath, Path.Combine(outputDirectory, outputName)));
        }

        return inputs;
    }

    private async Task<List<GeneratedFile>> GenerateSourcesAsync(
        IEnumerable<(string Input, string Output)> inputs, ImportResolver resolver)
    {
        var generationOptions = options.CreateGenerationOptions();
        var outputs = new List<GeneratedFile>();
        foreach (var (input, output) in inputs)
        {
            CurrentPath = input;
            var content = await File.ReadAllTextAsync(input, cancellationToken);
            var parsed = await ParserFacade.ParseContentAsync(content, input, resolver);
            if (!parsed.Success)
            {
                AddErrors(parsed.Errors, "Parsing produced no schema.");
                continue;
            }

            var generated = CSharpGenerator.Generate(parsed.Ast!, input, generationOptions);
            if (!generated.Success)
            {
                AddErrors(generated.Errors, "C# generation produced no output.");
                continue;
            }

            outputs.Add(new GeneratedFile(output, generated.Code!));
        }

        return outputs;
    }

    private async Task<List<GeneratedFile>> PrepareWritesAsync(
        string outputDirectory, IReadOnlyList<GeneratedFile> outputs)
    {
        CurrentPath = outputDirectory;
        if (File.Exists(outputDirectory))
        {
            _errors.Add(new ParseError("Output directory is an existing file.", outputDirectory, 0, 0));
        }

        IEnumerable<string> entries = Directory.Exists(outputDirectory)
            ? Directory.EnumerateFileSystemEntries(outputDirectory)
            : [];
        var existingEntries = entries.ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var pending = new List<GeneratedFile>();

        foreach (var output in outputs)
        {
            CurrentPath = output.Path;
            var outputName = Path.GetFileName(output.Path);
            var matches = existingEntries[outputName].ToArray();
            var differentCasing = matches.Length == 1
                && !string.Equals(Path.GetFileName(matches[0]), outputName, StringComparison.Ordinal);
            if (matches.Length > 1 || differentCasing)
            {
                _errors.Add(new ParseError(
                    "Output-name collision with an existing directory entry (case-insensitive).", output.Path, 0, 0));
                continue;
            }

            if (matches.Length == 1)
            {
                var attributes = File.GetAttributes(matches[0]);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    _errors.Add(new ParseError(
                        "Output path is a directory or symbolic link; refusing to overwrite it.", output.Path, 0, 0));
                    continue;
                }

                var existing = await File.ReadAllTextAsync(output.Path, cancellationToken);
                if (!HasGeneratedHeader(existing))
                {
                    _errors.Add(new ParseError("Refusing to overwrite a non-generated file.", output.Path, 0, 0));
                    continue;
                }

                if (string.Equals(existing, output.Code, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            pending.Add(output);
        }

        return pending;
    }

    private void AddErrors(IReadOnlyList<ParseError> errors, string missingResult)
    {
        if (errors.Count == 0)
        {
            _errors.Add(new ParseError(missingResult, CurrentPath, 0, 0));
        }
        else
        {
            _errors.AddRange(errors);
        }
    }

    private static bool HasGeneratedHeader(string content) =>
        content == CSharpGenerator.GeneratedHeader
        || content.StartsWith(CSharpGenerator.GeneratedHeader + "\n", StringComparison.Ordinal)
        || content.StartsWith(CSharpGenerator.GeneratedHeader + "\r\n", StringComparison.Ordinal);
}
