using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Bond.Parser.Parser;

/// <summary>
/// Resolves an `import "..."` statement to a canonical path + content. Allows
/// callers to override how imports are loaded (e.g., reading from git objects
/// instead of disk).
/// </summary>
public delegate Task<(string canonicalPath, string content)> ImportResolver(
    string currentFile,
    string importPath
);

public static class DefaultImportResolver
{
    /// <summary>Resolves imports relative to the importing file.</summary>
    public static Task<(string canonicalPath, string content)> Resolve(string currentFile, string importPath) =>
        Create([])(currentFile, importPath);

    /// <summary>Resolves imports relative to the importing file, then to each import directory in order.</summary>
    public static ImportResolver Create(IEnumerable<string> importDirectories)
    {
        var directories = importDirectories.Select(Path.GetFullPath).ToArray();
        return async (currentFile, importPath) =>
        {
            foreach (var candidate in Candidates(currentFile, importPath, directories))
            {
                if (File.Exists(candidate))
                {
                    return (candidate, await File.ReadAllTextAsync(candidate));
                }
            }

            throw new FileNotFoundException($"Imported file not found: {importPath}", importPath);
        };
    }

    /// <summary>The paths an import may refer to, in search order.</summary>
    public static IEnumerable<string> Candidates(string currentFile, string importPath, IEnumerable<string> importDirectories)
    {
        var relativePath = importPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var currentDirectory = Path.GetDirectoryName(currentFile) ?? Directory.GetCurrentDirectory();
        return importDirectories.Prepend(currentDirectory)
            .Select(directory => Path.GetFullPath(Path.Combine(directory, relativePath)));
    }
}
