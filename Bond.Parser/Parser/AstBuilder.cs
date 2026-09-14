using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Bond.Parser.Grammar;
using Bond.Parser.Syntax;

namespace Bond.Parser.Parser;

/// <summary>Builds the AST from an ANTLR parse tree.</summary>
public class AstBuilder : BondBaseVisitor<object?>
{
    private readonly List<Namespace> _currentNamespaces = [];
    private readonly List<TypeParam> _currentTypeParams = [];

    /// <summary>Token stream used to recover comments from the hidden channel; null disables trivia.</summary>
    private readonly BufferedTokenStream? _tokens;

    public AstBuilder()
    {
    }

    public AstBuilder(BufferedTokenStream tokens)
    {
        _tokens = tokens;
    }

    public override object? Visit(IParseTree tree)
    {
        try
        {
            return base.Visit(tree);
        }
        catch (Exception error) when (error is FormatException or OverflowException && tree is ParserRuleContext)
        {
            var token = ((ParserRuleContext)tree).Start;
            throw new SemanticErrorException(error.Message, new SourceLocation(token.Line, token.Column + 1));
        }
    }

    /// <summary>Comments on the hidden channel immediately before <paramref name="start"/>.</summary>
    private Trivia[] LeadingTriviaFor(IToken start)
    {
        var hidden = _tokens?.GetHiddenTokensToLeft(start.TokenIndex);
        return ToTrivia(hidden);
    }

