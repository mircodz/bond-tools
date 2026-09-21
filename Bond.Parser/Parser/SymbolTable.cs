using System.Collections.Generic;
using System.Linq;
using Bond.Parser.Syntax;

namespace Bond.Parser.Parser;

/// <summary>
/// Globally-visible declarations across this file and its transitive imports,
/// plus per-file alias environments and the set of import paths already processed.
/// </summary>
public class SymbolTable
{
    private readonly List<Declaration> _globalDeclarations = [];
    private readonly List<AliasDeclaration> _aliases = [];
    private readonly List<ForwardDeclaration> _forwards = [];
    private readonly List<Declaration> _boundDeclarations = [];
    private readonly HashSet<string> _processedImports = [];
    private readonly Dictionary<Declaration, (IReadOnlyList<AliasDeclaration> Aliases, string? File)> _contexts =
        new(ReferenceEqualityComparer.Instance);

    internal IEnumerable<Declaration> Declarations => _globalDeclarations.Concat(_aliases).Concat(_forwards);
    internal IEnumerable<Declaration> BoundDeclarations => _boundDeclarations;

    internal void SetContext(Declaration declaration, IReadOnlyList<AliasDeclaration> aliases, string? filePath)
    {
        _contexts[declaration] = (aliases, filePath);
        if (declaration is AliasDeclaration alias)
        {
            _aliases.Add(alias);
        }

        if (declaration is ForwardDeclaration forward)
        {
            _forwards.Add(forward);
        }
    }

    internal IReadOnlyList<AliasDeclaration>? GetAliases(Declaration declaration) =>
        _contexts.TryGetValue(declaration, out var context) ? context.Aliases : null;

    internal string? GetSourceFile(Declaration declaration) =>
        _contexts.TryGetValue(declaration, out var context) ? context.File : null;

    internal void SetResolvedContext(Declaration original, Declaration resolved)
    {
        _boundDeclarations.Add(resolved);
        if (_contexts.TryGetValue(original, out var context))
        {
            _contexts[resolved] = context;
        }
    }

    /// <summary>
    /// Throws on name collision unless the existing entry is a forward declaration
    /// that the new one closes (or vice versa).
    /// </summary>
    public void AddDeclaration(Declaration declaration)
    {
        var duplicates = _globalDeclarations
            .Where(d => d.Name == declaration.Name && d.Namespaces.Any(ns1 => declaration.Namespaces.Any(ns1.Matches)))
            .ToList();

        foreach (var duplicate in duplicates)
        {
            if (!TryReconcile(duplicate, declaration))
            {
                throw new SemanticErrorException(
                    $"Duplicate declaration: {declaration.Kind} '{declaration.Name}' was already declared as {duplicate.Kind}",
                    declaration.Location);
            }
        }

        if (duplicates.Count > 0)
        {
            if (declaration is not StructDeclaration || duplicates.Any(d => d is StructDeclaration))
            {
                return;
            }

            _globalDeclarations.RemoveAll(duplicates.Contains);
        }

        _globalDeclarations.Add(declaration);
    }

    /// <summary>Aliases first, then the global table.</summary>
    public Declaration? FindSymbol(string[] qualifiedName, Namespace[] currentNamespaces, IReadOnlyList<AliasDeclaration> aliases)
    {
        var alias = FindAlias(qualifiedName, currentNamespaces, aliases);
        if (alias != null)
        {
            return alias;
        }

        if (qualifiedName.Length == 1)
        {
            return _globalDeclarations.FirstOrDefault(d =>
                d.Name == qualifiedName[0] &&
                d.Namespaces.Any(ns1 => currentNamespaces.Any(ns1.Matches)))
                ?? FindAlias(qualifiedName, currentNamespaces, _aliases);
        }

        var namespacePart = qualifiedName[..^1];
        var namePart = qualifiedName[^1];
        return _globalDeclarations.FirstOrDefault(d =>
            d.Name == namePart &&
            d.Namespaces.Any(ns => ns.Name.SequenceEqual(namespacePart)))
            ?? FindAlias(qualifiedName, currentNamespaces, _aliases);
    }

    /// <summary>Returns true on first claim, false on cycle / diamond import.</summary>
    public bool ClaimImport(string canonicalPath) => _processedImports.Add(canonicalPath);

