namespace Bond.Parser.Syntax;

public enum TriviaKind
{
    LineComment, BlockComment
}

/// <summary>A comment attached to a node. Text is the raw comment including markers.</summary>
public record Trivia(TriviaKind Kind, string Text, SourceLocation Location);
