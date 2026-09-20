using System;
using System.IO;
using System.Linq;
using Bond.Parser.Parser;
using Bond.Parser.Formatting;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bond.Parser.CLI;

public static class Program
{
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
        if (args.Length > 0 && args[0].Equals("breaking", StringComparison.OrdinalIgnoreCase))
            return await BreakingCommand.RunAsync(args[1..], Console.Out, Console.Error);

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            ShowHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        return command switch
        {
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
        Console.WriteLine("  --suppress <IDs>           Suppress selected diagnostic IDs");
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

    }
}
