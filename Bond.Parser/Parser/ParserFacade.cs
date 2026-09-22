using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime;
using Bond.Parser.Grammar;

namespace Bond.Parser.Parser;

public record ParseError(
    string Message,
    string? FilePath,
    int Line,
    int Column
);

public record ParseResult(
    Syntax.Bond? Ast,
    IReadOnlyList<ParseError> Errors
)
{
    public bool Success => Errors.Count == 0 && Ast != null;
}

public sealed record ParseOptions(bool IgnoreImports = false);

public static class ParserFacade
{
    public static async Task<ParseResult> ParseFileAsync(
        string filePath,
        ImportResolver? importResolver = null,
        CancellationToken cancellationToken = default,
        ParseOptions? options = null)
    {
        if (!File.Exists(filePath))
        {
            return new ParseResult(null, [new ParseError($"File not found: {filePath}", filePath, 0, 0)]);
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var absolutePath = Path.GetFullPath(filePath);

        return await ParseContentInternalAsync(
            content,
            absolutePath,
            importResolver ?? DefaultImportResolver.Resolve,
            options);
    }

    public static Task<ParseResult> ParseStringAsync(
        string content,
        ImportResolver? importResolver = null,
        ParseOptions? options = null) =>
        ParseContentInternalAsync(content, "<inline>", importResolver ?? DefaultImportResolver.Resolve, options);

    public static Task<ParseResult> ParseContentAsync(
        string content,
        string filePath,
        ImportResolver? importResolver = null,
        ParseOptions? options = null) =>
        ParseContentInternalAsync(content, filePath, importResolver ?? DefaultImportResolver.Resolve, options);

    private static async Task<ParseResult> ParseContentInternalAsync(
        string content,
        string filePath,
        ImportResolver importResolver,
        ParseOptions? options)
    {
        var errors = new List<ParseError>();

        try
        {
            var inputStream = new AntlrInputStream(content);
            var lexer = new BondLexer(inputStream);
            var tokenStream = new CommonTokenStream(lexer);
            var parser = new BondParser(tokenStream);

            var errorListener = new ErrorListener(filePath);
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(errorListener);
            parser.RemoveErrorListeners();
            parser.AddErrorListener(errorListener);

            var parseTree = parser.bond();
            if (errorListener.Errors.Count > 0)
            {
                return new ParseResult(null, errorListener.Errors);
            }

            var astBuilder = new AstBuilder(tokenStream);
            var ast = (Syntax.Bond)astBuilder.Visit(parseTree)!;

            if (options?.IgnoreImports == true)
            {
                return new ParseResult(ast, errors);
            }

            var symbolTable = new SymbolTable();
            var analyzer = new SemanticAnalyzer(symbolTable, importResolver, filePath);

            try
            {
                ast = await analyzer.AnalyzeAsync(ast);
            }
            catch (SemanticErrorException ex)
            {
                errors.Add(new ParseError(ex.Message, filePath, ex.Location.Line, ex.Location.Column));
                return new ParseResult(ast, errors);
            }
            catch (ParseErrorsException ex)
            {
                errors.AddRange(ex.Errors);
                return new ParseResult(ast, errors);
            }
            catch (Exception ex)
            {
                errors.Add(new ParseError(ex.Message, filePath, 0, 0));
                return new ParseResult(ast, errors);
            }

            return new ParseResult(ast, errors);
        }
        catch (SemanticErrorException ex)
        {
            errors.Add(new ParseError(ex.Message, filePath, ex.Location.Line, ex.Location.Column));
            return new ParseResult(null, errors);
        }
        catch (Exception ex)
        {
            errors.Add(new ParseError($"Unexpected error: {ex.Message}", filePath, 0, 0));
            return new ParseResult(null, errors);
        }
    }
}
