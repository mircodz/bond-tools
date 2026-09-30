using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

internal static class Diagnostics
{
    public static int Report(IEnumerable<ParseError> errors, string format, int exitCode)
    {
        var values = errors.ToArray();
        if (format == "json")
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                errors = values.Select(error => new
                {
                    file = error.FilePath,
                    line = error.Line,
                    column = error.Column,
                    message = error.Message
                })
            }));
        }
        else
        {
            foreach (var error in values)
            {
                Console.Error.WriteLine($"{error.FilePath ?? "bond"}({error.Line},{error.Column}): error: {error.Message}");
            }
        }

        return exitCode;
    }
}
