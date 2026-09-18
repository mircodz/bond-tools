using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using Bond.Parser.Syntax;

namespace Bond.Parser.CodeGeneration;

public static partial class CSharpGenerator
{
    private sealed partial class Emitter
    {
        private void EmitDocumentation(Trivia[] leading, Trivia? trailing, int indent)
        {
            var lines = new List<string>();
            Trivia? previous = null;
            foreach (var trivia in leading)
            {
                if (previous is not null && trivia.Location.Line > previous.Location.Line + previous.Text.Count(c => c == '\n') + 1)
                    lines.Add("");
                lines.AddRange(DocumentationLines(trivia));
                previous = trivia;
            }
            if (trailing is not null)
                lines.AddRange(DocumentationLines(trailing));

            var start = lines.FindIndex(line => line.Length != 0);
            if (start < 0)
                return;
            var end = lines.FindLastIndex(line => line.Length != 0);
            Line(indent, "/// <summary>");
            for (var i = start; i <= end; i++)
                Line(indent, "/// " + EscapeDocumentation(lines[i]));
            Line(indent, "/// </summary>");
        }

        private static string[] DocumentationLines(Trivia trivia)
        {
            var text = trivia.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (trivia.Kind == TriviaKind.LineComment)
            {
                var marker = text.StartsWith("///", StringComparison.Ordinal) ? 3 : 2;
                return [RemoveCommentSpace(text[marker..]).TrimEnd(' ', '\t')];
            }

            text = text[2..^2];
            if (text.StartsWith('*'))
                text = text[1..];
            var lines = text.Split('\n');
            lines[0] = RemoveCommentSpace(lines[0]);
            var margin = lines.Skip(1).Where(line => line.Trim(' ', '\t').Length != 0)
                .Select(line => line.Length - line.TrimStart(' ', '\t').Length)
                .DefaultIfEmpty(0).Min();
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                line = line[Math.Min(margin, line.Length)..];
                if (line.StartsWith('*') && (line.Length == 1 || line[1] is ' ' or '\t'))
                    line = RemoveCommentSpace(line[1..]);
                lines[i] = line;
            }

            lines = lines.Select(line => line.TrimEnd(' ', '\t')).ToArray();
            var start = Array.FindIndex(lines, line => line.Length != 0);
            return start < 0 ? [] : lines[start..(Array.FindLastIndex(lines, line => line.Length != 0) + 1)];
        }

        private static string RemoveCommentSpace(string text) =>
            text.Length != 0 && text[0] is ' ' or '\t' ? text[1..] : text;

        private static string EscapeDocumentation(string text)
        {
            var escaped = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                switch (character)
                {
                    case '&': escaped.Append("&amp;"); break;
                    case '<': escaped.Append("&lt;"); break;
                    case '>': escaped.Append("&gt;"); break;
                    case '\u0085':
                    case '\u2028':
                    case '\u2029':
                        escaped.Append("&#x").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture)).Append(';');
                        break;
                    default:
                        if (char.IsHighSurrogate(character) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                            escaped.Append(character).Append(text[++i]);
                        else if (XmlConvert.IsXmlChar(character))
                            escaped.Append(character);
                        else
                            escaped.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                        break;
                }
            }
            return escaped.ToString();
        }
    }
}
