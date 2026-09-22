using System;
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
    private readonly List<AliasDeclaration> _aliasDeclarations = [];
    private readonly List<ForwardDeclaration> _forwards = [];
    private readonly List<Declaration> _boundDeclarations = [];
    private readonly HashSet<string> _processedImports = [];
    private readonly Dictionary<Declaration, (IReadOnlyList<AliasDeclaration> LocalAliases, string? File)> _contexts =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, FileAliasScope> _fileScopes = [];

    private sealed class FileAliasScope(IReadOnlyList<AliasDeclaration> localAliases)
    {
        // Kept live while recursive import registration fills each file's local aliases.
        public IReadOnlyList<AliasDeclaration> LocalAliases { get; } = localAliases;
        public List<string> Imports { get; } = [];
        public List<AliasDeclaration>? EffectiveAliases { get; set; }
    }

    internal IEnumerable<Declaration> Declarations => _globalDeclarations.Concat(_aliasDeclarations).Concat(_forwards);
    internal IEnumerable<Declaration> BoundDeclarations => _boundDeclarations;

    internal void SetFileAliases(string filePath, IReadOnlyList<AliasDeclaration> localAliases) =>
        _fileScopes[filePath] = new FileAliasScope(localAliases);

    internal void AddImport(string filePath, string importedFilePath) =>
        _fileScopes[filePath].Imports.Add(importedFilePath);

    internal void SetContext(Declaration declaration, IReadOnlyList<AliasDeclaration> localAliases, string? filePath)
    {
        _contexts[declaration] = (localAliases, filePath);
        if (declaration is AliasDeclaration alias)
        {
            _aliasDeclarations.Add(alias);
        }

        if (declaration is ForwardDeclaration forward)
        {
            _forwards.Add(forward);
        }
    }

    internal IReadOnlyList<AliasDeclaration>? GetEffectiveAliases(Declaration declaration)
    {
        if (!_contexts.TryGetValue(declaration, out var context))
        {
            return null;
        }

        if (context.File is null || !_fileScopes.TryGetValue(context.File, out var scope))
        {
            return context.LocalAliases;
        }

        if (scope.EffectiveAliases is null)
        {
            CompleteAliasScopes();
        }

        return scope.EffectiveAliases!;
    }

    internal void CompleteAliasScopes()
    {
        var dependents = _fileScopes.Values.ToDictionary(scope => scope, _ => new List<FileAliasScope>());
        var seen = new Dictionary<FileAliasScope, HashSet<AliasDeclaration>>();
        var pending = new Queue<(FileAliasScope Scope, AliasDeclaration Alias)>();
        foreach (var scope in _fileScopes.Values)
        {
            scope.EffectiveAliases = [.. scope.LocalAliases];
            seen[scope] = new HashSet<AliasDeclaration>(scope.LocalAliases, ReferenceEqualityComparer.Instance);
            foreach (var alias in scope.LocalAliases)
            {
                pending.Enqueue((scope, alias));
            }

            foreach (var import in scope.Imports)
            {
                dependents[_fileScopes[import]].Add(scope);
            }
        }

        // Propagate each declaration through each file once, including cyclic import graphs.
        while (pending.TryDequeue(out var entry))
        {
            foreach (var scope in dependents[entry.Scope])
            {
                var shadowed = scope.LocalAliases.Any(local =>
                    local.Name == entry.Alias.Name && local.Namespaces.Any(ns => entry.Alias.Namespaces.Any(ns.Matches)));
                if (!shadowed && seen[scope].Add(entry.Alias))
                {
                    scope.EffectiveAliases!.Add(entry.Alias);
                    pending.Enqueue((scope, entry.Alias));
                }
            }
        }
    }

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
    public Declaration? FindSymbol(string[] qualifiedName, Namespace[] currentNamespaces, IReadOnlyList<AliasDeclaration> aliases) =>
        FindSymbol(qualifiedName, currentNamespaces, aliases, SourceLocation.Unknown, EquivalentDeclarations);

    internal Declaration? FindSymbol(
        string[] qualifiedName,
        Namespace[] currentNamespaces,
        IReadOnlyList<AliasDeclaration> aliases,
        SourceLocation location,
        Func<AliasDeclaration, AliasDeclaration, bool> equivalentAliases)
    {
        var alias = FindAlias(qualifiedName, currentNamespaces, aliases, location, equivalentAliases);
        if (alias != null)
        {
            return alias;
        }

        if (qualifiedName.Length == 1)
        {
            return _globalDeclarations.FirstOrDefault(d =>
                d.Name == qualifiedName[0] &&
                d.Namespaces.Any(ns1 => currentNamespaces.Any(ns1.Matches)));
        }

        var namespacePart = qualifiedName[..^1];
        var namePart = qualifiedName[^1];
        return _globalDeclarations.FirstOrDefault(d =>
            d.Name == namePart &&
            d.Namespaces.Any(ns => ns.Name.SequenceEqual(namespacePart)));
    }

    /// <summary>Returns true on first claim, false on cycle / diamond import.</summary>
    public bool ClaimImport(string canonicalPath) => _processedImports.Add(canonicalPath);

    private static AliasDeclaration? FindAlias(
        string[] qualifiedName,
        Namespace[] currentNamespaces,
        IReadOnlyList<AliasDeclaration> aliases,
        SourceLocation location,
        Func<AliasDeclaration, AliasDeclaration, bool> equivalentAliases)
    {
        var namespacePart = qualifiedName[..^1];
        var namePart = qualifiedName[^1];
        var matches = qualifiedName.Length == 1
            ? aliases.Where(a =>
                a.Name == qualifiedName[0] &&
                a.Namespaces.Any(ns1 => currentNamespaces.Any(ns1.Matches)))
            : aliases.Where(a =>
                a.Name == namePart &&
                a.Namespaces.Any(ns => ns.Name.SequenceEqual(namespacePart)));
        AliasDeclaration? result = null;
        foreach (var alias in matches)
        {
            if (result is not null && !ReferenceEquals(result, alias) && !equivalentAliases(result, alias))
            {
                throw new SemanticErrorException($"Ambiguous type alias '{string.Join(".", qualifiedName)}'", location);
            }

            result ??= alias;
        }

        return result;
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
