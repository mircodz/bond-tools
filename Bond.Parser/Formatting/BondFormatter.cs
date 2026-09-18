using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Antlr4.Runtime;
using Bond.Parser.Grammar;
using Bond.Parser.Parser;

namespace Bond.Parser.Formatting;

public sealed record FormatOptions(int IndentSize = 4);

public sealed record FormatResult(string? FormattedText, List<ParseError> Errors)
{
    public bool Success => Errors.Count == 0 && FormattedText != null;
}

public static class BondFormatter
{
    public static FormatResult Format(string content, string filePath, FormatOptions? options = null)
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

            var bondCtx = parser.bond();
            if (errorListener.Errors.Count > 0)
            {
                errors.AddRange(errorListener.Errors);
                return new FormatResult(null, errors);
            }

            tokenStream.Fill();
            var formatter = new ParseTreeFormatter(tokenStream, options ?? new FormatOptions());
            var formatted = formatter.Format(bondCtx);
            return new FormatResult(formatted, errors);
        }
        catch (Exception ex)
        {
            errors.Add(new ParseError($"Unexpected error: {ex.Message}", filePath, 0, 0));
            return new FormatResult(null, errors);
        }
    }

    private sealed class ParseTreeFormatter
    {
        private readonly CommonTokenStream _tokens;
        private readonly string _indent;

        private readonly Dictionary<int, List<IToken>> _standaloneLeading = new();
        private readonly Dictionary<int, List<IToken>> _inlineLeading = new();
        private readonly Dictionary<int, List<IToken>> _trailing = new();
        private readonly HashSet<int> _emittedComments = [];

        public ParseTreeFormatter(CommonTokenStream tokens, FormatOptions options)
        {
            _tokens = tokens;
            _indent = new string(' ', options.IndentSize);
            BuildCommentMap();
        }

        private void BuildCommentMap()
        {
            var allTokens = _tokens.GetTokens();
            IToken? prevDefault = null;

            for (int i = 0; i < allTokens.Count; i++)
            {
                var tok = allTokens[i];
                if (tok.Channel == TokenConstants.DefaultChannel)
                {
                    prevDefault = tok;
                }
                else if (tok.Type == BondLexer.LINE_COMMENT || tok.Type == BondLexer.COMMENT)
                {
                    IToken? nextDefault = null;
                    for (int j = i + 1; j < allTokens.Count; j++)
                    {
                        if (allTokens[j].Channel == TokenConstants.DefaultChannel)
                        {
                            nextDefault = allTokens[j];
                            break;
                        }
                    }

                    var afterSeparator = prevDefault?.Type is BondLexer.SEMI or BondLexer.COMMA;
                    var beforeTerminator = nextDefault?.Type is BondLexer.SEMI or BondLexer.COMMA or BondLexer.RBRACE;
                    if (tok.Type == BondLexer.COMMENT && nextDefault != null && tok.Line == nextDefault.Line
                        && !afterSeparator && !beforeTerminator)
                    {
                        if (!_inlineLeading.ContainsKey(nextDefault.TokenIndex))
                            _inlineLeading[nextDefault.TokenIndex] = new List<IToken>();
                        _inlineLeading[nextDefault.TokenIndex].Add(tok);
                    }
                    else if (prevDefault != null && tok.Line == prevDefault.Line)
                    {
                        if (!_trailing.TryGetValue(prevDefault.TokenIndex, out var comments))
                            _trailing[prevDefault.TokenIndex] = comments = [];
                        comments.Add(tok);
                    }
                    else if (nextDefault != null)
                    {
                        if (!_standaloneLeading.ContainsKey(nextDefault.TokenIndex))
                            _standaloneLeading[nextDefault.TokenIndex] = new List<IToken>();
                        _standaloneLeading[nextDefault.TokenIndex].Add(tok);
                    }
                }
            }
        }

        private void EmitStandaloneLeading(StringBuilder sb, int tokenIndex, string indent = "")
        {
            if (!_standaloneLeading.TryGetValue(tokenIndex, out var comments))
                return;
            foreach (var c in comments)
            {
                if (!_emittedComments.Add(c.TokenIndex))
                    continue;
                sb.Append(indent);
                sb.Append(c.Text.TrimEnd());
                sb.Append('\n');
            }
        }

        private void EmitInlineLeading(StringBuilder sb, int tokenIndex)
        {
            if (!_inlineLeading.TryGetValue(tokenIndex, out var comments))
                return;
            foreach (var c in comments)
            {
                if (!_emittedComments.Add(c.TokenIndex))
                    continue;
                sb.Append(c.Text.TrimEnd());
                sb.Append(' ');
            }
        }

        private void EmitTrailing(StringBuilder sb, IToken stop)
        {
            var index = stop.TokenIndex;
            while (true)
            {
                if (_trailing.TryGetValue(index, out var comments))
                {
                    foreach (var comment in comments)
                    {
                        if (_emittedComments.Add(comment.TokenIndex))
                            sb.Append(' ').Append(comment.Text.TrimEnd());
                    }
                }
                do { index++; }
                while (index < _tokens.Size && _tokens.Get(index).Channel != TokenConstants.DefaultChannel);
                if (index >= _tokens.Size || _tokens.Get(index).Type is not (BondLexer.SEMI or BondLexer.COMMA))
                    break;
            }
        }

        private void EmitClosingComments(StringBuilder sb, int tokenIndex)
        {
            EmitStandaloneLeading(sb, tokenIndex, _indent);
            if (_inlineLeading.ContainsKey(tokenIndex))
            {
                sb.Append(_indent);
                EmitInlineLeading(sb, tokenIndex);
                sb.Append('\n');
            }
        }

        private bool HasComments(int tokenIndex)
        {
            var hidden = _tokens.GetHiddenTokensToRight(tokenIndex);
            if (hidden == null) return false;
            return hidden.Any(t => t.Type == BondLexer.LINE_COMMENT || t.Type == BondLexer.COMMENT);
        }

        public string Format(BondParser.BondContext ctx)
        {
            var sections = new List<string>();

            var imports = ctx.import_();
            if (imports.Length > 0)
            {
                var parts = new List<string>();
                foreach (var imp in imports)
                    parts.Add(FormatImport(imp));
                sections.Add(string.Join("\n", parts));
            }

            var namespaces = ctx.@namespace();
            if (namespaces.Length > 0)
            {
                var parts = new List<string>();
                foreach (var ns in namespaces)
                    parts.Add(FormatNamespace(ns));
                sections.Add(string.Join("\n", parts));
            }

            var currentAliasGroup = new List<string>();
            var declSections = new List<string>();

            foreach (var decl in ctx.declaration())
            {
                if (decl.alias() != null)
                {
                    currentAliasGroup.Add(FormatDeclaration(decl));
                }
                else
                {
                    if (currentAliasGroup.Count > 0)
                    {
                        declSections.Add(string.Join("\n", currentAliasGroup));
                        currentAliasGroup.Clear();
                    }
                    declSections.Add(FormatDeclaration(decl));
                }
            }

            if (currentAliasGroup.Count > 0)
                declSections.Add(string.Join("\n", currentAliasGroup));

            sections.AddRange(declSections);
            var finalComments = new StringBuilder();
            EmitStandaloneLeading(finalComments, _tokens.Size - 1);
            if (finalComments.Length > 0)
                sections.Add(finalComments.ToString().TrimEnd('\n'));
            var unhandled = _tokens.GetTokens().FirstOrDefault(token =>
                token.Type is BondLexer.LINE_COMMENT or BondLexer.COMMENT && !_emittedComments.Contains(token.TokenIndex));
            if (unhandled != null)
                throw new InvalidOperationException($"Cannot preserve comment at {unhandled.Line}:{unhandled.Column + 1}; formatting was not applied.");
            return string.Join("\n\n", sections);
        }

        private string FormatImport(BondParser.Import_Context ctx)
        {
            var sb = new StringBuilder();
            EmitStandaloneLeading(sb, ctx.Start.TokenIndex);
            sb.Append($"import {ctx.STRING_LITERAL().GetText()};");
            EmitTrailing(sb, ctx.Stop);
            return sb.ToString();
        }

        private string FormatNamespace(BondParser.NamespaceContext ctx)
        {
            var sb = new StringBuilder();
            EmitStandaloneLeading(sb, ctx.Start.TokenIndex);
            var lang = ctx.language() != null ? $" {ctx.language().GetText()}" : "";
            sb.Append($"namespace{lang} {ctx.qualifiedName().GetText()}");
            EmitTrailing(sb, ctx.Stop);
            return sb.ToString();
        }

        private string FormatDeclaration(BondParser.DeclarationContext ctx)
        {
            if (ctx.structDecl() != null) return FormatStruct(ctx.structDecl());
            if (ctx.@enum() != null) return FormatEnum(ctx.@enum());
            if (ctx.service() != null) return FormatService(ctx.service());
            if (ctx.alias() != null) return FormatAlias(ctx.alias());
            if (ctx.forward() != null) return FormatForward(ctx.forward());
            return ctx.GetText();
        }

        private string FormatAlias(BondParser.AliasContext ctx)
        {
            var sb = new StringBuilder();
            EmitStandaloneLeading(sb, ctx.Start.TokenIndex);
            var typeParams = ctx.typeParameters() != null ? FormatTypeParameters(ctx.typeParameters()) : "";
            sb.Append($"using {ctx.identifier().GetText()}{typeParams} = {FormatType(ctx.type())};");
            EmitTrailing(sb, ctx.Stop);
            return sb.ToString();
        }

        private string FormatForward(BondParser.ForwardContext ctx)
        {
            var sb = new StringBuilder();
            EmitStandaloneLeading(sb, ctx.Start.TokenIndex);
            var typeParams = ctx.typeParameters() != null ? FormatTypeParameters(ctx.typeParameters()) : "";
            sb.Append($"struct {ctx.identifier().GetText()}{typeParams};");
            EmitTrailing(sb, ctx.Stop);
            return sb.ToString();
        }

        private string FormatStruct(BondParser.StructDeclContext ctx)
        {
            var sb = new StringBuilder();
            EmitStandaloneLeading(sb, ctx.Start.TokenIndex);

            if (ctx.attributes() != null)
            {
                var attrs = ctx.attributes().attribute();
                for (int i = 0; i < attrs.Length; i++)
                {
                    if (attrs[i].Start.TokenIndex != ctx.Start.TokenIndex)
                        EmitStandaloneLeading(sb, attrs[i].Start.TokenIndex);
                    sb.Append(FormatAttribute(attrs[i]));
                    sb.Append('\n');
                }
            }

            var name = ctx.identifier().GetText();
            var typeParams = ctx.typeParameters() != null ? FormatTypeParameters(ctx.typeParameters()) : "";

            if (ctx.structDef() != null)
            {
                var def = ctx.structDef();
                var baseClause = def.userType() != null ? $" : {FormatUserType(def.userType())}" : "";
                var header = $"struct {name}{typeParams}{baseClause}";
                var fields = def.field();

                if (fields.Length == 0 && !HasComments(def.LBRACE().Symbol.TokenIndex))
                {
                    sb.Append($"{header} {{}}");
                }
                else
                {
                    sb.Append(header);
                    sb.Append(" {");
                    EmitTrailing(sb, def.LBRACE().Symbol);
                    sb.Append('\n');
                    foreach (var field in fields)
                    {
                        sb.Append(FormatField(field));
                        sb.Append('\n');
                    }
                    EmitClosingComments(sb, def.RBRACE().Symbol.TokenIndex);
                    sb.Append('}');
                }
            }
            else if (ctx.structView() != null)
            {
                var view = ctx.structView();
                var baseType = view.qualifiedName().GetText();
                var viewFields = view.viewFieldList().identifier();
                sb.Append($"struct {name}{typeParams} view_of {baseType} {{");
                EmitTrailing(sb, view.LBRACE().Symbol);
                sb.Append('\n');
                for (int i = 0; i < viewFields.Length; i++)
                {
                    EmitStandaloneLeading(sb, viewFields[i].Start.TokenIndex, _indent);
                    sb.Append(_indent);
                    EmitInlineLeading(sb, viewFields[i].Start.TokenIndex);
                    sb.Append(viewFields[i].GetText());
                    if (i < viewFields.Length - 1)
                        sb.Append(',');
                    EmitTrailing(sb, viewFields[i].Stop);
                    sb.Append('\n');
                }
                EmitClosingComments(sb, view.RBRACE().Symbol.TokenIndex);
                sb.Append('}');
            }

            EmitTrailing(sb, ctx.structDef()?.RBRACE().Symbol ?? ctx.structView().RBRACE().Symbol);
            return sb.ToString().TrimEnd('\n');
        }

        private string FormatEnum(BondParser.EnumContext ctx)
        {
            var sb = new StringBuilder();
            EmitStandaloneLeading(sb, ctx.Start.TokenIndex);

            if (ctx.attributes() != null)
            {
                var attrs = ctx.attributes().attribute();
                for (int i = 0; i < attrs.Length; i++)
                {
                    if (attrs[i].Start.TokenIndex != ctx.Start.TokenIndex)
                        EmitStandaloneLeading(sb, attrs[i].Start.TokenIndex);
                    sb.Append(FormatAttribute(attrs[i]));
                    sb.Append('\n');
                }
            }

            var name = ctx.identifier().GetText();
            sb.Append($"enum {name} {{");
            EmitTrailing(sb, ctx.LBRACE().Symbol);
            sb.Append('\n');

            var constants = ctx.enumConstant();
            for (int i = 0; i < constants.Length; i++)
            {
                EmitStandaloneLeading(sb, constants[i].Start.TokenIndex, _indent);
                sb.Append(_indent);
                EmitInlineLeading(sb, constants[i].Start.TokenIndex);
                sb.Append(FormatEnumConstant(constants[i]));
                sb.Append(',');
                EmitTrailing(sb, constants[i].Stop);
                sb.Append('\n');
            }

            EmitClosingComments(sb, ctx.RBRACE().Symbol.TokenIndex);
            sb.Append('}');
            EmitTrailing(sb, ctx.RBRACE().Symbol);
            return sb.ToString().TrimEnd('\n');
        }

        private string FormatService(BondParser.ServiceContext ctx)
        {
            var sb = new StringBuilder();
            EmitStandaloneLeading(sb, ctx.Start.TokenIndex);

            if (ctx.attributes() != null)
            {
                var attrs = ctx.attributes().attribute();
                for (int i = 0; i < attrs.Length; i++)
                {
                    if (attrs[i].Start.TokenIndex != ctx.Start.TokenIndex)
                        EmitStandaloneLeading(sb, attrs[i].Start.TokenIndex);
                    sb.Append(FormatAttribute(attrs[i]));
                    sb.Append('\n');
                }
            }

            var name = ctx.identifier().GetText();
            var typeParams = ctx.typeParameters() != null ? FormatTypeParameters(ctx.typeParameters()) : "";
            var baseClause = ctx.serviceType() != null ? $" : {FormatServiceType(ctx.serviceType())}" : "";
            sb.Append($"service {name}{typeParams}{baseClause} {{");
            EmitTrailing(sb, ctx.LBRACE().Symbol);
            sb.Append('\n');

            foreach (var method in ctx.method())
            {
                sb.Append(FormatMethod(method));
                sb.Append('\n');
            }

            EmitClosingComments(sb, ctx.RBRACE().Symbol.TokenIndex);
            sb.Append('}');
            EmitTrailing(sb, ctx.RBRACE().Symbol);
            return sb.ToString().TrimEnd('\n');
        }

        private string FormatField(BondParser.FieldContext ctx)
        {
            var sb = new StringBuilder();

            if (ctx.attributes() != null)
            {
                foreach (var attr in ctx.attributes().attribute())
                {
                    EmitStandaloneLeading(sb, attr.Start.TokenIndex, _indent);
                    sb.Append(_indent);
                    sb.Append(FormatAttribute(attr));
                    sb.Append('\n');
                }
            }

            var ordinalIndex = ctx.fieldOrdinal().Start.TokenIndex;
            EmitStandaloneLeading(sb, ordinalIndex, _indent);
            sb.Append(_indent);
            EmitInlineLeading(sb, ordinalIndex);
            sb.Append(ctx.fieldOrdinal().GetText());
            sb.Append(": ");

            if (ctx.modifier() != null)
            {
                sb.Append(ctx.modifier().GetText());
                sb.Append(' ');
            }

            sb.Append(FormatFieldType(ctx.fieldType()));
            sb.Append(' ');
            sb.Append(ctx.fieldIdentifier().GetText());

            if (ctx.default_() != null)
            {
                sb.Append(" = ");
                sb.Append(FormatDefault(ctx.default_()));
            }

            sb.Append(';');
            EmitTrailing(sb, ctx.Stop);
            return sb.ToString();
        }

        private string FormatEnumConstant(BondParser.EnumConstantContext ctx)
        {
            var name = ctx.identifier().GetText();
            if (ctx.INTEGER_LITERAL() != null)
            {
                var minus = ctx.MINUS() != null ? "-" : "";
                return $"{name} = {minus}{ctx.INTEGER_LITERAL().GetText()}";
            }
            return name;
        }

        private string FormatMethod(BondParser.MethodContext ctx)
        {
            var sb = new StringBuilder();
            EmitStandaloneLeading(sb, ctx.Start.TokenIndex, _indent);

            if (ctx.attributes() != null)
            {
                var attrs = ctx.attributes().attribute();
                for (int i = 0; i < attrs.Length; i++)
                {
                    if (attrs[i].Start.TokenIndex != ctx.Start.TokenIndex)
                        EmitStandaloneLeading(sb, attrs[i].Start.TokenIndex, _indent);
                    sb.Append(_indent);
                    sb.Append(FormatAttribute(attrs[i]));
                    sb.Append('\n');
                }
            }

            sb.Append(_indent);
            string resultType = ctx.NOTHING() != null
                ? "nothing"
                : FormatMethodResultType(ctx.methodResultType());

            var methodName = ctx.identifier().GetText();
            string param = ctx.methodParameter() != null
                ? FormatMethodParameter(ctx.methodParameter())
                : "";

            sb.Append($"{resultType} {methodName}({param});");
            EmitTrailing(sb, ctx.Stop);
            return sb.ToString();
        }

        private string FormatAttribute(BondParser.AttributeContext ctx) =>
            $"[{ctx.qualifiedName().GetText()}({ctx.STRING_LITERAL().GetText()})]";

        private string FormatFieldType(BondParser.FieldTypeContext ctx)
        {
            if (ctx.type() != null) return FormatType(ctx.type());
            return ctx.GetText();
        }

        private string FormatType(BondParser.TypeContext ctx)
        {
            if (ctx.basicType() != null) return ctx.basicType().GetText();
            if (ctx.complexType() != null) return FormatComplexType(ctx.complexType());
            if (ctx.userType() != null) return FormatUserType(ctx.userType());
            return ctx.GetText();
        }

        private string FormatComplexType(BondParser.ComplexTypeContext ctx)
        {
            if (ctx.BLOB() != null) return "blob";
            if (ctx.LIST() != null) return $"list<{FormatType(ctx.type())}>";
            if (ctx.VECTOR() != null) return $"vector<{FormatType(ctx.type())}>";
            if (ctx.NULLABLE() != null) return $"nullable<{FormatType(ctx.type())}>";
            if (ctx.SET() != null) return $"set<{FormatKeyType(ctx.keyType())}>";
            if (ctx.MAP() != null) return $"map<{FormatKeyType(ctx.keyType())}, {FormatType(ctx.type())}>";
            if (ctx.BONDED() != null) return $"bonded<{FormatUserStructRef(ctx.userStructRef())}>";
            return ctx.GetText();
        }

        private string FormatKeyType(BondParser.KeyTypeContext ctx)
        {
            if (ctx.basicType() != null) return ctx.basicType().GetText();
            if (ctx.userType() != null) return FormatUserType(ctx.userType());
            return ctx.GetText();
        }

        private string FormatUserType(BondParser.UserTypeContext ctx)
        {
            var name = ctx.qualifiedName().GetText();
            return ctx.typeArgs() != null ? $"{name}{FormatTypeArgs(ctx.typeArgs())}" : name;
        }

        private string FormatUserStructRef(BondParser.UserStructRefContext ctx)
        {
            var name = ctx.qualifiedName().GetText();
            return ctx.typeArgs() != null ? $"{name}{FormatTypeArgs(ctx.typeArgs())}" : name;
        }

        private string FormatServiceType(BondParser.ServiceTypeContext ctx)
        {
            var name = ctx.qualifiedName().GetText();
            return ctx.typeArgs() != null ? $"{name}{FormatTypeArgs(ctx.typeArgs())}" : name;
        }

        private string FormatTypeArgs(BondParser.TypeArgsContext ctx) =>
            $"<{string.Join(", ", ctx.typeArg().Select(FormatTypeArg))}>";

        private string FormatTypeArg(BondParser.TypeArgContext ctx)
        {
            if (ctx.type() != null) return FormatType(ctx.type());
            var sign = ctx.MINUS() != null ? "-" : ctx.PLUS() != null ? "+" : "";
            return sign + ctx.INTEGER_LITERAL().GetText();
        }

        private string FormatTypeParameters(BondParser.TypeParametersContext ctx) =>
            $"<{string.Join(", ", ctx.typeParam().Select(FormatTypeParam))}>";

        private string FormatTypeParam(BondParser.TypeParamContext ctx)
        {
            var name = ctx.identifier().GetText();
            return ctx.constraint() != null ? $"{name}: value" : name;
        }

        private string FormatMethodResultType(BondParser.MethodResultTypeContext ctx)
        {
            if (ctx.VOID() != null) return "void";
            if (ctx.methodTypeStreaming() != null)
                return $"stream {FormatUserStructRef(ctx.methodTypeStreaming().userStructRef())}";
            if (ctx.methodTypeUnary() != null)
                return FormatUserStructRef(ctx.methodTypeUnary().userStructRef());
            return ctx.GetText();
        }

        private string FormatMethodInputType(BondParser.MethodInputTypeContext ctx)
        {
            if (ctx.VOID() != null) return "void";
            if (ctx.methodTypeStreaming() != null)
                return $"stream {FormatUserStructRef(ctx.methodTypeStreaming().userStructRef())}";
            if (ctx.methodTypeUnary() != null)
                return FormatUserStructRef(ctx.methodTypeUnary().userStructRef());
            return ctx.GetText();
        }

        private string FormatMethodParameter(BondParser.MethodParameterContext ctx)
        {
            var type = FormatMethodInputType(ctx.methodInputType());
            return ctx.identifier() != null ? $"{type} {ctx.identifier().GetText()}" : type;
        }

        private static string FormatDefault(BondParser.Default_Context ctx) => ctx.GetText();
    }
}
