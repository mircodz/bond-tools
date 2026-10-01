using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Formatting;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

public static class Program
{
    private const string Help = """
        Bond schema tools

        Usage:
          bond check <schema.bond> [options]
          bond breaking <schema.bond> --against <reference> [options]
          bond format <schema.bond> [--check]                (alias: fmt)
          bond generate csharp <schema.bond>... -o <dir> [options]

        Common options:
          -I, --import-dir <dir>      Import search directory, searched after the importing file's directory (repeatable)
          --error-format text|json    Diagnostic format (default: text)
          -h, --help                  Show this help
          --version                   Show the version

        breaking:
          --against <reference>       A schema file, or .git#branch=<name>, .git#tag=<name>, .git#commit=<hash>
          --ignore-imports            Compare root declarations without resolving imports
          --suppress <ids>            Acknowledge diagnostic IDs (comma-separated, repeatable)
          --list-rules                List diagnostic IDs
          -v, --verbose               Include compatible and suppressed changes

        format:
          --check                     Report files that need formatting instead of rewriting them

        generate csharp (writes <input-name>.g.cs per input; requires Bond.Runtime.CSharp):
          -o, --output-dir <dir>      Output directory
          -n, --namespace <from=to>   Map a C# namespace (repeatable)
          -u, --using <namespace>     Add a using directive (repeatable)
          --type-map <alias=type>     Map a Bond alias to a CLR type; converters are user-supplied (repeatable)
          --clone                     Generate deep Clone()
          --equality                  Generate structural Equals() and GetHashCode()
          --to-string                 Generate ToString()
          --serialization             Generate Compact Binary serialization (requires BondTools.Runtime)

        Exit codes:
          0  success
          1  invalid schema, breaking change, unformatted file, or failed generation
          2  usage error, or an invalid schema passed to breaking
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case []:
                    Console.Error.WriteLine(Help);
                    return 2;
                case ["-h" or "--help"]:
                    Console.WriteLine(Help);
                    return 0;
                case ["--version"]:
                    Console.WriteLine(CSharpGenerator.Version);
                    return 0;
            }

            var rest = args[1..];
            return args[0].ToLowerInvariant() switch
            {
                "check" => await Check(rest),
                "breaking" => await BreakingCommand.Run(rest),
                "format" or "fmt" => await Format(rest),
                "generate" => await Generate(rest),
                var command => throw new UsageException($"Unknown command '{command}'.")
            };
        }
        catch (UsageException error)
        {
            Console.Error.WriteLine($"error: {error.Message} Run 'bond --help' for usage.");
            return 2;
        }
    }

    internal static int ShowHelp()
    {
        Console.WriteLine(Help);
        return 0;
    }

    private static async Task<int> Check(string[] rest)
    {
        var args = Arguments.Parse(rest, [], ["--import-dir", "--error-format"]);
        if (args.Has("--help"))
        {
            return ShowHelp();
        }

        var format = args.ErrorFormat();
        var input = Path.GetFullPath(args.Positional("schema file"));
        var result = await ParserFacade.ParseFileAsync(input, DefaultImportResolver.Create(args.All("--import-dir")));
        return result.Success ? 0 : Diagnostics.Report(result.Errors, format, exitCode: 1);
    }

    private static async Task<int> Format(string[] rest)
    {
        var args = Arguments.Parse(rest, ["--check"], ["--error-format"]);
        if (args.Has("--help"))
        {
            return ShowHelp();
        }

        var format = args.ErrorFormat();
        var input = args.Positional("schema file");
        var path = Path.GetFullPath(input);
        if (!File.Exists(path))
        {
            return Diagnostics.Report([new ParseError($"File not found: {path}", path, 0, 0)], format, exitCode: 1);
        }

        var content = await File.ReadAllTextAsync(path);
        var result = BondFormatter.Format(content, path);
        if (!result.Success)
        {
            return Diagnostics.Report(result.Errors, format, exitCode: 1);
        }

        if (result.FormattedText == content)
        {
            return 0;
        }

        if (args.Has("--check"))
        {
            Console.Error.WriteLine($"{input} would be reformatted");
            return 1;
        }

        await File.WriteAllTextAsync(path, result.FormattedText);
        return 0;
    }

    private static async Task<int> Generate(string[] rest)
    {
        var args = Arguments.Parse(rest, ["--clone", "--equality", "--to-string", "--serialization"],
            ["--output-dir", "--import-dir", "--namespace", "--using", "--type-map", "--error-format"]);
        if (args.Has("--help"))
        {
            return ShowHelp();
        }

        var format = args.ErrorFormat();
        if (args.Positionals is not [var language, _, ..])
        {
            throw new UsageException("Expected: bond generate csharp <schema.bond>... -o <dir>.");
        }

        if (!language.Equals("csharp", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException($"Unsupported language '{language}'; only csharp is supported.");
        }

        var output = Path.GetFullPath(args.Required("--output-dir"));
        var options = new CSharpGenerationOptions
        {
            UsingNamespaces = args.All("--using"),
            NamespaceMappings = args.All("--namespace"),
            TypeMappings = args.All("--type-map"),
            ModelFeatures = (args.Has("--clone") ? CSharpModelFeatures.Cloning : 0)
                | (args.Has("--equality") ? CSharpModelFeatures.Equality : 0)
                | (args.Has("--to-string") ? CSharpModelFeatures.StringRepresentation : 0),
            Serialization = args.Has("--serialization")
        };

        var resolver = DefaultImportResolver.Create(args.All("--import-dir"));
        var errors = new List<ParseError>();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in args.Positionals.Skip(1).Select(Path.GetFullPath))
        {
            var parsed = await ParserFacade.ParseFileAsync(input, resolver);
            var generated = parsed.Success ? CSharpGenerator.Generate(parsed.Ast!, input, options) : null;
            errors.AddRange(generated?.Errors ?? parsed.Errors);

            var path = Path.Combine(output, Path.GetFileNameWithoutExtension(input) + ".g.cs");
            if (!files.TryAdd(path, generated?.Code ?? ""))
            {
                errors.Add(new ParseError($"Another input also generates '{path}'.", input, 0, 0));
            }
        }

        if (errors.Count != 0)
        {
            return Diagnostics.Report(errors, format, exitCode: 1);
        }

        Directory.CreateDirectory(output);
        foreach (var (path, code) in files)
        {
            // Unchanged files keep their timestamps, so incremental builds are not invalidated.
            if (!File.Exists(path) || await File.ReadAllTextAsync(path) != code)
            {
                await File.WriteAllTextAsync(path, code);
            }

            Console.WriteLine(path);
        }

        return 0;
    }
}
