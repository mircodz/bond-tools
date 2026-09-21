using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Bond.Parser.Syntax;

namespace Bond.Parser.Parser;

/// <summary>Binds types in their declaration's namespace and alias environment.</summary>
public static class TypeResolver
{
    public static Syntax.Bond Resolve(Syntax.Bond ast, SymbolTable symbols, IReadOnlyList<AliasDeclaration> aliases) =>
        new Binder(symbols, aliases).Resolve(ast);

    private sealed class Binder(SymbolTable symbols, IReadOnlyList<AliasDeclaration> aliases)
    {
        private readonly Dictionary<Declaration, Declaration> _resolved = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Declaration> _active = new(ReferenceEqualityComparer.Instance);

        public Syntax.Bond Resolve(Syntax.Bond ast)
        {
            var declarations = ast.Declarations.Select(ResolveDeclaration).ToArray();
            var environment = symbols.Declarations.Select(ResolveDeclaration)
                .Concat(declarations)
                .GroupBy(d => d.QualifiedName)
                .Select(group => group.LastOrDefault(d => d is not ForwardDeclaration) ?? group.Last())
                .ToArray();

            return ast with
            {
                Declarations = declarations,
                ResolvedDeclarations = environment
            };
        }

        private Declaration ResolveDeclaration(Declaration declaration)
        {
            if (_resolved.TryGetValue(declaration, out var existing))
            {
                return existing;
            }

            if (!_active.Add(declaration))
            {
                throw new SemanticErrorException($"Cyclic definition of '{declaration.Name}'", declaration.Location);
            }

            try
            {
                var result = declaration switch
                {
                    StructDeclaration { IsView: true } view => ResolveView(view),
                    StructDeclaration structure => structure with
                    {
                        BaseType = structure.BaseType is null
                            ? null
                            : ResolveType(structure.BaseType, structure, structure.Location),
                        Fields = structure.Fields.Select(field => field with
                        {
                            Type = ResolveType(field.Type, structure, field.Location)
                        }).ToArray()
                    },
                    AliasDeclaration alias => alias with
                    {
                        AliasedType = ResolveType(alias.AliasedType, alias, alias.Location)
                    },
                    ServiceDeclaration service => service with
                    {
                        BaseType = service.BaseType is null
                            ? null
                            : ResolveType(service.BaseType, service, service.Location),
                        Methods = service.Methods.Select(method => ResolveMethod(method, service)).ToArray()
                    },
                    _ => declaration
                };

                _resolved.Add(declaration, result);
                symbols.SetResolvedContext(declaration, result);
                return result;
            }
            catch (SemanticErrorException error) when (symbols.GetSourceFile(declaration) is { } file)
            {
                throw new ParseErrorsException([new ParseError(error.Message, file, error.Location.Line, error.Location.Column)]);
            }
            finally
            {
                _active.Remove(declaration);
            }
        }

        private StructDeclaration ResolveView(StructDeclaration view)
        {
            if (view.ViewTarget is null)
            {
                throw new SemanticErrorException($"View '{view.Name}' has no target struct", view.Location);
            }

            if (view.TypeParameters.Length != 0)
            {
                throw new SemanticErrorException("A view inherits its type parameters from its target struct", view.Location);
            }

            var target = FindSymbol(view.ViewTarget, view);
            if (target is not StructDeclaration structure)
            {
                throw new SemanticErrorException($"View '{view.Name}' requires a defined struct target", view.Location);
            }

            if (_active.Contains(structure))
            {
                throw new SemanticErrorException($"Cyclic view definition involving '{view.Name}'", view.Location);
            }

            var source = (StructDeclaration)ResolveDeclaration(structure);
            return view with
            {
                TypeParameters = source.TypeParameters,
                BaseType = source.BaseType,
                Fields = source.Fields.Where(field => view.ViewFields.Contains(field.Name, StringComparer.Ordinal)).ToArray()
            };
        }

        private Method ResolveMethod(Method method, ServiceDeclaration service) => method switch
        {
            FunctionMethod function => function with
            {
                InputType = ResolveMethodType(function.InputType, service, function.Location),
                ResultType = ResolveMethodType(function.ResultType, service, function.Location)
            },
            EventMethod eventMethod => eventMethod with
            {
                InputType = ResolveMethodType(eventMethod.InputType, service, eventMethod.Location)
            },
            _ => method
        };

        private MethodType ResolveMethodType(MethodType type, Declaration owner, SourceLocation location) => type switch
        {
            MethodType.Unary unary => new MethodType.Unary(ResolveType(unary.Type, owner, location)),
            MethodType.Streaming streaming => new MethodType.Streaming(ResolveType(streaming.Type, owner, location)),
            _ => type
        };

