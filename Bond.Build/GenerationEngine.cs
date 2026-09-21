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

internal static class GenerationEngine
{
    internal static async Task<GenerationResult> RunAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var currentPath = request.OutputDirectory;
        try
        {
            BuildFiles.EnsureSafePath(request.OutputDirectory, directory: true);
            var manifestPath = Path.Combine(request.OutputDirectory, "manifest.json");
            currentPath = manifestPath;
            var previous = await BuildManifest.ReadAsync(manifestPath, request, cancellationToken);
            var identity = await GeneratorIdentityAsync(cancellationToken);
            var generatorUnchanged = previous?.GeneratorIdentity == identity;

            var oldEntries = previous?.Entries.ToDictionary(entry => entry.Source, BuildFiles.PathComparer)
                ?? new Dictionary<string, BuildManifestEntry>(BuildFiles.PathComparer);
            var entries = new List<BuildManifestEntry>();
            var pending = new List<(string Path, byte[] Content)>();
            var removed = new List<string>();
            var errors = new List<ParseError>();

            var generatedCount = 0;
            var skippedCount = 0;

            foreach (var input in request.Inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentPath = BuildFiles.OutputPath(request.OutputDirectory, input.Output);
                var existingOutput = await BuildFiles.ReadOwnedOutputAsync(currentPath, cancellationToken);
                var optionsHash = BuildFiles.HashText(JsonSerializer.Serialize(input));
                oldEntries.Remove(input.Source, out var oldEntry);

                if (generatorUnchanged && oldEntry != null
                    && await CanReuseOutputAsync(oldEntry, optionsHash, existingOutput, cancellationToken))
                {
                    entries.Add(oldEntry);
                    skippedCount++;
                    continue;
                }

                currentPath = input.Source;
                var (generated, dependencies) = await GenerateInputAsync(input, cancellationToken);
                if (!generated.Success)
                {
                    errors.AddRange(generated.Errors);
                    continue;
                }

                var contentBytes = Encoding.UTF8.GetBytes(generated.Code!);
                if (existingOutput == null || !existingOutput.AsSpan().SequenceEqual(contentBytes))
                {
                    pending.Add((BuildFiles.OutputPath(request.OutputDirectory, input.Output), contentBytes));
                }

                entries.Add(new BuildManifestEntry
                {
                    Source = input.Source,
                    Output = input.Output,
                    OptionsHash = optionsHash,
                    OutputHash = BuildFiles.Hash(contentBytes),
                    Dependencies = dependencies
                });
                generatedCount++;
            }

            foreach (var entry in oldEntries.Values)
            {
                currentPath = BuildFiles.OutputPath(request.OutputDirectory, entry.Output);
                if (await BuildFiles.ReadOwnedOutputAsync(currentPath, cancellationToken) != null)
                {
                    removed.Add(currentPath);
                }
            }

            if (errors.Count != 0)
            {
                return new GenerationResult([], [], errors, "");
            }

            // Validate every root, dependency, and owned output before changing any generated files.
            foreach (var output in pending)
            {
                currentPath = output.Path;
                await BuildFiles.ReadOwnedOutputAsync(output.Path, cancellationToken);
                await BuildFiles.WriteAtomicAsync(output.Path, output.Content, cancellationToken);
            }

            foreach (var output in removed)
            {
                currentPath = output;
                await BuildFiles.ReadOwnedOutputAsync(output, cancellationToken);
                File.Delete(output);
            }

            var manifest = new BuildManifest
            {
                Version = 1,
                ProjectFile = request.ProjectFile,
                OutputDirectory = request.OutputDirectory,
                GeneratorIdentity = identity,
                RequestHash = BuildFiles.HashText(JsonSerializer.Serialize(request)),
                Entries = entries.ToArray()
            };
            currentPath = manifestPath;
            await BuildFiles.WriteAtomicAsync(manifestPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest, BuildManifest.JsonOptions), cancellationToken);

            var outputs = entries.Select(entry => BuildFiles.OutputPath(request.OutputDirectory, entry.Output)).ToArray();
            var state = generatedCount == 0 && removed.Count == 0
                ? $"Bond: up-to-date ({skippedCount} schemas)."
                : $"Bond: generated {generatedCount}, skipped {skippedCount}, removed {removed.Count}.";
            return new GenerationResult(outputs, outputs.Append(manifestPath).ToArray(), [], state);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or JsonException)
        {
            return new GenerationResult([], [], [new ParseError(error.Message, currentPath, 0, 0)], "");
        }
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