    /// <summary>
    /// The single comment on the same line, to the right of <paramref name="stop"/>. Skips a
    /// trailing field/constant separator (<c>;</c> or <c>,</c>) so <c>0: int32 x; // c</c> works.
    /// </summary>
    private Trivia? TrailingTriviaFor(IToken stop)
    {
        if (_tokens is null)
        {
            return null;
        }

        for (var i = stop.TokenIndex + 1; i < _tokens.Size; i++)
        {
            var token = _tokens.Get(i);
            if (token.Type == TokenConstants.EOF || token.Line != stop.Line)
            {
                break;
            }

            switch (token.Type)
            {
                case BondLexer.COMMENT:
                case BondLexer.LINE_COMMENT:
                    return MakeTrivia(token);
                case BondLexer.SEMI:
                case BondLexer.COMMA:
                case BondLexer.WS:
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    private static Trivia[] ToTrivia(IList<IToken>? tokens)
    {
        if (tokens is null)
        {
            return [];
        }

        var result = new List<Trivia>();
        foreach (var token in tokens)
        {
            if (token.Type is BondLexer.COMMENT or BondLexer.LINE_COMMENT)
            {
                result.Add(MakeTrivia(token));
            }
        }

        return result.Count == 0 ? [] : result.ToArray();
    }

    private static Trivia MakeTrivia(IToken token) =>
        new(
            token.Type == BondLexer.LINE_COMMENT ? TriviaKind.LineComment : TriviaKind.BlockComment,
            token.Text.Trim(),
            new SourceLocation(token.Line, token.Column + 1));

    public override Syntax.Bond VisitBond(BondParser.BondContext context)
    {
        var imports = context.import_()
            .Select(i => (Import)Visit(i)!)
            .ToArray();

        var namespaces = context.@namespace()
            .Select(n => (Namespace)Visit(n)!)
            .ToArray();

        _currentNamespaces.AddRange(namespaces);

        var declarations = context.declaration()
            .Select(d => (Declaration)Visit(d)!)
            .ToArray();

        return new Syntax.Bond(imports, namespaces, declarations);
    }

    public override Import VisitImport_(BondParser.Import_Context context)
    {
        var path = context.STRING_LITERAL().GetText()[1..^1];
        return new Import(path);
    }

    public override Namespace VisitNamespace(BondParser.NamespaceContext context)
    {
        var lang = context.language() != null
            ? (Language?)Visit(context.language())
            : null;

        var name = (string[])Visit(context.qualifiedName())!;
        return new Namespace(lang, name);
    }

    public override object VisitLanguage(BondParser.LanguageContext context)
    {
        return context.GetText() switch
        {
            "cpp" => Language.Cpp,
            "csharp" or "cs" => Language.Cs,
            "java" => Language.Java,
            _ => throw new InvalidOperationException($"Unknown language: {context.GetText()}")
        };
    }

    public override string VisitIdentifier(BondParser.IdentifierContext context)
    {
        return context.GetText();
    }

    public override string[] VisitQualifiedName(BondParser.QualifiedNameContext context)
    {
        return context.identifier().Select(id => (string)Visit(id)!).ToArray();
    }

    public override Declaration VisitDeclaration(BondParser.DeclarationContext context)
    {
        if (context.forward() != null)
        {
            return (Declaration)Visit(context.forward())!;
        }

        if (context.alias() != null)
        {
            return (Declaration)Visit(context.alias())!;
        }

        if (context.structDecl() != null)
        {
            return (Declaration)Visit(context.structDecl())!;
        }

        if (context.@enum() != null)
        {
            return (Declaration)Visit(context.@enum())!;
        }

        if (context.service() != null)
        {
            return (Declaration)Visit(context.service())!;
        }

        throw new InvalidOperationException("Unknown declaration type");
    }

    public override ForwardDeclaration VisitForward(BondParser.ForwardContext context)
    {
        var name = (string)Visit(context.identifier())!;
        var typeParams = context.typeParameters() != null
            ? (TypeParam[])Visit(context.typeParameters())!
            : [];

        return new ForwardDeclaration
        {
            Namespaces = _currentNamespaces.ToArray(),
            Name = name,
            TypeParameters = typeParams,
            Location = new SourceLocation(context.Start.Line, context.Start.Column + 1),
            LeadingTrivia = LeadingTriviaFor(context.Start),
            TrailingTrivia = TrailingTriviaFor(context.Stop)
        };
    }

    public override AliasDeclaration VisitAlias(BondParser.AliasContext context)
    {
        var name = (string)Visit(context.identifier())!;
        var typeParams = context.typeParameters() != null
            ? (TypeParam[])Visit(context.typeParameters())!
            : [];

        _currentTypeParams.AddRange(typeParams);

        var aliasedType = (BondType)Visit(context.type())!;

        _currentTypeParams.RemoveRange(_currentTypeParams.Count - typeParams.Length, typeParams.Length);

        return new AliasDeclaration
        {
            Namespaces = _currentNamespaces.ToArray(),
            Name = name,
            TypeParameters = typeParams,
            AliasedType = aliasedType,
            Location = new SourceLocation(context.Start.Line, context.Start.Column + 1),
            LeadingTrivia = LeadingTriviaFor(context.Start),
            TrailingTrivia = TrailingTriviaFor(context.Stop)
        };
    }

    public override Declaration VisitStructDecl(BondParser.StructDeclContext context)
    {
        var attributes = context.attributes() != null
            ? (Syntax.Attribute[])Visit(context.attributes())!
            : [];

        var name = (string)Visit(context.identifier())!;
        var typeParams = context.typeParameters() != null
            ? (TypeParam[])Visit(context.typeParameters())!
            : [];

        var loc = new SourceLocation(context.Start.Line, context.Start.Column + 1);
        var leading = LeadingTriviaFor(context.Start);
        var trailing = TrailingTriviaFor(context.Stop);

        _currentTypeParams.AddRange(typeParams);

        Declaration result;
        if (context.structView() != null)
        {
            result = VisitStructView(context.structView(), name, typeParams, attributes, loc, leading, trailing);
        }
        else if (context.structDef() != null)
        {
            result = VisitStructDef(context.structDef(), name, typeParams, attributes, loc, leading, trailing);
        }
        else
        {
            throw new InvalidOperationException("Struct must have either view or definition");
        }

        _currentTypeParams.RemoveRange(_currentTypeParams.Count - typeParams.Length, typeParams.Length);

        return result;
    }

    private StructDeclaration VisitStructView(BondParser.StructViewContext context, string name, TypeParam[] typeParams, Syntax.Attribute[] attributes, SourceLocation loc, Trivia[] leading, Trivia? trailing)
    {
        return new StructDeclaration
        {
            Namespaces = _currentNamespaces.ToArray(),
            Attributes = attributes,
            Name = name,
            TypeParameters = typeParams,
            BaseType = null,
            IsView = true,
            ViewTarget = (string[])Visit(context.qualifiedName())!,
            ViewFields = context.viewFieldList().identifier().Select(id => (string)Visit(id)!).ToArray(),
            Fields = [],
            Location = loc,
            LeadingTrivia = leading,
            TrailingTrivia = trailing
        };
    }

    private StructDeclaration VisitStructDef(BondParser.StructDefContext context, string name, TypeParam[] typeParams, Syntax.Attribute[] attributes, SourceLocation loc, Trivia[] leading, Trivia? trailing)
    {
        var baseType = context.userType() != null
            ? (BondType)Visit(context.userType())!
            : null;

        var fields = context.field()
            .Select(f => (Field)Visit(f)!)
            .OrderBy(f => f.Ordinal)
            .ToArray();

        return new StructDeclaration
        {
            Namespaces = _currentNamespaces.ToArray(),
            Attributes = attributes,
            Name = name,
            TypeParameters = typeParams,
            BaseType = baseType,
            Fields = fields,
            Location = loc,
            LeadingTrivia = leading,
            TrailingTrivia = trailing
        };
    }

    public override EnumDeclaration VisitEnum(BondParser.EnumContext context)
    {
        var attributes = context.attributes() != null
            ? (Syntax.Attribute[])Visit(context.attributes())!
            : [];

        var name = (string)Visit(context.identifier())!;
        var constants = context.enumConstant()
            .Select(c => (Constant)Visit(c)!)
            .ToArray();

        return new EnumDeclaration
        {
            Namespaces = _currentNamespaces.ToArray(),
            Attributes = attributes,
            Name = name,
            TypeParameters = [],
            Constants = constants,
            Location = new SourceLocation(context.Start.Line, context.Start.Column + 1),
            LeadingTrivia = LeadingTriviaFor(context.Start),
            TrailingTrivia = TrailingTriviaFor(context.Stop)
        };
    }

    public override Constant VisitEnumConstant(BondParser.EnumConstantContext context)
    {
        var name = (string)Visit(context.identifier())!;
        var location = new SourceLocation(context.Start.Line, context.Start.Column + 1);
        long? value = null;

        if (context.INTEGER_LITERAL() != null)
        {
            var bigIntValue = ParseInteger(context.INTEGER_LITERAL().GetText());
            var isNegative = context.MINUS() != null;
            var finalValue = isNegative ? -bigIntValue : bigIntValue;

            // gbc stores enum values in a machine-sized signed integer.
            value = unchecked((long)(ulong)(finalValue & ulong.MaxValue));
        }

        return new Constant(name, value)
        {
            Location = location,
            LeadingTrivia = LeadingTriviaFor(context.Start),
            TrailingTrivia = TrailingTriviaFor(context.Stop)
        };
    }

    public override ServiceDeclaration VisitService(BondParser.ServiceContext context)
    {
        var attributes = context.attributes() != null
            ? (Syntax.Attribute[])Visit(context.attributes())!
            : [];

        var name = (string)Visit(context.identifier())!;
        var typeParams = context.typeParameters() != null
            ? (TypeParam[])Visit(context.typeParameters())!
            : [];

        _currentTypeParams.AddRange(typeParams);

        var baseType = context.serviceType() != null
            ? (BondType)Visit(context.serviceType())!
            : null;

        var methods = context.method()
            .Select(m => (Method)Visit(m)!)
            .ToArray();

        _currentTypeParams.RemoveRange(_currentTypeParams.Count - typeParams.Length, typeParams.Length);

        return new ServiceDeclaration
        {
            Namespaces = _currentNamespaces.ToArray(),
            Attributes = attributes,
            Name = name,
            TypeParameters = typeParams,
            BaseType = baseType,
            Methods = methods,
            Location = new SourceLocation(context.Start.Line, context.Start.Column + 1),
            LeadingTrivia = LeadingTriviaFor(context.Start),
            TrailingTrivia = TrailingTriviaFor(context.Stop)
        };
    }

    public override Method VisitMethod(BondParser.MethodContext context)
    {
        var attributes = context.attributes() != null
            ? (Syntax.Attribute[])Visit(context.attributes())!
            : [];

        var name = (string)Visit(context.identifier())!;
        var location = new SourceLocation(context.Start.Line, context.Start.Column + 1);

        var inputType = context.methodParameter() != null
            ? (MethodType)Visit(context.methodParameter().methodInputType())!
            : MethodType.Void.Instance;

        if (context.NOTHING() != null)
        {
            return new EventMethod
            {
                Attributes = attributes,
                Name = name,
                InputType = inputType,
                Location = location
            };
        }

        return new FunctionMethod
        {
            Attributes = attributes,
            Name = name,
            ResultType = (MethodType)Visit(context.methodResultType())!,
            InputType = inputType,
            Location = location
        };
    }

    public override MethodType VisitMethodResultType(BondParser.MethodResultTypeContext context)
    {
        if (context.VOID() != null)
        {
            return MethodType.Void.Instance;
        }

        if (context.methodTypeStreaming() != null)
        {
            return (MethodType)Visit(context.methodTypeStreaming())!;
        }

        if (context.methodTypeUnary() != null)
        {
            return (MethodType)Visit(context.methodTypeUnary())!;
        }

        throw new InvalidOperationException("Unknown method result type");
    }

    public override MethodType VisitMethodInputType(BondParser.MethodInputTypeContext context)
    {
        if (context.VOID() != null)
        {
            return MethodType.Void.Instance;
        }

        if (context.methodTypeStreaming() != null)
        {
            return (MethodType)Visit(context.methodTypeStreaming())!;
        }

        if (context.methodTypeUnary() != null)
        {
            return (MethodType)Visit(context.methodTypeUnary())!;
        }

        throw new InvalidOperationException("Unknown method input type");
    }

    public override MethodType VisitMethodTypeStreaming(BondParser.MethodTypeStreamingContext context)
    {
        var type = (BondType)Visit(context.userStructRef())!;
        return new MethodType.Streaming(type);
    }

    public override MethodType VisitMethodTypeUnary(BondParser.MethodTypeUnaryContext context)
    {
        var type = (BondType)Visit(context.userStructRef())!;
        return new MethodType.Unary(type);
    }

    public override Field VisitField(BondParser.FieldContext context)
    {
        var attributes = context.attributes() != null
            ? (Syntax.Attribute[])Visit(context.attributes())!
            : [];

        var ordinalValue = ParseInteger(context.fieldOrdinal().INTEGER_LITERAL().GetText());
        if (ordinalValue < ushort.MinValue || ordinalValue > ushort.MaxValue)
            throw new SemanticErrorException("Field ordinal must be within the range 0-65535",
                new SourceLocation(context.Start.Line, context.Start.Column + 1));
        var ordinal = (ushort)ordinalValue;

        var modifier = context.modifier() != null
            ? (FieldModifier)Visit(context.modifier())!
            : FieldModifier.Optional;

        var type = (BondType)Visit(context.fieldType())!;
        var name = context.fieldIdentifier().GetText();

        var defaultValue = context.default_() != null
            ? (Default?)Visit(context.default_())
            : null;

        // Non-struct types with `nothing` default become Maybe<T>; structs are
        // rejected later in semantic analysis.
        if (defaultValue is Default.Nothing && type is not BondType.Maybe)
        {
            type = new BondType.Maybe(type);
        }

        return new Field(attributes, ordinal, modifier, type, name, defaultValue)
        {
            Location = new SourceLocation(context.Start.Line, context.Start.Column + 1),
            LeadingTrivia = LeadingTriviaFor(context.Start),
            TrailingTrivia = TrailingTriviaFor(context.Stop)
        };
    }

    public override object VisitModifier(BondParser.ModifierContext context)
    {
        if (context.REQUIRED_OPTIONAL() != null)
        {
            return FieldModifier.RequiredOptional;
        }

        if (context.REQUIRED() != null)
        {
            return FieldModifier.Required;
        }

        return FieldModifier.Optional;
    }

    public override BondType VisitFieldType(BondParser.FieldTypeContext context)
    {
        if (context.BOND_META_NAME() != null)
        {
            return BondType.MetaName.Instance;
        }

        if (context.BOND_META_FULL_NAME() != null)
        {
            return BondType.MetaFullName.Instance;
        }

        return (BondType)Visit(context.type())!;
    }

    public override BondType VisitType(BondParser.TypeContext context)
    {
        if (context.basicType() != null)
        {
            return (BondType)Visit(context.basicType())!;
        }

        if (context.complexType() != null)
        {
            return (BondType)Visit(context.complexType())!;
        }

        if (context.userType() != null)
        {
            return (BondType)Visit(context.userType())!;
        }

        throw new InvalidOperationException("Unknown type");
    }

    public override BondType VisitBasicType(BondParser.BasicTypeContext context)
    {
        return context.GetText() switch
        {
            "int8" => BondType.Int8.Instance,
            "int16" => BondType.Int16.Instance,
            "int32" => BondType.Int32.Instance,
            "int64" => BondType.Int64.Instance,
            "uint8" => BondType.UInt8.Instance,
            "uint16" => BondType.UInt16.Instance,
            "uint32" => BondType.UInt32.Instance,
            "uint64" => BondType.UInt64.Instance,
            "float" => BondType.Float.Instance,
            "double" => BondType.Double.Instance,
            "string" => BondType.String.Instance,
            "wstring" => BondType.WString.Instance,
            "bool" => BondType.Bool.Instance,
            _ => throw new InvalidOperationException($"Unknown basic type: {context.GetText()}")
        };
    }

    public override BondType VisitComplexType(BondParser.ComplexTypeContext context)
    {
        if (context.LIST() != null)
        {
            var elementType = (BondType)Visit(context.type())!;
            return new BondType.List(elementType);
        }
        if (context.BLOB() != null)
        {
            return BondType.Blob.Instance;
        }
        if (context.VECTOR() != null)
        {
            var elementType = (BondType)Visit(context.type())!;
            return new BondType.Vector(elementType);
        }
        if (context.NULLABLE() != null)
        {
            var elementType = (BondType)Visit(context.type())!;
            return new BondType.Nullable(elementType);
        }
        if (context.SET() != null)
        {
            var keyType = (BondType)Visit(context.keyType())!;
            return new BondType.Set(keyType);
        }
        if (context.MAP() != null)
        {
            var keyType = (BondType)Visit(context.keyType())!;
            var valueType = (BondType)Visit(context.type())!;
            return new BondType.Map(keyType, valueType);
        }
        if (context.BONDED() != null)
        {
            var structType = (BondType)Visit(context.userStructRef())!;
            return new BondType.Bonded(structType);
        }

        throw new InvalidOperationException("Unknown complex type");
    }

    public override BondType VisitKeyType(BondParser.KeyTypeContext context)
    {
        if (context.basicType() != null)
        {
            return (BondType)Visit(context.basicType())!;
        }

        if (context.userType() != null)
        {
            return (BondType)Visit(context.userType())!;
        }

        throw new InvalidOperationException("Unknown key type");
    }

    public override BondType VisitUserType(BondParser.UserTypeContext context)
    {
        var name = (string[])Visit(context.qualifiedName())!;

        if (name.Length == 1)
        {
            var typeParam = _currentTypeParams.FirstOrDefault(p => p.Name == name[0]);
            if (typeParam != null)
            {
                RejectParameterArguments(context.typeArgs(), context.Start);
                return new BondType.TypeParameter(typeParam);
            }
        }

        var typeArgs = context.typeArgs() != null
            ? (BondType[])Visit(context.typeArgs())!
            : [];

        return new BondType.UnresolvedType(name, typeArgs);
    }

    public override BondType VisitUserStructRef(BondParser.UserStructRefContext context)
    {
        var name = (string[])Visit(context.qualifiedName())!;

        if (name.Length == 1)
        {
            var typeParam = _currentTypeParams.FirstOrDefault(p => p.Name == name[0]);
            if (typeParam != null)
            {
                RejectParameterArguments(context.typeArgs(), context.Start);
                return new BondType.TypeParameter(typeParam);
            }
        }

        var typeArgs = context.typeArgs() != null
            ? (BondType[])Visit(context.typeArgs())!
            : [];

        return new BondType.UnresolvedType(name, typeArgs);
    }

    public override BondType VisitServiceType(BondParser.ServiceTypeContext context)
    {
        var name = (string[])Visit(context.qualifiedName())!;

        if (name.Length == 1)
        {
            var typeParam = _currentTypeParams.FirstOrDefault(p => p.Name == name[0]);
            if (typeParam != null)
            {
                RejectParameterArguments(context.typeArgs(), context.Start);
                return new BondType.TypeParameter(typeParam);
            }
        }

        var typeArgs = context.typeArgs() != null
            ? (BondType[])Visit(context.typeArgs())!
            : [];

        return new BondType.UnresolvedType(name, typeArgs);
    }

    public override BondType[] VisitTypeArgs(BondParser.TypeArgsContext context)
    {
        return context.typeArg()
            .Select(a => (BondType)Visit(a)!)
            .ToArray();
    }

    private static void RejectParameterArguments(BondParser.TypeArgsContext? arguments, IToken token)
    {
        if (arguments is not null)
            throw new SemanticErrorException("A type parameter cannot have type arguments",
                new SourceLocation(token.Line, token.Column + 1));
    }

    public override BondType VisitTypeArg(BondParser.TypeArgContext context)
    {
        if (context.type() != null)
        {
            return (BondType)Visit(context.type())!;
        }

        if (context.INTEGER_LITERAL() != null)
        {
            var value = ParseInteger(context.INTEGER_LITERAL().GetText());
            if (context.MINUS() != null)
                value = -value;

            return new BondType.IntTypeArg(unchecked((long)(ulong)(value & ulong.MaxValue)));
        }

        throw new InvalidOperationException("Unknown type argument");
    }

    public override TypeParam[] VisitTypeParameters(BondParser.TypeParametersContext context)
    {
        return context.typeParam()
            .Select(p => (TypeParam)Visit(p)!)
            .ToArray();
    }

    public override TypeParam VisitTypeParam(BondParser.TypeParamContext context)
    {
        var name = (string)Visit(context.identifier())!;
        var constraint = context.constraint() != null
            ? TypeConstraint.Value
            : TypeConstraint.None;

        return new TypeParam(name, constraint);
    }

    public override Default VisitDefault_(BondParser.Default_Context context)
    {
        if (context.TRUE() != null)
        {
            return new Default.Bool(true);
        }
        if (context.FALSE() != null)
        {
            return new Default.Bool(false);
        }
        if (context.NOTHING() != null)
        {
            return Default.Nothing.Instance;
        }
        if (context.STRING_LITERAL() != null)
        {
            var value = Unquote(context.STRING_LITERAL().GetText());
            return new Default.String(value);
        }

        var isNegative = context.MINUS() != null;

        if (context.FLOAT_LITERAL() != null)
        {
            var value = double.Parse(context.FLOAT_LITERAL().GetText(), CultureInfo.InvariantCulture);
            return new Default.Float(isNegative ? -value : value);
        }
        if (context.INTEGER_LITERAL() != null)
        {
            var value = ParseInteger(context.INTEGER_LITERAL().GetText());
            return new Default.Integer(isNegative ? -value : value);
        }
        if (context.identifier() != null)
        {
            var identifier = (string)Visit(context.identifier())!;
            return new Default.Enum(identifier);
        }

        throw new InvalidOperationException("Unknown default value");
    }

    public override Syntax.Attribute[] VisitAttributes(BondParser.AttributesContext context)
    {
        return context.attribute()
            .Select(a => (Syntax.Attribute)Visit(a)!)
            .ToArray();
    }

    public override Syntax.Attribute VisitAttribute(BondParser.AttributeContext context)
    {
        var name = (string[])Visit(context.qualifiedName())!;
        var value = Unquote(context.STRING_LITERAL().GetText());
        return new Syntax.Attribute(name, value);
    }

    private static string Unquote(string str)
    {
        if (str.Length >= 2 && str[0] == '"' && str[^1] == '"')
        {
            var value = new StringBuilder();
            for (var i = 1; i < str.Length - 1; i++)
            {
                if (str[i] != '\\')
                {
                    value.Append(str[i]);
                    continue;
                }
                var escape = str[++i];
                switch (escape)
                {
                    case '"': value.Append('"'); break;
                    case '\'': value.Append('\''); break;
                    case '\\': value.Append('\\'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'a': value.Append('\a'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'v': value.Append('\v'); break;
                    case 'x':
                    case 'o':
                        AppendCodePoint(value, ReadEscapedNumber(str, ref i, escape == 'x' ? 16 : 8, i + 1));
                        break;
                    case >= '0' and <= '9':
                        AppendCodePoint(value, ReadEscapedNumber(str, ref i, 10, i));
                        break;
                    case '^':
                        if (i + 1 >= str.Length - 1 || str[i + 1] is < '@' or > '_')
                            throw new FormatException("Invalid control escape in string literal.");
                        value.Append((char)(str[++i] - '@'));
                        break;
                    case 'u':
                    case 'U':
                    {
                        var digits = escape == 'U' ? 8 : 4;
                        if (i + digits >= str.Length - 1
                            || !uint.TryParse(str.AsSpan(i + 1, digits), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out var codePoint))
                            throw new FormatException($"Invalid '\\{escape}' escape in string literal.");
                        if (escape == 'U')
                        {
                            if (codePoint > 0x10ffff || codePoint is >= 0xd800 and <= 0xdfff)
                                throw new FormatException("Invalid Unicode code point in string literal.");
                            value.Append(char.ConvertFromUtf32((int)codePoint));
                        }
                        else
                        {
                            value.Append((char)codePoint);
                        }
                        i += digits;
                        break;
                    }
                    default:
                    {
                        var named = NamedEscapes.FirstOrDefault(pair =>
                            str.AsSpan(i, str.Length - 1 - i).StartsWith(pair.Key, StringComparison.Ordinal));
                        if (named.Key is null)
                            throw new FormatException($"Unsupported '\\{escape}' escape in string literal.");
                        value.Append(named.Value);
                        i += named.Key.Length - 1;
                        break;
                    }
                }
            }
            return value.ToString();
        }
        return str;
    }

    private static readonly KeyValuePair<string, char>[] NamedEscapes =
    [
        new("NUL", '\0'), new("SOH", '\x01'), new("STX", '\x02'), new("ETX", '\x03'),
        new("EOT", '\x04'), new("ENQ", '\x05'), new("ACK", '\x06'), new("BEL", '\x07'),
        new("DLE", '\x10'), new("DC1", '\x11'), new("DC2", '\x12'), new("DC3", '\x13'),
        new("DC4", '\x14'), new("NAK", '\x15'), new("SYN", '\x16'), new("ETB", '\x17'),
        new("CAN", '\x18'), new("SUB", '\x1a'), new("ESC", '\x1b'), new("DEL", '\x7f'),
        new("BS", '\b'), new("HT", '\t'), new("LF", '\n'), new("VT", '\v'), new("FF", '\f'),
        new("CR", '\r'), new("SO", '\x0e'), new("SI", '\x0f'), new("EM", '\x19'),
        new("FS", '\x1c'), new("GS", '\x1d'), new("RS", '\x1e'), new("US", '\x1f'), new("SP", ' ')
    ];

    private static uint ReadEscapedNumber(string literal, ref int index, int numberBase, int start)
    {
        uint value = 0;
        var cursor = start;
        for (; cursor < literal.Length - 1; cursor++)
        {
            var digit = literal[cursor] switch
            {
                >= '0' and <= '9' => literal[cursor] - '0',
                >= 'a' and <= 'f' => literal[cursor] - 'a' + 10,
                >= 'A' and <= 'F' => literal[cursor] - 'A' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= numberBase)
                break;
            value = value * (uint)numberBase + (uint)digit;
            if (value > 0x10ffff)
                throw new FormatException("Invalid Unicode code point in string literal.");
        }
        if (cursor == start)
            throw new FormatException("An escaped character code in a string literal requires at least one digit.");
        index = cursor - 1;
        return value;
    }

    private static void AppendCodePoint(StringBuilder value, uint codePoint)
    {
        if (codePoint <= ushort.MaxValue)
            value.Append((char)codePoint);
        else
            value.Append(char.ConvertFromUtf32((int)codePoint));
    }

    private static BigInteger ParseInteger(string str)
    {
        bool isNegative = str.StartsWith('-');
        if (isNegative)
        {
            str = str[1..];
        }

        BigInteger value;
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // Parse hex as unsigned by prepending "0" to ensure positive interpretation
            var hexDigits = str[2..];
            // BigInteger.Parse with HexNumber treats high bit as sign bit
            // Prepend "0" to force positive interpretation
            value = BigInteger.Parse("0" + hexDigits, System.Globalization.NumberStyles.HexNumber);
        }
        else if (str.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            // Parse octal manually (BigInteger doesn't have native octal support)
            value = 0;
            foreach (char c in str[2..])
            {
                value = value * 8 + (c - '0');
            }
        }
        else
        {
            value = BigInteger.Parse(str);
        }

        return isNegative ? -value : value;
    }
}
