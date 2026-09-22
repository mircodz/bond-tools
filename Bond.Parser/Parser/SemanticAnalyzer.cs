using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Antlr4.Runtime;
using Bond.Parser.Syntax;
using Bond.Parser.Grammar;

namespace Bond.Parser.Parser;

/// <summary>
/// Drives the semantic phase of parsing for a single Bond file:
/// Registers the import graph, binds each declaration in its original environment,
/// and validates the bound graph without adding imports to the root declarations.
/// </summary>
public class SemanticAnalyzer
{
    private readonly SymbolTable _symbolTable;
    private readonly ImportResolver _importResolver;
    private readonly string _currentFile;
    private readonly List<AliasDeclaration> _localAliases = [];

    public SemanticAnalyzer(SymbolTable symbolTable, ImportResolver importResolver, string currentFile)
    {
        _symbolTable = symbolTable;
        _importResolver = importResolver;
        _currentFile = currentFile;
    }

    public async Task<Syntax.Bond> AnalyzeAsync(Syntax.Bond bond)
    {
        _symbolTable.ClaimImport(_currentFile);
        await RegisterFileAsync(bond);
        _symbolTable.CompleteAliasScopes();

        var resolved = TypeResolver.Resolve(bond, _symbolTable, _localAliases);

        foreach (var declaration in _symbolTable.BoundDeclarations)
        {
            try
            {
                ValidateDeclaration(declaration);
                ValidateInheritance(declaration, resolved.ResolvedDeclarations);

                if (declaration is ForwardDeclaration forward
                    && resolved.ResolvedDeclarations.FirstOrDefault(d => d.QualifiedName == forward.QualifiedName) is StructDeclaration definition
                    && !SymbolTable.ParametersMatch(forward.TypeParameters, definition.TypeParameters))
                {
                    throw new SemanticErrorException($"Type parameters for forward declaration '{forward.Name}' do not match its definition", forward.Location);
                }
            }
            catch (SemanticErrorException error)
            {
                throw new ParseErrorsException([
                    new ParseError(error.Message, _symbolTable.GetSourceFile(declaration) ?? _currentFile,
                        error.Location.Line, error.Location.Column)
                ]);
            }
        }

        return resolved;
    }

    private async Task RegisterFileAsync(Syntax.Bond bond)
    {
        _symbolTable.SetFileAliases(_currentFile, _localAliases);
        foreach (var import in bond.Imports)
        {
            await ProcessImportAsync(import);
        }

        foreach (var alias in bond.Declarations.OfType<AliasDeclaration>())
        {
            RegisterAlias(alias);
        }

        foreach (var declaration in bond.Declarations)
        {
            _symbolTable.SetContext(declaration, _localAliases, _currentFile);
            if (declaration is not AliasDeclaration)
            {
                _symbolTable.AddDeclaration(declaration);
            }
        }
    }

    private void RegisterAlias(AliasDeclaration alias)
    {
        var duplicate = _localAliases.FirstOrDefault(existing =>
            existing.Name == alias.Name &&
            existing.Namespaces.Any(ns => alias.Namespaces.Any(ns.Matches)));

        if (duplicate is not null)
        {
            if (SymbolTable.EquivalentDeclarations(duplicate, alias))
            {
                return;
            }

            throw new SemanticErrorException($"Duplicate declaration: alias '{alias.Name}' was already declared", alias.Location);
        }

        _localAliases.Add(alias);
    }

    private async Task ProcessImportAsync(Import import)
    {
        var (canonicalPath, content) = await _importResolver(_currentFile, import.FilePath);
        _symbolTable.AddImport(_currentFile, canonicalPath);

        if (!_symbolTable.ClaimImport(canonicalPath))
        {
            return;
        }

        try
        {
            var importAst = ParseContent(content, canonicalPath);
            var analyzer = new SemanticAnalyzer(_symbolTable, _importResolver, canonicalPath);
            await analyzer.RegisterFileAsync(importAst);
        }
        catch (SemanticErrorException error)
        {
            throw new ParseErrorsException([
                new ParseError(error.Message, canonicalPath, error.Location.Line, error.Location.Column)
            ]);
        }
    }

