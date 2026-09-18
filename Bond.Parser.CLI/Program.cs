using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bond.Parser.Parser;
using Bond.Parser.Formatting;
using Bond.Parser.Compatibility;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Bond.Parser.CLI;

public static class Program
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine(CSharpGenerator.Version);
            return 0;
        }
        if (args.Length > 0 && args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
        {
            return await GenerateCommand.RunAsync(args[1..], Console.Out, Console.Error);
        }

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            ShowHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        return command switch
        {
            "breaking" => await RunBreakingCommand(rest),
            "parse" => await RunParseCommand(rest),
            "fmt" or "format" => await RunFormatCommand(rest),
            _ => UnknownCommand(command)
        };
    }

    static int UnknownCommand(string command)
    {
        WriteError($"Error: unknown command '{command}'. Run with --help to see available commands.");
        return 1;
    }

    static async Task<int> RunParseCommand(string[] args)
    {
        var parsed = new Args(args);
        var filePath = parsed.PositionalOrNull;
        if (filePath is null)
        {
            WriteError("Error: No file specified");
            ShowHelp();
            return 1;
        }

        var verbose = parsed.HasFlag("-v", "--verbose");
        var jsonOutput = parsed.HasFlag("--json");
        var ignoreImports = parsed.HasFlag("--ignore-imports");

        var result = await ParserFacade.ParseFileAsync(filePath, options: new ParseOptions(IgnoreImports: ignoreImports));

        if (!result.Success)
        {
            Console.Error.WriteLine($"parse failed: {filePath}");
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"{error.Line}:{error.Column}: {error.Message}");
                if (error.FilePath is not null)
                {
                    Console.Error.WriteLine($"  in {error.FilePath}");
                }
            }
            return 1;
        }

        if (result.Ast is not null)
        {
            if (jsonOutput) PrintJson(result.Ast);
            else PrintSummary(result.Ast, filePath, verbose);
        }

        return 0;
    }

    static async Task<int> RunBreakingCommand(string[] args)
    {
        var parsed = new Args(args);
        var filePath = parsed.PositionalOrNull;
        if (filePath is null)
        {
            WriteError("Error: No file specified");
            ShowHelp();
            return 1;
        }

        var against = parsed.GetValue("--against");
        if (against is null)
        {
            WriteError("Error: --against flag is required for breaking command");
            ShowHelp();
            return 1;
        }

        var errorFormat = parsed.GetValue("--error-format") ?? "text";
        var verbose = parsed.HasFlag("-v", "--verbose");
        var ignoreImports = parsed.HasFlag("--ignore-imports");

        var reference = await ResolveReference(against, filePath);
        if (reference is null)
        {
            WriteError($"Error: Could not resolve reference: {against}");
            return 1;
        }

        return await CheckBreaking(reference, filePath, errorFormat, verbose, ignoreImports);
    }

    static async Task<int> RunFormatCommand(string[] args)
    {
        var parsed = new Args(args);
        var filePath = parsed.PositionalOrNull;
        if (filePath is null)
        {
            WriteError("Error: No file specified");
            ShowHelp();
            return 1;
        }

        var check = parsed.HasFlag("--check");

        if (!File.Exists(filePath))
        {
            WriteError($"Error: File not found: {filePath}");
            return 1;
        }

        var content = await File.ReadAllTextAsync(filePath);
        var result = BondFormatter.Format(content, Path.GetFullPath(filePath));

        if (!result.Success)
        {
            Console.Error.WriteLine($"format failed: {filePath}");
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"{error.Line}:{error.Column}: {error.Message}");
                if (error.FilePath is not null)
                {
                    Console.Error.WriteLine($"  in {error.FilePath}");
                }
            }
            return 1;
        }

        if (result.FormattedText is null)
        {
            WriteError("Error: Format produced no output");
            return 1;
        }

        if (check)
        {
            if (!string.Equals(content, result.FormattedText, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{filePath} would be reformatted");
                return 1;
            }
            return 0;
        }

        if (!string.Equals(content, result.FormattedText, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(filePath, result.FormattedText);
        }

        return 0;
    }

    private sealed record ResolvedReference(
        string FilePath,
        string? Content,
        ImportResolver? ImportResolver);

    static async Task<ResolvedReference?> ResolveReference(string reference, string currentFilePath)
    {
        if (reference.StartsWith(".git#", StringComparison.Ordinal))
        {
            return await ResolveGitReference(reference, currentFilePath);
        }

        if (File.Exists(reference))
        {
            return new ResolvedReference(Path.GetFullPath(reference), null, null);
        }

        return null;
    }

    static async Task<ResolvedReference?> ResolveGitReference(string gitRef, string currentFilePath)
    {
        var parts = gitRef.Split('#');
        if (parts.Length != 2)
        {
            return null;
        }

        var refParts = parts[1].Split('=');
        if (refParts.Length != 2)
        {
            return null;
        }

        var refName = refParts[1];

        try
        {
            var gitRoot = await RunGitCommand("rev-parse --show-toplevel");
            if (gitRoot is null) return null;

            var fullPath = Path.GetFullPath(currentFilePath);
            var gitRelativePath = Path.GetRelativePath(gitRoot, fullPath).Replace('\\', '/');
            if (gitRelativePath.StartsWith("..") || Path.IsPathRooted(gitRelativePath))
            {
                return null;
            }

            var content = await RunGitCommand($"show {refName}:{gitRelativePath}", gitRoot);
            if (content is null) return null;

            var virtualPath = Path.GetFullPath(Path.Combine(gitRoot, gitRelativePath));
            var importResolver = CreateGitAwareImportResolver(gitRoot, refName);
            return new ResolvedReference(virtualPath, content, importResolver);
        }
        catch (Exception ex)
        {
            WriteError($"Error: failed to resolve git reference '{gitRef}': {ex.Message}");
            return null;
        }
    }

    static async Task<string?> RunGitCommand(string arguments, string? workingDirectory = null)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            CreateNoWindow = true
        };

        process.Start();
        // Read both streams concurrently to avoid deadlock if git fills its stderr buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                WriteError($"git {arguments}: {stderr.Trim()}");
            }
            return null;
        }

        return output.Trim();
    }

    static async Task<int> CheckBreaking(ResolvedReference oldSchema, string newFilePath, string errorFormat, bool verbose, bool ignoreImports)
    {
        var parseOptions = new ParseOptions(IgnoreImports: ignoreImports);
        ParseResult oldResult;
        if (oldSchema.Content is not null)
        {
            oldResult = await ParserFacade.ParseContentAsync(
                oldSchema.Content,
                oldSchema.FilePath,
                oldSchema.ImportResolver,
                parseOptions);
        }
        else
        {
            oldResult = await ParserFacade.ParseFileAsync(
                oldSchema.FilePath,
                oldSchema.ImportResolver,
                CancellationToken.None,
                parseOptions);
        }
        if (!oldResult.Success)
        {
            return OutputParseError(errorFormat, oldResult.Errors, "Failed to parse reference schema", oldSchema.FilePath);
        }

        var newResult = await ParserFacade.ParseFileAsync(newFilePath, options: parseOptions);
        if (!newResult.Success)
        {
            return OutputParseError(errorFormat, newResult.Errors, "Failed to parse current schema", newFilePath);
        }

        var checker = new CompatibilityChecker();
        var changes = checker.CheckCompatibility(oldResult.Ast!, newResult.Ast!);
        var hasBreaking = changes.Any(c => c.Category is ChangeCategory.BreakingWire or ChangeCategory.BreakingText);

        if (errorFormat == "json")
        {
            OutputJsonBreaking(changes);
            return hasBreaking ? 1 : 0;
        }

        if (hasBreaking)
        {
            foreach (var change in changes.Where(c => c.Category is ChangeCategory.BreakingWire or ChangeCategory.BreakingText))
            {
                Console.Error.WriteLine($"{change.Location}: {change.Description}");
            }
            return 1;
        }

        if (verbose)
        {
            foreach (var change in changes)
            {
                Console.WriteLine($"{change.Category}: {change.Location}: {change.Description}");
            }
        }
        return 0;
    }

    // git's object database is content-addressed and won't change for a given (ref, path)
    // during one CLI invocation; cache to avoid one process spawn per imported file.
    static ImportResolver CreateGitAwareImportResolver(string gitRoot, string refName)
    {
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);

        return async (currentFile, importPath) =>
        {
            var currentDir = Path.GetDirectoryName(currentFile) ?? gitRoot;
            var absolutePath = Path.GetFullPath(Path.Combine(currentDir, importPath));

            var relativePath = Path.GetRelativePath(gitRoot, absolutePath).Replace('\\', '/');
            var inRepo = !relativePath.StartsWith("..") && !Path.IsPathRooted(relativePath);
            if (inRepo)
            {
                if (!cache.TryGetValue(relativePath, out var cached))
                {
                    cached = await RunGitCommand($"show {refName}:{relativePath}", gitRoot);
                    cache[relativePath] = cached;
                }
                if (cached is not null)
                {
                    return (absolutePath, cached);
                }
            }

            if (File.Exists(absolutePath))
            {
                var content = await File.ReadAllTextAsync(absolutePath);
                return (absolutePath, content);
            }

            throw new FileNotFoundException($"Imported file not found: {importPath}", absolutePath);
        };
    }

    static int OutputParseError(string errorFormat, IReadOnlyList<ParseError> errors, string message, string filePath)
    {
        if (errorFormat == "json")
        {
            OutputJsonErrors("parse_error", errors, message);
        }
        else
        {
            WriteError($"{message}: {filePath}");
            foreach (var error in errors)
            {
                Console.WriteLine($"  {error.Message}");
            }
        }
        return 1;
    }

    // Suppress ANSI color sequences when stderr is redirected so they don't leak into pipes / log files.
    static void WriteError(string message)
    {
        if (!Console.IsErrorRedirected)
        {
            Console.ForegroundColor = ConsoleColor.Red;
        }
        Console.Error.WriteLine(message);
        if (!Console.IsErrorRedirected)
        {
            Console.ResetColor();
        }
    }

    static void OutputJson(object output) =>
        Console.WriteLine(JsonSerializer.Serialize(output, PrettyJson));

    static void OutputJsonErrors(string errorType, IReadOnlyList<ParseError> errors, string message) =>
        OutputJson(new
        {
            error = errorType,
            message,
            errors = errors.Select(e => new
            {
                line = e.Line,
                column = e.Column,
                message = e.Message,
                file = e.FilePath
            })
        });

    static void OutputJsonBreaking(List<SchemaChange> changes)
    {
        static string CategoryToString(ChangeCategory cat) => cat switch
        {
            ChangeCategory.BreakingWire => "breaking_wire",
            ChangeCategory.BreakingText => "breaking_text",
            _                           => "compatible",
        };

        OutputJson(new
        {
            changes = changes.Select(c => new
            {
                type           = CategoryToString(c.Category),
                location       = c.Location,
                description    = c.Description,
                recommendation = c.Recommendation
            }).ToArray()
        });
    }

    static void PrintJson(Bond.Parser.Syntax.Bond ast)
    {
        var json = JsonSerializer.Serialize(ast, BondJsonSerializerOptions.GetOptions());
        Console.WriteLine(json);
    }

    static void PrintSummary(Bond.Parser.Syntax.Bond ast, string filePath, bool verbose)
    {
        Console.WriteLine($"parse: {filePath}");

        foreach (var ns in ast.Namespaces)
        {
            Console.WriteLine(ns);
        }

        foreach (var import in ast.Imports)
        {
            Console.WriteLine($"import {import.FilePath}");
        }

        foreach (var decl in ast.Declarations)
        {
            Console.WriteLine($"{decl.Kind.ToLowerInvariant()} {decl.Name}");

            if (verbose)
            {
                PrintDeclarationDetails(decl);
            }
        }
    }

    static void PrintDeclarationDetails(Syntax.Declaration decl)
    {
        switch (decl)
        {
            case Syntax.StructDeclaration structDecl:
                foreach (var field in structDecl.Fields)
                {
                    Console.WriteLine($"  {field.Ordinal}: {field.Modifier} {field.Type} {field.Name}");
                }
                break;

            case Syntax.EnumDeclaration enumDecl:
                foreach (var constant in enumDecl.Constants)
                {
                    Console.WriteLine($"  {constant}");
                }
                break;

            case Syntax.ServiceDeclaration serviceDecl:
                foreach (var method in serviceDecl.Methods)
                {
                    Console.WriteLine($"  {method}");
                }
                break;

            case Syntax.AliasDeclaration aliasDecl:
                Console.WriteLine($"  = {aliasDecl.AliasedType}");
                break;
        }
    }

    static void ShowHelp()
    {
        Console.WriteLine("Bond Schema Compiler - Parses and validates Bond IDL files");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  bond parse <file.bond> [options]");
        Console.WriteLine("  bond breaking <file.bond> --against <reference> [options]");
        Console.WriteLine("  bond format <file.bond> [options]   (alias: bond fmt)");
        Console.WriteLine("  bond generate csharp <file.bond>... -o <output-dir> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  parse       Parse and validate a Bond schema file");
        Console.WriteLine("  breaking    Check for breaking changes against a reference schema");
        Console.WriteLine("  format      Format a Bond schema file (alias: fmt)");
        Console.WriteLine("  generate    Generate C# models (run bond generate csharp --help)");
        Console.WriteLine();
        Console.WriteLine("Parse Options:");
        Console.WriteLine("  -v, --verbose              Show detailed AST output");
        Console.WriteLine("  --json                     Output AST as JSON (Bond schema format)");
        Console.WriteLine("  --ignore-imports           Parse without resolving imports or types");
        Console.WriteLine();
        Console.WriteLine("Breaking Options:");
        Console.WriteLine("  --against <reference>      Reference schema to compare against (file path or .git#branch=name)");
        Console.WriteLine("  --error-format <format>    Output format: text, json (default: text)");
        Console.WriteLine("  --ignore-imports           Compare without resolving imports or types");
        Console.WriteLine();
        Console.WriteLine("Format Options:");
        Console.WriteLine("  --check                    Exit non-zero if formatting is needed");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  bond parse schema.bond");
        Console.WriteLine("  bond breaking schema.bond --against schema_v1.bond");
        Console.WriteLine("  bond breaking schema.bond --against .git#branch=main --error-format=json");
        Console.WriteLine("  bond format schema.bond");
        Console.WriteLine("  bond generate csharp schemas/order.bond -o out/generated");
        Console.WriteLine();
        Console.WriteLine("Global Options:");
        Console.WriteLine("  -h, --help                 Show this help message");
        Console.WriteLine("  --version                  Show the package version");
    }

    /// <summary>
    /// Tiny argument helper. The first non-flag arg is the positional (file path);
    /// flags are matched by exact name; values support both `--flag value` and `--flag=value`.
    /// </summary>
    private sealed class Args
    {
        private readonly string[] _args;

        public Args(string[] args) { _args = args; }

        public string? PositionalOrNull
        {
            get
            {
                for (var i = 0; i < _args.Length; i++)
                {
                    if (!_args[i].StartsWith('-')) return _args[i];
                }
                return null;
            }
        }

        public bool HasFlag(params string[] names)
        {
            foreach (var arg in _args)
            {
                foreach (var name in names)
                {
                    if (arg == name) return true;
                }
            }
            return false;
        }

        public string? GetValue(string name)
        {
            for (var i = 0; i < _args.Length; i++)
            {
                if (_args[i] == name && i + 1 < _args.Length)
                {
                    return _args[i + 1];
                }
                var prefix = name + "=";
                if (_args[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return _args[i][prefix.Length..];
                }
            }
            return null;
        }
    }
}
