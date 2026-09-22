using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bond.Parser.Syntax;
using static Bond.Parser.Compatibility.SchemaContractValidation;

namespace Bond.Parser.Compatibility;

internal sealed class SchemaContractBuilder(bool includeImports, bool allowUnresolvedTypes)
{
    private readonly Dictionary<string, Declaration> _declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ContractConstant>> _enums = new(StringComparer.Ordinal);
    private readonly HashSet<string> _roots = new(StringComparer.Ordinal);

    internal SchemaContract Build(Syntax.Bond schema)
    {
        Require(schema is not null && schema.Declarations is not null && schema.ResolvedDeclarations is not null, "Missing schema declarations.");

        var seen = new HashSet<Declaration>(ReferenceEqualityComparer.Instance);
        var rootAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in schema!.Declarations)
        {
            ValidateDeclaration(declaration);
            if (declaration is AliasDeclaration)
            {
                Require(rootAliases.Add(declaration.Name), $"Duplicate alias '{declaration.Name}'.", declaration.Name, DiagnosticIds.DuplicateDeclaration);
                continue;
            }

            _roots.Add(Identity(declaration));
            Add(declaration);
            seen.Add(declaration);
        }

        if (includeImports)
        {
            foreach (var declaration in schema.ResolvedDeclarations)
            {
                ValidateDeclaration(declaration);
                // Aliases are scoped to their declaring file; equal qualified names are not global duplicates.
                if (declaration is AliasDeclaration || !seen.Add(declaration))
                {
                    continue;
                }

                Add(declaration);
            }
        }

        foreach (var enumeration in _declarations.Values.OfType<EnumDeclaration>())
        {
            _enums.Add(Identity(enumeration), EnumValues(enumeration));
        }

        // Validate unused root aliases too, but compare their expanded payload types only at uses.
        foreach (var alias in schema.Declarations.OfType<AliasDeclaration>())
        {
            Shape(alias.AliasedType, Parameters(alias), new HashSet<Declaration>(ReferenceEqualityComparer.Instance) { alias }, Identity(alias));
        }

