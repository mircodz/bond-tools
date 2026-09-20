using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

internal static class SchemaFiles
{
    internal static ImportResolver ImportResolver(IEnumerable<string> directories, CancellationToken cancellationToken)
    {
        var imports = directories.Select(Path.GetFullPath).ToArray();
        return async (currentFile, importPath) =>
        {
            foreach (var candidate in ImportCandidates(currentFile, importPath, imports))
            {
                var content = await ReadIfPresent(candidate, cancellationToken);
                if (content != null) return (candidate, content);
            }
            throw new FileNotFoundException($"Imported file not found: {importPath}", importPath);
        };
    }

    internal static IEnumerable<string> ImportCandidates(string currentFile, string importPath, IEnumerable<string> directories)
    {
        var name = importPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return new[] { Path.GetDirectoryName(currentFile)! }.Concat(directories)
            .Select(directory => Path.GetFullPath(Path.Combine(directory, name)));
    }

    internal static async Task<string?> ReadIfPresent(string path, CancellationToken cancellationToken)
    {
        try { return await File.ReadAllTextAsync(path, cancellationToken); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static async Task<(string Content, ImportResolver Resolver)> ReadGitReference(
        string reference, string input, IEnumerable<string> directories, CancellationToken cancellationToken)
    {
        var separator = reference.IndexOf('=');
        if (separator < 0 || reference[..separator] is not (".git#branch" or ".git#tag" or ".git#commit"))
            throw new ArgumentException("Git references use .git#branch=name, .git#tag=name, or .git#commit=hash.");
        var root = (await Git(Path.GetDirectoryName(input)!, cancellationToken, "rev-parse", "--show-toplevel")).Trim();
        var revision = (await Git(root, cancellationToken, "rev-parse", "--verify", "--end-of-options",
            reference[(separator + 1)..] + "^{commit}")).Trim();
        var relative = Path.GetRelativePath(root, input);
        if (!WithinRepository(relative))
            throw new ArgumentException("The input schema must be inside the selected Git repository.");
        var content = await Git(root, cancellationToken, "show", revision + ":" + relative.Replace('\\', '/'));
        var imports = directories.Select(Path.GetFullPath).ToArray();
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        ImportResolver resolver = async (currentFile, importPath) =>
        {
            foreach (var candidate in ImportCandidates(currentFile, importPath, imports))
            {
                var path = Path.GetRelativePath(root, candidate);
                string? imported;
                if (WithinRepository(path))
                {
                    if (!cache.TryGetValue(path, out imported))
                    {
                        var result = await RunGit(root, cancellationToken, "show", revision + ":" + path.Replace('\\', '/'));
                        imported = result.ExitCode == 0 ? result.Output : null;
                        cache[path] = imported;
                    }
                }
                else
                    imported = await ReadIfPresent(candidate, cancellationToken);
                if (imported != null) return (candidate, imported);
            }
            throw new FileNotFoundException($"Imported file not found at {revision}: {importPath}", importPath);
        };
        return (content, resolver);
    }

    private static bool WithinRepository(string relative) =>
        !Path.IsPathRooted(relative) && relative != ".."
        && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static async Task<string> Git(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await RunGit(directory, cancellationToken, arguments);
        if (result.ExitCode != 0)
            throw new IOException(result.Error.Trim());
        return result.Output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGit(
        string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}
