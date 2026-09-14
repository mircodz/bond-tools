using System.IO;
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
    public static async Task<(string canonicalPath, string content)> Resolve(
        string currentFile,
        string importPath)
    {
        var currentDir = Path.GetDirectoryName(currentFile) ?? Directory.GetCurrentDirectory();
        var relativePath = importPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(Path.Combine(currentDir, relativePath));

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Imported file not found: {importPath}", absolutePath);
        }

        var content = await File.ReadAllTextAsync(absolutePath);
        return (absolutePath, content);
    }
}
