using System.Collections.Generic;
using System.IO;
using Antlr4.Runtime;

namespace Bond.Parser.Parser;

public sealed class ErrorListener(string? path = null) : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
{
    private readonly List<ParseError> _errors = [];

    public IReadOnlyList<ParseError> Errors => _errors;

    public void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        IToken offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e)
    {
        _errors.Add(new ParseError(msg, path, line, charPositionInLine + 1));
    }

    public void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        int offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e) =>
        _errors.Add(new ParseError(msg, path, line, charPositionInLine + 1));
}