    private static Syntax.Bond ParseContent(string content, string filePath)
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
            throw new ParseErrorsException(errorListener.Errors);
        }

        var astBuilder = new AstBuilder(tokenStream);
        return (Syntax.Bond)astBuilder.Visit(parseTree)!;
    }

    private static void ValidateDeclaration(Declaration declaration)
    {
        switch (declaration)
        {
            case StructDeclaration structDecl:
                ValidateStruct(structDecl);
                break;
            case AliasDeclaration alias:
                TypeValidator.ValidateType(alias.AliasedType, alias.Location);
                break;
            case EnumDeclaration enumDecl:
                ValidateEnum(enumDecl);
                break;
            case ServiceDeclaration serviceDecl:
                ValidateService(serviceDecl);
                break;
        }
    }

    private static void ValidateStruct(StructDeclaration structDecl)
    {
        CheckForDuplicates(structDecl.Fields.Select(f => f.Ordinal), $"Struct '{structDecl.Name}'", "field ordinal", structDecl.Location);
        CheckForDuplicates(structDecl.Fields.Select(f => f.Name), $"Struct '{structDecl.Name}'", "field name", structDecl.Location);

        if (structDecl.BaseType is not null)
        {
            var baseType = structDecl.BaseType.ResolveAliases();
            if (!baseType.IsStruct() && baseType is not BondType.TypeParameter)
            {
                throw new SemanticErrorException($"Struct '{structDecl.Name}' must inherit from a struct", structDecl.Location);
            }

            TypeValidator.ValidateType(structDecl.BaseType, structDecl.Location);
        }

        foreach (var field in structDecl.Fields)
        {
            ValidateField(field);
        }
    }

    private static void ValidateEnum(EnumDeclaration enumDecl)
    {
        CheckForDuplicates(enumDecl.Constants.Select(c => c.Name), $"Enum '{enumDecl.Name}'", "constant name", enumDecl.Location);
    }

    private static void ValidateService(ServiceDeclaration serviceDecl)
    {
        CheckForDuplicates(serviceDecl.Methods.Select(m => m.Name), $"Service '{serviceDecl.Name}'", "method name", serviceDecl.Location);

        if (serviceDecl.BaseType is BondType.TypeParameter)
        {
            throw new SemanticErrorException($"Service '{serviceDecl.Name}' cannot inherit from type parameter", serviceDecl.Location);
        }

        if (serviceDecl.BaseType is not null && serviceDecl.BaseType.ResolveAliases() is not BondType.TypeReference { Declaration: ServiceDeclaration })
        {
            throw new SemanticErrorException($"Service '{serviceDecl.Name}' cannot inherit from struct", serviceDecl.Location);
        }

        foreach (var method in serviceDecl.Methods)
        {
            switch (method)
            {
                case FunctionMethod function:
                    ValidateMethodType(function.InputType, function.Location);
                    ValidateMethodType(function.ResultType, function.Location);
                    break;
                case EventMethod eventMethod:
                    ValidateMethodType(eventMethod.InputType, eventMethod.Location);
                    break;
            }

            if (method is EventMethod { InputType: MethodType.Streaming })
            {
                throw new SemanticErrorException($"Event method '{method.Name}' cannot have streaming input", method.Location);
            }
        }
    }

    private static void ValidateMethodType(MethodType type, SourceLocation location)
    {
        var parameterType = type switch
        {
            MethodType.Unary unary => unary.Type,
            MethodType.Streaming streaming => streaming.Type,
            _ => null
        };
        if (parameterType is null)
        {
            return;
        }

        TypeValidator.ValidateType(parameterType, location);

        var resolvedType = parameterType.ResolveAliases();
        if (!resolvedType.IsStruct() && resolvedType is not BondType.TypeParameter)
        {
            throw new SemanticErrorException("A service method requires a struct type", location);
        }
    }

    private static void ValidateInheritance(Declaration declaration, Declaration[] environment)
    {
        var visited = new HashSet<string>();
        var current = declaration;
        while (current is StructDeclaration or ServiceDeclaration)
        {
            if (!visited.Add(current.QualifiedName))
            {
                throw new SemanticErrorException($"Cyclic inheritance involving '{current.Name}'", declaration.Location);
            }

            var baseType = current switch
            {
                StructDeclaration structure => structure.BaseType,
                ServiceDeclaration service => service.BaseType,
                _ => null
            };
            if (baseType?.ResolveAliases() is not BondType.TypeReference reference)
            {
                return;
            }

            current = environment.FirstOrDefault(d => d.QualifiedName == reference.Declaration.QualifiedName)
                ?? reference.Declaration;
        }
    }

    private static void CheckForDuplicates<T>(IEnumerable<T> items, string context, string itemType, SourceLocation location)
    {
        var duplicates = items
            .GroupBy(item => item)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new SemanticErrorException($"{context} has duplicate {itemType}(s): {string.Join(", ", duplicates)}", location);
        }
    }

    private static void ValidateField(Field field)
    {
        var unwrapped = field.Type.ResolveAliases();
        TypeValidator.ValidateType(field.Type, field.Location);

        if (!TypeValidator.ValidateDefaultValue(unwrapped, field.DefaultValue))
        {
            throw new SemanticErrorException($"Field '{field.Name}' has invalid default value for type {field.Type}", field.Location);
        }

        if (unwrapped.IsEnum() && field.DefaultValue == null && field.Modifier != FieldModifier.Required)
        {
            throw new SemanticErrorException($"Enum field '{field.Name}' must have a default value", field.Location);
        }

        // Structs cannot have default 'nothing' even when wrapped in Maybe.
        if (field.DefaultValue is Default.Nothing && UnwrapMaybe(unwrapped).ResolveAliases().IsStruct())
        {
            throw new SemanticErrorException($"Struct field '{field.Name}' cannot have default value of 'nothing'", field.Location);
        }
    }

    private static BondType UnwrapMaybe(BondType type) =>
        type is BondType.Maybe maybe ? maybe.ElementType : type;
}
