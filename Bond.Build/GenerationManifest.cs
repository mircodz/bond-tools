using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BondTools.Build;

internal sealed record BuildDependency(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string? Hash);

internal sealed record BuildManifestEntry
{
    public required string Source { get; init; }
    public required string Output { get; init; }
    public required string OptionsHash { get; init; }
    public required string OutputHash { get; init; }
    public required BuildDependency[] Dependencies { get; init; }
}

internal sealed record BuildManifest
{
    public required int Version { get; init; }
    public required string ProjectFile { get; init; }
    public required string OutputDirectory { get; init; }
    public required string GeneratorIdentity { get; init; }
    public required string RequestHash { get; init; }
    public required BuildManifestEntry[] Entries { get; init; }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task<BuildManifest?> ReadAsync(string path, BuildRequest request, CancellationToken cancellationToken)
    {
        BuildFiles.EnsureSafePath(path, directory: false);
        var bytes = await BuildFiles.ReadIfPresentAsync(path, cancellationToken);
        if (bytes == null)
        {
            return null;
        }

        try
        {
            var manifest = ValidateIdentity(JsonSerializer.Deserialize<BuildManifest>(bytes, JsonOptions), request);
            ValidateEntries(manifest.Entries, request);
            return manifest;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"Bond manifest '{path}' is invalid. Run dotnet clean and rebuild, " +
                $"or remove this manifest and rebuild. {error.Message}", error);
        }
    }

    private static BuildManifest ValidateIdentity(BuildManifest? manifest, BuildRequest request)
    {
        // JSON can contain explicit nulls even for required, non-nullable properties.
        if (manifest == null || manifest.Version != 1
            || !BuildFiles.PathComparer.Equals(manifest.ProjectFile, request.ProjectFile)
            || !BuildFiles.PathComparer.Equals(manifest.OutputDirectory, request.OutputDirectory)
            || !BuildFiles.IsHash(manifest.GeneratorIdentity) || !BuildFiles.IsHash(manifest.RequestHash)
            || manifest.Entries == null)
        {
            throw new InvalidDataException("Invalid manifest identity or version.");
        }

        return manifest;
    }

    private static void ValidateEntries(BuildManifestEntry[] entries, BuildRequest request)
    {
        var sources = new HashSet<string>(BuildFiles.PathComparer);
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry == null || !CanonicalPath(entry.Source) || !sources.Add(entry.Source)
                || !string.Equals(Path.GetExtension(entry.Source), ".bond", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Invalid manifest output entry.");
            }

            if (entry.Output != BuildRequest.OutputFor(request.ProjectFile, entry.Source) || !outputs.Add(entry.Output))
            {
                throw new InvalidDataException("Invalid manifest output entry.");
            }

            if (!BuildFiles.IsHash(entry.OptionsHash) || !BuildFiles.IsHash(entry.OutputHash) || entry.Dependencies == null)
            {
                throw new InvalidDataException("Invalid manifest output entry.");
            }

            BuildFiles.OutputPath(request.OutputDirectory, entry.Output);
            ValidateDependencies(entry);
        }
    }

    private static void ValidateDependencies(BuildManifestEntry entry)
    {
        var paths = new HashSet<string>(BuildFiles.PathComparer);
        foreach (var dependency in entry.Dependencies)
        {
            if (dependency == null || !CanonicalPath(dependency.Path) || !paths.Add(dependency.Path)
                || (dependency.Hash != null && !BuildFiles.IsHash(dependency.Hash)))
            {
                throw new InvalidDataException("Invalid manifest dependency.");
            }
        }

        if (!entry.Dependencies.Any(dependency =>
            BuildFiles.PathComparer.Equals(dependency.Path, entry.Source) && dependency.Hash != null))
        {
            throw new InvalidDataException("Manifest does not include the root input hash.");
        }
    }

    private static bool CanonicalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && string.Equals(path, BuildFiles.FullPath(path), StringComparison.Ordinal);
}