        return Normalize(new SchemaContract(includeImports, allowUnresolvedTypes,
            _declarations.Values.Select(Project).ToArray()));
    }

    internal static string Identity(Declaration declaration)
    {
        var schemaNamespace = declaration.Namespaces.FirstOrDefault(n => n.LanguageQualifier is null)
            ?? declaration.Namespaces.LastOrDefault();

        return string.Join(".", (schemaNamespace?.Name ?? []).Append(declaration.Name));
    }

    private static void ValidateDeclaration(Declaration declaration)
    {
        Require(declaration is not null && ValidName(declaration.Name, qualified: false)
            && declaration.Namespaces is not null && declaration.TypeParameters is not null, "Malformed declaration.");

        foreach (var schemaNamespace in declaration.Namespaces)
        {
            Require(schemaNamespace is not null && schemaNamespace.Name is not null && schemaNamespace.Name.Length != 0
                && schemaNamespace.Name.All(part => ValidName(part, qualified: false)), "Invalid namespace.", declaration.Name);
        }

        var parameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in declaration.TypeParameters)
        {
            Require(parameter is not null && ValidName(parameter.Name, qualified: false) && parameters.Add(parameter.Name)
                && parameter.Constraint is TypeConstraint.None or TypeConstraint.Value, "Invalid or duplicate type parameter.", declaration.Name);
        }
    }

    private void Add(Declaration declaration)
    {
        var name = Identity(declaration);
        if (!_declarations.TryGetValue(name, out var existing))
        {
            _declarations.Add(name, declaration);
            return;
        }

        if (existing is ForwardDeclaration && declaration is StructDeclaration or ForwardDeclaration
            || declaration is ForwardDeclaration && existing is StructDeclaration)
        {
            Require(existing.TypeParameters.Select(p => p.Constraint).SequenceEqual(declaration.TypeParameters.Select(p => p.Constraint)),
                $"Forward declaration '{name}' has inconsistent generic parameters.", name);
            if (declaration is StructDeclaration)
            {
                _declarations[name] = declaration;
            }

            return;
        }

        throw Invalid($"Duplicate declaration definition '{name}'.", name, DiagnosticIds.DuplicateDeclaration);
    }

    private static Dictionary<string, TypeShape> Parameters(Declaration declaration) =>
        declaration.TypeParameters.Select((p, index) => (p.Name, Type: new TypeShape("parameter", null, index, [])))
            .ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);

    private ContractDeclaration Project(Declaration declaration)
    {
        var name = Identity(declaration);
        var parameters = Parameters(declaration);
        TypeShape Type(BondType type) => Shape(type, parameters, new HashSet<Declaration>(ReferenceEqualityComparer.Instance), name);

        ValidateAttributes(declaration switch
        {
            StructDeclaration d => d.Attributes,
            EnumDeclaration d => d.Attributes,
            ServiceDeclaration d => d.Attributes,
            _ => []
        }, name);

        IReadOnlyList<ContractField> fields = declaration is StructDeclaration structure
            ? ProjectFields(structure, parameters)
            : [];

        var methods = new List<ContractMethod>();
        if (declaration is ServiceDeclaration service)
        {
            Require(service.Methods is not null, "Missing service methods.", name);
            var methodNames = new HashSet<string>(StringComparer.Ordinal);
            TypeShape MethodShape(MethodType type) => type switch
            {
                MethodType.Void => TypeShape.Of("void"),
                MethodType.Unary u => Type(u.Type),
                MethodType.Streaming s => TypeShape.Of("stream", Type(s.Type)),
                _ => throw Invalid("Invalid method type.", name)
            };

            foreach (var method in service.Methods!)
            {
                Require(method is not null, "Null method.", name);
                if (!methodNames.Add(method!.Name))
                {
                    throw Invalid($"Duplicate method '{method.Name}'.", name, DiagnosticIds.DuplicateMethod);
                }

                var (kind, input, result) = method switch
                {
                    FunctionMethod f => ("function", MethodShape(f.InputType), MethodShape(f.ResultType)),
                    EventMethod e => ("event", MethodShape(e.InputType), TypeShape.Of("void")),
                    _ => throw Invalid("Unknown method kind.", name)
                };
                ValidateAttributes(method.Attributes, name + "." + method.Name);
                methods.Add(new ContractMethod(method.Name, kind, input, result));
            }
        }

        var baseType = declaration switch
        {
            StructDeclaration s => s.BaseType,
            ServiceDeclaration s => s.BaseType,
            _ => null
        };
        var declarationKind = declaration switch
        {
            StructDeclaration => "struct",
            ForwardDeclaration => "forward",
            EnumDeclaration => "enum",
            ServiceDeclaration => "service",
            _ => throw Invalid($"Unknown declaration '{name}'.", name)
        };
        var constraints = declaration.TypeParameters
            .Select(parameter => parameter.Constraint == TypeConstraint.Value ? "value" : "none")
            .ToArray();

        return new ContractDeclaration(name, declarationKind, _roots.Contains(name), constraints,
            baseType is null ? null : Type(baseType), fields, _enums.GetValueOrDefault(name) ?? [], methods);
    }

    private IReadOnlyList<ContractField> ProjectFields(StructDeclaration declaration, Dictionary<string, TypeShape> parameters)
    {
        var name = Identity(declaration);
        Require(declaration.Fields is not null, "Missing struct fields.", name);

        var fields = new List<ContractField>();
        var ordinals = new HashSet<ushort>();
        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in declaration.Fields!)
        {
            Require(field is not null, "Null field.", name);
            if (!ordinals.Add(field!.Ordinal) || !fieldNames.Add(field.Name))
            {
                throw Invalid($"Duplicate field ordinal or name '{field.Ordinal}: {field.Name}'.", name, DiagnosticIds.DuplicateField);
            }

            var type = Shape(field.Type, parameters, new HashSet<Declaration>(ReferenceEqualityComparer.Instance), name);
            if (field.DefaultValue is Default.Nothing && type.Kind != "maybe")
            {
                type = TypeShape.Of("maybe", type);
            }

            ValidateAttributes(field.Attributes, name + "." + field.Name);
            var jsonName = field.Attributes
                .FirstOrDefault(attribute => attribute.QualifiedName.Length == 1 && attribute.QualifiedName[0] == "JsonName")
                ?.Value ?? field.Name;

            string modifier;
            if (type.Kind is "meta_name" or "meta_full_name")
            {
                modifier = "required_optional";
            }
            else
            {
                modifier = field.Modifier switch
                {
                    FieldModifier.Optional => "optional",
                    FieldModifier.Required => "required",
                    FieldModifier.RequiredOptional => "required_optional",
                    _ => throw Invalid("Invalid field modifier.", name + "." + field.Name)
                };
            }

            fields.Add(new ContractField(field.Ordinal, field.Name, jsonName, modifier,
                type, DefaultValue(field.DefaultValue, type, declaration)));
        }

        return fields;
    }

    private TypeShape Shape(BondType type, Dictionary<string, TypeShape> parameters,
        HashSet<Declaration> aliases, string location, int depth = 0)
    {
        Require(type is not null && depth < 64, "Missing, cyclic, or excessively nested type expression.", location);
        TypeShape Child(BondType child) => Shape(child, parameters, aliases, location, depth + 1);

        switch (type)
        {
            case BondType.TypeReference reference:
                {
                    ValidateDeclaration(reference.Declaration);
                    Require(reference.TypeArguments is not null, "Missing type arguments.", location);
                    var declaration = reference.Declaration;
                    var arguments = reference.TypeArguments!.Select(Child).ToArray();
                    Require(arguments.Length == declaration.TypeParameters.Length,
                        $"Type '{Identity(declaration)}' has the wrong number of generic arguments.", location);
                    if (declaration is AliasDeclaration alias)
                    {
                        Require(aliases.Add(alias), $"Cyclic alias '{alias.Name}'.", location);
                        var substitutions = alias.TypeParameters.Select((p, i) => (p.Name, Type: arguments[i]))
                            .ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
                        var expanded = Shape(alias.AliasedType, substitutions, aliases, location, depth + 1);
                        aliases.Remove(alias);
                        return expanded;
                    }

                    var kind = declaration switch
                    {
                        StructDeclaration or ForwardDeclaration => "struct",
                        EnumDeclaration => "enum",
                        ServiceDeclaration => "service",
                        _ => throw Invalid("Invalid named type.", location)
                    };
                    if (declaration is EnumDeclaration enumeration && !_enums.ContainsKey(Identity(enumeration)))
                    {
                        _enums.Add(Identity(enumeration), EnumValues(enumeration));
                    }

                    return new TypeShape(kind, Identity(declaration), null, ReadOnly(arguments));
                }
            case BondType.UnresolvedType unresolved:
                Require(allowUnresolvedTypes, $"Unresolved type '{string.Join(".", unresolved.QualifiedName)}'. Resolve imports or explicitly allow unresolved types.",
                    location, DiagnosticIds.UnresolvedType);
                return new TypeShape("unresolved", string.Join(".", unresolved.QualifiedName), null, ReadOnly(unresolved.TypeArguments.Select(Child)));
            case BondType.TypeParameter parameter:
                Require(parameters.TryGetValue(parameter.Param.Name, out var substitution), $"Unknown type parameter '{parameter.Param.Name}'.", location);
                return substitution!;
            case BondType.IntTypeArg integer:
                return new TypeShape("integer", null, integer.Value, []);
            case BondType.List list:
                return TypeShape.Of("list", Child(list.ElementType));
            case BondType.Vector vector:
                return TypeShape.Of("vector", Child(vector.ElementType));
            case BondType.Set set:
                return TypeShape.Of("set", Child(set.KeyType));
            case BondType.Map map:
                return TypeShape.Of("map", Child(map.KeyType), Child(map.ValueType));
            case BondType.Nullable nullable:
                return TypeShape.Of("nullable", Child(nullable.ElementType));
            case BondType.Maybe maybe:
                return TypeShape.Of("maybe", Child(maybe.ElementType));
            case BondType.Bonded bonded:
                return TypeShape.Of("bonded", Child(bonded.StructType));
            case BondType.MetaName:
                return TypeShape.Of("meta_name");
            case BondType.MetaFullName:
                return TypeShape.Of("meta_full_name");
            default:
                return TypeShape.Of(type!.ToString()!);
        }
    }

    private static void ValidateAttributes(Syntax.Attribute[] attributes, string location)
    {
        Require(attributes is not null, "Missing attributes.", location);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes!)
        {
            Require(attribute is not null && attribute.QualifiedName is not null && attribute.Value is not null, "Invalid attribute.", location);
            var name = string.Join(".", attribute.QualifiedName);
            Require(ValidName(name) && names.Add(name), $"Invalid or duplicate attribute '{name}'.", location);
        }
    }

    private static IReadOnlyList<ContractConstant> EnumValues(EnumDeclaration enumeration)
    {
        Require(enumeration.Constants is not null, "Missing enum constants.", Identity(enumeration));
        var values = new List<ContractConstant>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long next = 0;
        foreach (var constant in enumeration.Constants!)
        {
            Require(constant is not null, "Null enum constant.", Identity(enumeration));
            if (!names.Add(constant!.Name))
            {
                throw Invalid($"Duplicate enum member '{constant.Name}'.", Identity(enumeration), DiagnosticIds.DuplicateEnumMember);
            }

            if (constant.Value is null && next > int.MaxValue)
            {
                throw Invalid($"Implicit enum value '{constant.Name}' overflows signed int32; specify an explicit value.",
                    Identity(enumeration) + "." + constant.Name, DiagnosticIds.EnumOverflow);
            }

            var value = constant.Value is long explicitValue ? unchecked((int)explicitValue) : (int)next;
            values.Add(new ContractConstant(constant.Name, value));
            next = (long)value + 1;
        }

        return ReadOnly(values);
    }

    private ContractDefault DefaultValue(Default? value, TypeShape type, Declaration owner)
    {
        var location = Identity(owner);
        if (type.Kind == "maybe" || value is Default.Nothing)
        {
            Require(value is null or Default.Nothing, "A maybe field must use the nothing default.", location);
            return new ContractDefault("nothing", "");
        }

        if (value is Default.Enum member)
        {
            Require(type.Kind is "enum" or "unresolved", "An enum default requires an enum field.", location);
            if (type.Kind == "unresolved")
            {
                return new ContractDefault("symbol", member.Identifier);
            }

            var constants = _enums.GetValueOrDefault(type.Name!);
            var constant = constants?.FirstOrDefault(c => c.Name == member.Identifier || type.Name + "." + c.Name == member.Identifier);
            Require(constant is not null, $"Unknown enum default '{member.Identifier}' for '{type.Name}'.", location);
            return new ContractDefault("integer", constant!.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (value is Default.Integer integer)
        {
            if (type.Kind is "float" or "double")
            {
                return Floating((double)integer.Value, type);
            }

            return new ContractDefault("integer", integer.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (value is Default.Float number)
        {
            return Floating(number.Value, type);
        }

        if (value is Default.Bool boolean)
        {
            return new ContractDefault("bool", boolean.Value ? "true" : "false");
        }

        if (value is Default.String text)
        {
            return new ContractDefault("string", text.Value);
        }

        return ImplicitDefault(type, Identity(owner));
    }

    internal static ContractDefault ImplicitDefault(TypeShape type, string ownerName) => type.Kind switch
    {
        "int8" or "int16" or "int32" or "int64" or "uint8" or "uint16" or "uint32" or "uint64" or "enum" => new("integer", "0"),
        "float" or "double" => new("float", "0"),
        "bool" => new("bool", "false"),
        "string" or "wstring" => new("string", ""),
        "nullable" => new("nullable", ""),
        "struct" or "bonded" => new("struct", ""),
        "parameter" or "unresolved" => new("generic", ""),
        "meta_name" => new("meta", ownerName.Split('.').Last()),
        "meta_full_name" => new("meta", ownerName),
        _ => new("empty", "")
    };

    private static ContractDefault Floating(double number, TypeShape type)
    {
        if (type.Kind == "float")
        {
            number = (float)number;
        }

        return new ContractDefault("float", number.ToString("R", CultureInfo.InvariantCulture));
    }
}
