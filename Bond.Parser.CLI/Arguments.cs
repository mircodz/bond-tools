using System;
using System.Collections.Generic;
using System.Linq;

namespace Bond.Parser.CLI;

internal sealed class UsageException(string message) : Exception(message);

/// <summary>Positional arguments, flags, and options with values (<c>--name value</c> or <c>--name=value</c>).</summary>
internal sealed class Arguments
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-h"] = "--help",
        ["-v"] = "--verbose",
        ["-I"] = "--import-dir",
        ["-o"] = "--output-dir",
        ["-n"] = "--namespace",
        ["-u"] = "--using"
    };

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];

    public static Arguments Parse(IReadOnlyList<string> args, string[] flags, string[] options)
    {
        var result = new Arguments();
        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            if (argument == "--")
            {
                result.Positionals.AddRange(args.Skip(i + 1));
                break;
            }

            if (argument.Length < 2 || argument[0] != '-')
            {
                result.Positionals.Add(argument);
                continue;
            }

            var separator = argument.IndexOf('=');
            var name = separator < 0 ? argument : argument[..separator];
            name = Aliases.GetValueOrDefault(name, name);
            var value = separator < 0 ? null : argument[(separator + 1)..];
            if (name == "--help" || flags.Contains(name))
            {
                if (value != null)
                {
                    throw new UsageException($"Option '{name}' does not take a value.");
                }
            }
            else if (options.Contains(name))
            {
                if (value == null && i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                {
                    value = args[++i];
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new UsageException($"Option '{name}' requires a value.");
                }
            }
            else
            {
                throw new UsageException($"Unknown option '{name}'.");
            }

            if (!result._options.TryGetValue(name, out var values))
            {
                result._options[name] = values = [];
            }

            values.Add(value ?? "");
        }

        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public IReadOnlyList<string> All(string name) => _options.GetValueOrDefault(name) ?? [];

    public string? Value(string name) => All(name) switch
    {
        [] => null,
        [var value] => value,
        _ => throw new UsageException($"Option '{name}' may only be specified once.")
    };

    public string Required(string name) => Value(name) ?? throw new UsageException($"Option '{name}' is required.");

    public string Positional(string description) => Positionals switch
    {
        [var value] => value,
        [] => throw new UsageException($"A {description} is required."),
        _ => throw new UsageException($"Only one {description} may be specified.")
    };

    public string ErrorFormat() => Value("--error-format") switch
    {
        null or "text" => "text",
        "json" => "json",
        var format => throw new UsageException($"Unsupported error format '{format}'; expected text or json.")
    };
}