    private static AliasDeclaration? FindAlias(string[] qualifiedName, Namespace[] currentNamespaces, IReadOnlyList<AliasDeclaration> aliases)
    {
        if (qualifiedName.Length == 1)
        {
            return aliases.FirstOrDefault(a =>
                a.Name == qualifiedName[0] &&
                a.Namespaces.Any(ns1 => currentNamespaces.Any(ns1.Matches)));
        }

        var namespacePart = qualifiedName[..^1];
        var namePart = qualifiedName[^1];
        return aliases.FirstOrDefault(a =>
            a.Name == namePart &&
            a.Namespaces.Any(ns => ns.Name.SequenceEqual(namespacePart)));
    }

    // Forward + struct (in either order) reconciles when the type-parameter shape
    // matches. Identical re-declarations also pass — happens when the same import
    // is seen via multiple paths.
    private static bool TryReconcile(Declaration existing, Declaration newDeclaration)
    {
        if (existing is ForwardDeclaration forward && newDeclaration is StructDeclaration structure)
        {
            return structure.IsView || ParametersMatch(forward.TypeParameters, newDeclaration.TypeParameters);
        }

        if (existing is StructDeclaration structure2 && newDeclaration is ForwardDeclaration forward2)
        {
            return structure2.IsView || ParametersMatch(existing.TypeParameters, forward2.TypeParameters);
        }

        if (existing is ForwardDeclaration && newDeclaration is ForwardDeclaration)
        {
            return ParametersMatch(existing.TypeParameters, newDeclaration.TypeParameters);
        }

        return EquivalentDeclarations(existing, newDeclaration);
    }

    internal static bool ParametersMatch(TypeParam[] a, TypeParam[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Constraint != b[i].Constraint)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool EquivalentDeclarations(Declaration left, Declaration right)
    {
        if (left.Name != right.Name
            || !left.TypeParameters.SequenceEqual(right.TypeParameters)
            || left.Namespaces.Length != right.Namespaces.Length
            || !left.Namespaces.Zip(right.Namespaces).All(pair =>
                pair.First.LanguageQualifier == pair.Second.LanguageQualifier
                && pair.First.Name.SequenceEqual(pair.Second.Name)))
        {
            return false;
        }

        return (left, right) switch
        {
            (StructDeclaration a, StructDeclaration b) =>
                a.IsView == b.IsView
                && (a.ViewTarget ?? []).SequenceEqual(b.ViewTarget ?? [])
                && a.ViewFields.SequenceEqual(b.ViewFields)
                && a.BaseType == b.BaseType
                && AttributesMatch(a.Attributes, b.Attributes)
                && a.Fields.Length == b.Fields.Length
                && a.Fields.Zip(b.Fields).All(pair =>
                    pair.First.Name == pair.Second.Name
                    && pair.First.Ordinal == pair.Second.Ordinal
                    && pair.First.Modifier == pair.Second.Modifier
                    && pair.First.Type == pair.Second.Type
                    && pair.First.DefaultValue == pair.Second.DefaultValue
                    && AttributesMatch(pair.First.Attributes, pair.Second.Attributes)),
            (EnumDeclaration a, EnumDeclaration b) =>
                AttributesMatch(a.Attributes, b.Attributes)
                && a.Constants.Select(c => (c.Name, c.Value)).SequenceEqual(b.Constants.Select(c => (c.Name, c.Value))),
            (AliasDeclaration a, AliasDeclaration b) => a.AliasedType == b.AliasedType,
            (ServiceDeclaration a, ServiceDeclaration b) =>
                a.BaseType == b.BaseType
                && AttributesMatch(a.Attributes, b.Attributes)
                && a.Methods.Length == b.Methods.Length
                && a.Methods.Zip(b.Methods).All(pair => MethodsMatch(pair.First, pair.Second)),
            (ForwardDeclaration, ForwardDeclaration) => true,
            _ => false
        };
    }

    private static bool AttributesMatch(Syntax.Attribute[] left, Syntax.Attribute[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Value == pair.Second.Value && pair.First.QualifiedName.SequenceEqual(pair.Second.QualifiedName));

    private static bool MethodsMatch(Method left, Method right)
    {
        if (left.Name != right.Name || !AttributesMatch(left.Attributes, right.Attributes))
        {
            return false;
        }

        return (left, right) switch
        {
            (FunctionMethod a, FunctionMethod b) => a.InputType == b.InputType && a.ResultType == b.ResultType,
            (EventMethod a, EventMethod b) => a.InputType == b.InputType,
            _ => false
        };
    }
}
