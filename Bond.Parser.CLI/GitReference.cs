using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

/// <summary>Parses a schema as of a Git revision, reading its imports from the same revision.</summary>
internal static class GitReference
{
    public static async Task<ParseResult> ParseAsync(string reference, string input, IReadOnlyList<string> importDirectories,
        ParseOptions options)
    {
        var separator = reference.IndexOf('=');
        if (separator < 0 || reference[..separator] is not (".git#branch" or ".git#tag" or ".git#commit"))
        {
            throw new UsageException("Git references use .git#branch=<name>, .git#tag=<name>, or .git#commit=<hash>.");
        }

        var directory = Path.GetDirectoryName(input)!;

        // Relative to the input's directory rather than --show-toplevel (a physical path), so the root stays in the
        // same path space as the input when it is reached through a symbolic link.
        var root = Path.GetFullPath(Path.Combine(directory, (await Git(directory, "rev-parse", "--show-cdup")).Trim()));
        var revision = (await Git(root, "rev-parse", "--verify", "--end-of-options",
            reference[(separator + 1)..] + "^{commit}")).Trim();

        string? Relative(string path)
        {
            var relative = Path.GetRelativePath(root, path);
            return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)
                ? null
                : relative.Replace('\\', '/');
        }

        async Task<string?> Show(string relative)
        {
            var (exitCode, output, _) = await Run(root, "show", $"{revision}:{relative}");
            return exitCode == 0 ? output : null;
        }

        var inputPath = Relative(input) ?? throw new IOException("The input schema must be inside the selected Git repository.");
        var content = await Show(inputPath) ?? throw new IOException($"'{input}' does not exist at {revision}.");
        var directories = importDirectories.Select(Path.GetFullPath).ToArray();
        ImportResolver resolver = async (currentFile, importPath) =>
        {
            foreach (var candidate in DefaultImportResolver.Candidates(currentFile, importPath, directories))
            {
                // Imports outside the repository, such as shared import directories, come from the working tree.
                var imported = Relative(candidate) is { } relative
                    ? await Show(relative)
                    : File.Exists(candidate) ? await File.ReadAllTextAsync(candidate) : null;
                if (imported != null)
                {
                    return (candidate, imported);
                }
            }

            throw new FileNotFoundException($"Imported file not found at {revision}: {importPath}", importPath);
        };

        return await ParserFacade.ParseContentAsync(content, input, resolver, options);
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var (exitCode, output, error) = await Run(directory, arguments);
        return exitCode == 0 ? output : throw new IOException(error.Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output, await error);
        }
        catch (Win32Exception error)
        {
            throw new IOException($"Could not start git: {error.Message}", error);
        }
    }
}