        private BondType ResolveType(BondType type, Declaration owner, SourceLocation location) => type switch
        {
            BondType.List list => new BondType.List(ResolveType(list.ElementType, owner, location)),
            BondType.Vector vector => new BondType.Vector(ResolveType(vector.ElementType, owner, location)),
            BondType.Set set => new BondType.Set(ResolveType(set.KeyType, owner, location)),
            BondType.Map map => new BondType.Map(ResolveType(map.KeyType, owner, location), ResolveType(map.ValueType, owner, location)),
            BondType.Nullable nullable => new BondType.Nullable(ResolveType(nullable.ElementType, owner, location)),
            BondType.Maybe maybe => new BondType.Maybe(ResolveType(maybe.ElementType, owner, location)),
            BondType.Bonded bonded => new BondType.Bonded(ResolveType(bonded.StructType, owner, location)),
            BondType.UnresolvedType unresolved => ResolveName(unresolved, owner, location),
            BondType.TypeReference reference => ResolveReference(reference.Declaration, reference.TypeArguments, owner, location),
            _ => type
        };

        private BondType ResolveName(BondType.UnresolvedType type, Declaration owner, SourceLocation location)
        {
            var declaration = FindSymbol(type.QualifiedName, owner);
            if (declaration is null)
            {
                if (type.TypeArguments.Length == 0 && TryResolvePrimitive(type.QualifiedName, out var primitive))
                {
                    return primitive;
                }

                throw new SemanticErrorException($"Type '{string.Join(".", type.QualifiedName)}' not found in symbol table", location);
            }

            return ResolveReference(declaration, type.TypeArguments, owner, location);
        }

        private Declaration? FindSymbol(string[] name, Declaration owner)
        {
            var localAliases = symbols.GetAliases(owner);
            return symbols.FindSymbol(name, owner.Namespaces, localAliases ?? aliases);
        }

        private BondType ResolveReference(
            Declaration declaration,
            BondType[] arguments,
            Declaration owner,
            SourceLocation location)
        {
            if (declaration is ForwardDeclaration)
            {
                declaration = FindSymbol(declaration.QualifiedName.Split('.'), owner) ?? declaration;
            }

            var typeArguments = arguments.Select(argument => ResolveType(argument, owner, location)).ToArray();

            Declaration target;
            if (declaration is StructDeclaration structure
                && (_active.Contains(structure)
                    || (owner is AliasDeclaration && !_resolved.ContainsKey(structure))
                    || (structure.IsView && HasActiveViewSource(structure))))
            {
                // Keep recursive/forward references finite. The complete definition is in ResolvedDeclarations.
                target = ToForward(structure, location);
            }
            else
            {
                if (_active.Contains(declaration))
                {
                    throw new SemanticErrorException($"Cyclic definition of '{declaration.Name}'", location);
                }

                target = ResolveDeclaration(declaration);
            }

            if (typeArguments.Length != target.TypeParameters.Length)
            {
                throw new SemanticErrorException(
                    target.TypeParameters.Length == 0
                        ? $"Type '{target.Name}' is not a generic type"
                        : $"Type '{target.Name}' requires {target.TypeParameters.Length} type argument(s)", location);
            }

            return new BondType.TypeReference(target, typeArguments);
        }

        private bool HasActiveViewSource(StructDeclaration view)
        {
            var visited = new HashSet<Declaration>(ReferenceEqualityComparer.Instance);
            while (view.IsView && view.ViewTarget is not null && visited.Add(view))
            {
                if (FindSymbol(view.ViewTarget, view) is not StructDeclaration source)
                {
                    return false;
                }

                if (_active.Contains(source))
                {
                    return true;
                }

                view = source;
            }

            return false;
        }

        private TypeParam[] GetTypeParameters(StructDeclaration structure, SourceLocation location)
        {
            var visited = new HashSet<Declaration>(ReferenceEqualityComparer.Instance);
            while (structure.IsView && structure.ViewTarget is not null)
            {
                if (!visited.Add(structure))
                {
                    throw new SemanticErrorException($"Cyclic view definition involving '{structure.Name}'", location);
                }

                if (FindSymbol(structure.ViewTarget, structure) is not StructDeclaration source)
                {
                    throw new SemanticErrorException($"View '{structure.Name}' requires a defined struct target", location);
                }

                structure = source;
            }

            return structure.TypeParameters;
        }

        private ForwardDeclaration ToForward(StructDeclaration structure, SourceLocation location) => new()
        {
            Namespaces = structure.Namespaces,
            Name = structure.Name,
            TypeParameters = GetTypeParameters(structure, location),
            Location = structure.Location
        };
    }

    private static bool TryResolvePrimitive(string[] name, [NotNullWhen(true)] out BondType? primitive)
    {
        primitive = null;
        if (name.Length != 1)
        {
            return false;
        }

        primitive = name[0].ToLowerInvariant() switch
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
            "bool" => BondType.Bool.Instance,
            "string" => BondType.String.Instance,
            "wstring" => BondType.WString.Instance,
            "blob" => BondType.Blob.Instance,
            _ => null
        };

        return primitive is not null;
    }
}
