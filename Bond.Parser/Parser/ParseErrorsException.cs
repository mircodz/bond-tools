using System;
using System.Collections.Generic;

namespace Bond.Parser.Parser;

internal sealed class ParseErrorsException(IReadOnlyList<ParseError> errors) : Exception("Failed to parse imported schema.")
{
    public IReadOnlyList<ParseError> Errors { get; } = errors;
}
