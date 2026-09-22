using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.Parser;

namespace Bond.Parser.CLI;

internal static class SchemaDiagnostics
{
    internal static async Task<int> WriteAsync(TextWriter writer, string format, IEnumerable<ParseError> errors,
        int exitCode = 2)
    {
        var values = errors.ToArray();
        if (format == "json")
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                error = "schema_error",
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
                await writer.WriteLineAsync($"{error.FilePath ?? "bond"}({error.Line},{error.Column}): error: {error.Message}");
            }
        }

        return exitCode;
    }
}
