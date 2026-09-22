using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;

namespace BondTools.Build;

internal sealed record GenerationResult(
    string[] GeneratedFiles,
    string[] WrittenFiles,
    IReadOnlyList<ParseError> Errors,
    string Status);

internal sealed class GenerationFailureException(string path, Exception cause) : Exception(cause.Message, cause)
{
    internal ParseError Error { get; } = new(cause.Message, path, 0, 0);

    internal static bool IsExpected(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or JsonException;
}

internal static class GenerationEngine
{
    internal static async Task<GenerationResult> RunAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (plan, errors) = await PrepareAsync(request, cancellationToken);
            if (plan == null)
            {
                return new GenerationResult([], [], errors, "");
            }

            return await plan.ApplyAsync(cancellationToken);
        }
        catch (GenerationFailureException error)
        {
            return new GenerationResult([], [], [error.Error], "");
        }
    }

    private static async Task<(GenerationPlan? Plan, IReadOnlyList<ParseError> Errors)> PrepareAsync(
        BuildRequest request, CancellationToken cancellationToken)
    {
        var (previous, identity) = await ReadPreviousGenerationAsync(request, cancellationToken);
        var generatorUnchanged = previous?.GeneratorIdentity == identity;
        var oldEntries = previous?.Entries.ToDictionary(entry => entry.Source, BuildFiles.PathComparer)
            ?? new Dictionary<string, BuildManifestEntry>(BuildFiles.PathComparer);
        var outputs = new List<PreparedOutput>();
        var errors = new List<ParseError>();

        foreach (var input in request.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            oldEntries.Remove(input.Source, out var oldEntry);
            var (output, inputErrors) = await PrepareOutputAsync(
                input, request.OutputDirectory, generatorUnchanged ? oldEntry : null, cancellationToken);
            if (output != null)
            {
                outputs.Add(output);
            }
            else
            {
                errors.AddRange(inputErrors);
            }
        }

        // Stale-output ownership errors take precedence over collected schema diagnostics.
        var removed = await FindRemovedOutputsAsync(request.OutputDirectory, oldEntries.Values, cancellationToken);
        if (errors.Count != 0)
        {
            return (null, errors);
        }

        return (new GenerationPlan(request, identity, outputs.ToArray(), removed), []);
    }

    private static async Task<(BuildManifest? Manifest, string GeneratorIdentity)> ReadPreviousGenerationAsync(
        BuildRequest request, CancellationToken cancellationToken)
    {
        var errorPath = request.OutputDirectory;
        try
        {
            BuildFiles.EnsureSafePath(request.OutputDirectory, directory: true);
            errorPath = Path.Combine(request.OutputDirectory, "manifest.json");
            var previous = await BuildManifest.ReadAsync(errorPath, request, cancellationToken);
            var identity = await GeneratorIdentityAsync(cancellationToken);
            return (previous, identity);
        }
        catch (Exception error) when (GenerationFailureException.IsExpected(error))
        {
            throw new GenerationFailureException(errorPath, error);
        }
    }

    private static async Task<(PreparedOutput? Output, IReadOnlyList<ParseError> Errors)> PrepareOutputAsync(
        BuildInput input, string outputDirectory, BuildManifestEntry? cachedEntry, CancellationToken cancellationToken)
    {
        var errorPath = outputDirectory;
        try
        {
            var outputPath = BuildFiles.OutputPath(outputDirectory, input.Output);
            errorPath = outputPath;
            var existingOutput = await BuildFiles.ReadOwnedOutputAsync(outputPath, cancellationToken);
            var optionsHash = BuildFiles.HashText(JsonSerializer.Serialize(input));

            if (cachedEntry != null
                && await CanReuseOutputAsync(cachedEntry, optionsHash, existingOutput, cancellationToken))
            {
                return (new PreparedOutput(outputPath, cachedEntry, null, Reused: true), []);
            }

            errorPath = input.Source;
            var (generated, dependencies) = await GenerateInputAsync(input, cancellationToken);
            if (!generated.Success)
            {
                return (null, generated.Errors);
            }

            var content = Encoding.UTF8.GetBytes(generated.Code!);
            var entry = new BuildManifestEntry
            {
                Source = input.Source,
                Output = input.Output,
                OptionsHash = optionsHash,
                OutputHash = BuildFiles.Hash(content),
                Dependencies = dependencies
            };
            var contentChanged = existingOutput == null || !existingOutput.AsSpan().SequenceEqual(content);

            return (new PreparedOutput(outputPath, entry, contentChanged ? content : null, Reused: false), []);
        }
        catch (Exception error) when (GenerationFailureException.IsExpected(error))
        {
            throw new GenerationFailureException(errorPath, error);
        }
    }

    private static async Task<string[]> FindRemovedOutputsAsync(
        string outputDirectory, IEnumerable<BuildManifestEntry> oldEntries, CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        foreach (var entry in oldEntries)
        {
            var path = BuildFiles.OutputPath(outputDirectory, entry.Output);
            try
            {
                if (await BuildFiles.ReadOwnedOutputAsync(path, cancellationToken) != null)
                {
                    removed.Add(path);
                }
            }
            catch (Exception error) when (GenerationFailureException.IsExpected(error))
            {
                throw new GenerationFailureException(path, error);
            }
        }

        return removed.ToArray();
    }

    private static async Task<(CSharpGenerationResult Result, BuildDependency[] Dependencies)> GenerateInputAsync(
        BuildInput input, CancellationToken cancellationToken)
    {
        var dependencies = new Dictionary<string, string?>(BuildFiles.PathComparer);

        async Task<string?> ReadSourceAsync(string path)
        {
            var bytes = await BuildFiles.ReadIfPresentAsync(path, cancellationToken);

            // Missing candidates also matter: a new higher-priority import must invalidate the cache.
            dependencies[path] = bytes == null ? null : BuildFiles.Hash(bytes);
            return bytes == null ? null : BuildFiles.Text(bytes);
        }

        ImportResolver resolver = async (importingFile, import) =>
        {
            var importPath = import.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var directories = new[] { Path.GetDirectoryName(importingFile)! }.Concat(input.ImportDirectories);

            foreach (var directory in directories)
            {
                var candidate = BuildFiles.FullPath(importPath, directory);
                var content = await ReadSourceAsync(candidate);
                if (content != null)
                {
                    return (candidate, content);
                }
            }

            throw new FileNotFoundException($"Imported file not found: {import}", import);
        };

        var source = await ReadSourceAsync(input.Source);
        if (source == null)
        {
            return (new CSharpGenerationResult(null,
                [new ParseError("Bond input file was not found.", input.Source, 0, 0)]), []);
        }

        var parsed = await ParserFacade.ParseContentAsync(source, input.Source, resolver);
        cancellationToken.ThrowIfCancellationRequested();
        if (!parsed.Success)
        {
            IReadOnlyList<ParseError> errors = parsed.Errors.Count != 0
                ? parsed.Errors
                : [new ParseError("Parsing produced no schema.", input.Source, 0, 0)];
            return (new CSharpGenerationResult(null, errors), []);
        }

        var generated = CSharpGenerator.Generate(parsed.Ast!, input.Source, input.Options);
        if (!generated.Success && generated.Errors.Count == 0)
        {
            generated = new CSharpGenerationResult(null,
                [new ParseError("C# generation produced no output.", input.Source, 0, 0)]);
        }

        var dependencyHashes = dependencies
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BuildDependency(pair.Key, pair.Value))
            .ToArray();

        return (generated, dependencyHashes);
    }

    private static async Task<bool> CanReuseOutputAsync(BuildManifestEntry entry, string optionsHash,
        byte[]? content, CancellationToken cancellationToken)
    {
        if (content == null || entry.OptionsHash != optionsHash)
        {
            return false;
        }

        if (BuildFiles.Hash(content) != entry.OutputHash)
        {
            return false;
        }

        return await DependenciesMatchAsync(entry.Dependencies, cancellationToken);
    }

    private static async Task<bool> DependenciesMatchAsync(IEnumerable<BuildDependency> dependencies, CancellationToken cancellationToken)
    {
        foreach (var dependency in dependencies)
        {
            var bytes = await BuildFiles.ReadIfPresentAsync(dependency.Path, cancellationToken);
            if ((bytes == null ? null : BuildFiles.Hash(bytes)) != dependency.Hash)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> GeneratorIdentityAsync(CancellationToken cancellationToken)
    {
        var assemblies = new[]
        {
            typeof(GenerationEngine).Assembly,
            typeof(ParserFacade).Assembly,
            typeof(Antlr4.Runtime.AntlrInputStream).Assembly
        };
        var identities = new List<string> { Environment.Version.ToString() };

        foreach (var assembly in assemblies.OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal))
        {
            // A reused build node can still hold an older module after files are replaced.
            var hash = BuildFiles.Hash(await File.ReadAllBytesAsync(assembly.Location, cancellationToken));
            identities.Add($"{assembly.GetName().Name}:{assembly.ManifestModule.ModuleVersionId}:{hash}");
        }

        return BuildFiles.HashText(string.Join("\n", identities));
    }
}
