using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;

namespace BondTools.Build;

internal static class BuildFiles
{
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static string FullPath(string path, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
        {
            throw new InvalidDataException("Bond paths must be nonempty and cannot contain control characters.");
        }

        return baseDirectory == null ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDirectory);
    }

    internal static bool IsRelativeChild(string path) =>
        !Path.IsPathRooted(path) && path != "." && path != ".."
        && !path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !path.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    internal static string OutputPath(string root, string relative)
    {
        if (!IsRelativeChild(relative))
        {
            throw new InvalidDataException("A Bond output path escapes its intermediate directory.");
        }

        var path = FullPath(relative, root);
        if (!IsRelativeChild(Path.GetRelativePath(root, path)))
        {
            throw new InvalidDataException("A Bond output path escapes its intermediate directory.");
        }

        return path;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string HashText(string text) => Hash(Encoding.UTF8.GetBytes(text));

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));

    internal static async Task<byte[]?> ReadIfPresentAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static string Text(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void EnsureSafePath(string path, bool directory)
    {
        var current = path;
        while (current != null)
        {
            FileAttributes? attributes = null;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            if (attributes is { } existing)
            {
                if ((existing & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Bond output paths cannot be symbolic links: {current}");
                }

                if (((existing & FileAttributes.Directory) != 0) != directory)
                {
                    throw new IOException($"Bond output path has the wrong file/directory kind: {current}");
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (parent != null && Directory.Exists(parent))
            {
                var name = Path.GetFileName(current);
                foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
                {
                    if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Path.GetFileName(entry), name, StringComparison.Ordinal))
                    {
                        throw new IOException($"Bond output path collides with an existing entry (case-insensitive): {current}");
                    }
                }
            }

            current = parent;
            directory = true;
        }
    }

    internal static async Task<byte[]?> ReadOwnedOutputAsync(string path, CancellationToken cancellationToken)
    {
        EnsureSafePath(path, directory: false);
        var bytes = await ReadIfPresentAsync(path, cancellationToken);
        if (bytes == null)
        {
            return null;
        }

        var text = Text(bytes);
        if (text != CSharpGenerator.GeneratedHeader
            && !text.StartsWith(CSharpGenerator.GeneratedHeader + "\n", StringComparison.Ordinal)
            && !text.StartsWith(CSharpGenerator.GeneratedHeader + "\r\n", StringComparison.Ordinal))
        {
            throw new IOException($"Refusing to overwrite or remove a non-generated file: {path}");
        }

        return bytes;
    }

    internal static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        EnsureSafePath(path, directory: false);
        var existing = await ReadIfPresentAsync(path, cancellationToken);
        if (existing != null && existing.AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            EnsureSafePath(path, directory: false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
