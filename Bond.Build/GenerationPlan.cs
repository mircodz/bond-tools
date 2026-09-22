using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BondTools.Build;

internal sealed record PreparedOutput(string Path, BuildManifestEntry Entry, byte[]? NewContent, bool Reused);

internal sealed record GenerationPlan(
    BuildRequest Request,
    string GeneratorIdentity,
    PreparedOutput[] Outputs,
    string[] RemovedOutputs)
{
    internal async Task<GenerationResult> ApplyAsync(CancellationToken cancellationToken)
    {
        foreach (var output in Outputs)
        {
            if (output.NewContent == null)
            {
                continue;
            }

            try
            {
                // Recheck ownership in case a file changed after preparation.
                await BuildFiles.ReadOwnedOutputAsync(output.Path, cancellationToken);
                await BuildFiles.WriteAtomicAsync(output.Path, output.NewContent, cancellationToken);
            }
            catch (Exception error) when (GenerationFailureException.IsExpected(error))
            {
                throw new GenerationFailureException(output.Path, error);
            }
        }

        foreach (var path in RemovedOutputs)
        {
            try
            {
                await BuildFiles.ReadOwnedOutputAsync(path, cancellationToken);
                File.Delete(path);
            }
            catch (Exception error) when (GenerationFailureException.IsExpected(error))
            {
                throw new GenerationFailureException(path, error);
            }
        }

        var manifestPath = Path.Combine(Request.OutputDirectory, "manifest.json");
        await SaveManifestAsync(manifestPath, cancellationToken);

        var skippedCount = Outputs.Count(output => output.Reused);
        var generatedCount = Outputs.Length - skippedCount;
        var status = generatedCount == 0 && RemovedOutputs.Length == 0
            ? $"Bond: up-to-date ({skippedCount} schemas)."
            : $"Bond: generated {generatedCount}, skipped {skippedCount}, removed {RemovedOutputs.Length}.";

        // MSBuild needs every compile and clean item, including outputs reused without a write.
        var generatedFiles = Outputs.Select(output => output.Path).ToArray();
        return new GenerationResult(generatedFiles, generatedFiles.Append(manifestPath).ToArray(), [], status);
    }

    private async Task SaveManifestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var manifest = new BuildManifest
            {
                Version = 1,
                ProjectFile = Request.ProjectFile,
                OutputDirectory = Request.OutputDirectory,
                GeneratorIdentity = GeneratorIdentity,
                RequestHash = BuildFiles.HashText(JsonSerializer.Serialize(Request)),
                Entries = Outputs.Select(output => output.Entry).ToArray()
            };
            await BuildFiles.WriteAtomicAsync(path,
                JsonSerializer.SerializeToUtf8Bytes(manifest, BuildManifest.JsonOptions), cancellationToken);
        }
        catch (Exception error) when (GenerationFailureException.IsExpected(error))
        {
            throw new GenerationFailureException(path, error);
        }
    }
}
