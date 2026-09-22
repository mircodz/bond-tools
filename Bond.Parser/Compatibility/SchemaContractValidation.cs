using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Bond.Parser.Compatibility;

internal static class SchemaContractValidation
{
    internal static SchemaContract Create(Syntax.Bond schema, bool includeImports, bool allowUnresolvedTypes)
    {
        try
        {
            return new SchemaContractBuilder(includeImports, allowUnresolvedTypes).Build(schema);
        }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException
                                   or OverflowException)
        {
            throw Invalid($"Malformed schema: {ex.Message}");
        }
    }

    internal static InvalidDataException Invalid(string message, string location = "schema",
        string id = DiagnosticIds.InvalidSchema)
    {
        var error = new InvalidDataException(message);
        error.Data["Location"] = location;
        error.Data["Id"] = id;
        return error;
    }

    internal static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());

    internal static SchemaContract Normalize(SchemaContract document)
    {
        ValidateContract(document);

        var declarations = document.Declarations
            .OrderBy(declaration => declaration.Name, StringComparer.Ordinal)
            .Select(NormalizeDeclaration);

        return document with
        {
            Declarations = ReadOnly(declarations)
        };
    }

    private static ContractDeclaration NormalizeDeclaration(ContractDeclaration declaration)
    {
        var fields = declaration.Fields
            .OrderBy(field => field.Ordinal)
            .Select(field => field with
            {
                Type = Freeze(field.Type),
                Default = CanonicalDefault(field.Default)
            });
        var methods = declaration.Methods
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .Select(method => method with
            {
                Input = Freeze(method.Input),
                Result = Freeze(method.Result)
            });

        return declaration with
        {
            Constraints = ReadOnly(declaration.Constraints),
            BaseType = declaration.BaseType is null ? null : Freeze(declaration.BaseType),
            Fields = ReadOnly(fields),
            Constants = ReadOnly(declaration.Constants.OrderBy(constant => constant.Name, StringComparer.Ordinal)),
            Methods = ReadOnly(methods)
        };
    }

    private static TypeShape Freeze(TypeShape type) => type with
    {
        Arguments = ReadOnly(type.Arguments.Select(Freeze))
    };

    private static ContractDefault CanonicalDefault(ContractDefault value) => value.Kind switch
    {
        "integer" => value with
        {
            Value = BigInteger.Parse(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
        },
        "float" => value with
        {
            Value = double.Parse(value.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
        },
        _ => value
    };

    private static void ValidateContract(SchemaContract document)
    {
        Require(document.Declarations is not null, "Missing schema declarations.");
        var declarations = new Dictionary<string, ContractDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in document.Declarations!)
        {
            Require(declaration is not null && ValidName(declaration.Name), "Invalid declaration name.");
            if (!declarations.TryAdd(declaration.Name, declaration))
            {
                throw Invalid($"Duplicate declaration '{declaration.Name}'.", declaration.Name, DiagnosticIds.DuplicateDeclaration);
            }

            Require(declaration.Kind is "struct" or "forward" or "enum" or "service",
                $"Invalid declaration kind '{declaration.Kind}'.", declaration.Name);
            Require(declaration.Constraints is not null && declaration.Constraints.All(constraint => constraint is "none" or "value"),
                "Invalid generic constraints.", declaration.Name);
            Require(declaration.Fields is not null && declaration.Constants is not null && declaration.Methods is not null,
                "Missing declaration members.", declaration.Name);
            Require(declaration.Kind == "struct" || declaration.Fields.Count == 0, "Only structs can have fields.", declaration.Name);
            Require(declaration.Kind == "enum" || declaration.Constants.Count == 0, "Only enums can have constants.", declaration.Name);
            Require(declaration.Kind != "enum" || declaration.Constraints.Count == 0, "Enums cannot have generic parameters.", declaration.Name);
            Require(declaration.Kind == "service" || declaration.Methods.Count == 0, "Only services can have methods.", declaration.Name);
            Require(declaration.Kind is "struct" or "service" || declaration.BaseType is null, "Invalid base type.", declaration.Name);

            var ordinals = new HashSet<int>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in declaration.Fields)
            {
                Require(field is not null && ValidName(field.Name, qualified: false), "Invalid field name.", declaration.Name);
                Require(field.Ordinal >= 0 && field.Ordinal <= ushort.MaxValue && field.JsonName is not null,
                    "Invalid field ordinal or JSON name.", declaration.Name);
                if (!ordinals.Add(field.Ordinal) || !names.Add(field.Name))
                {
                    throw Invalid($"Duplicate field ordinal or name '{field.Ordinal}: {field.Name}'.", declaration.Name, DiagnosticIds.DuplicateField);
                }

                Require(field.Modifier is "optional" or "required" or "required_optional", "Invalid field modifier.", declaration.Name);
                ValidateDefault(field.Default, declaration.Name + "." + field.Name);
            }

            names.Clear();
            foreach (var constant in declaration.Constants)
            {
                Require(constant is not null && ValidName(constant.Name, qualified: false), "Invalid enum member.", declaration.Name);
                if (!names.Add(constant.Name))
                {
                    throw Invalid($"Duplicate enum member '{constant.Name}'.", declaration.Name, DiagnosticIds.DuplicateEnumMember);
                }
            }

            names.Clear();
            foreach (var method in declaration.Methods)
            {
                Require(method is not null && ValidName(method.Name, qualified: false), "Invalid method name.", declaration.Name);
                if (!names.Add(method.Name))
                {
                    throw Invalid($"Duplicate method '{method.Name}'.", declaration.Name, DiagnosticIds.DuplicateMethod);
                }

                Require(method.Kind is "function" or "event", "Invalid method kind.", declaration.Name);
            }
        }

        foreach (var declaration in declarations.Values)
        {
            if (declaration.BaseType is not null)
            {
                ValidateType(declaration.BaseType, declaration, document, declarations);
                Require(declaration.BaseType.Kind == (declaration.Kind == "service" ? "service" : "struct")
                    || document.AllowsUnresolvedTypes && declaration.BaseType.Kind == "unresolved", "Invalid inheritance type.", declaration.Name);
            }

            foreach (var field in declaration.Fields)
            {
                ValidateType(field.Type, declaration, document, declarations);
                Require(field.Type.Kind is not ("integer" or "void" or "stream" or "service"),
                    "Invalid field type.", declaration.Name + "." + field.Name);
                ValidateFieldDefault(field, declaration.Name + "." + field.Name);
            }

            foreach (var method in declaration.Methods)
            {
                ValidateType(method.Input, declaration, document, declarations);
                ValidateType(method.Result, declaration, document, declarations);
            }
        }

        ValidateInheritance(declarations);
    }

    private static void ValidateType(TypeShape type, ContractDeclaration owner, SchemaContract document,
        Dictionary<string, ContractDeclaration> declarations, int depth = 0)
    {
        Require(type is not null && depth < 64 && type.Arguments is not null, "Invalid or excessively nested type expression.", owner.Name);

        var argumentCount = type.Arguments.Count;
        if (type.Kind is "struct" or "enum" or "service" or "unresolved")
        {
            Require(ValidName(type.Name) && type.Integer is null, "Invalid named type.", owner.Name);
            if (type.Kind == "unresolved")
            {
                Require(document.AllowsUnresolvedTypes, $"Unresolved type '{type.Name}'.", owner.Name, DiagnosticIds.UnresolvedType);
            }
            else if (declarations.TryGetValue(type.Name!, out var target))
            {
                Require(type.Kind == (target.Kind == "forward" ? "struct" : target.Kind), $"Type kind does not match '{type.Name}'.", owner.Name);
                Require(argumentCount == target.Constraints.Count, $"Type '{type.Name}' has the wrong number of generic arguments.", owner.Name);
                if (target.Kind == "forward" && !document.AllowsUnresolvedTypes)
                {
                    throw Invalid($"No complete definition for '{type.Name}'; its layout cannot be established.", owner.Name, DiagnosticIds.IncompleteDefinition);
                }
            }
            else
            {
                Require(!document.IncludesImports || document.AllowsUnresolvedTypes,
                    $"Missing definition for type '{type.Name}'.", owner.Name, DiagnosticIds.UnresolvedType);
            }
        }
        else if (type.Kind is "parameter" or "integer")
        {
            Require(type.Name is null && type.Integer is not null && argumentCount == 0, "Invalid generic argument.", owner.Name);
            if (type.Kind == "parameter")
            {
                Require(type.Integer >= 0 && type.Integer < owner.Constraints.Count, "Invalid generic parameter position.", owner.Name);
            }
        }
        else
        {
            var expectedArgumentCount = type.Kind switch
            {
                "map" => 2,
                "list" or "vector" or "set" or "nullable" or "maybe" or "bonded" or "stream" => 1,
                "int8" or "int16" or "int32" or "int64" or "uint8" or "uint16" or "uint32" or "uint64"
                    or "float" or "double" or "string" or "wstring" or "bool" or "blob" or "meta_name" or "meta_full_name" or "void" => 0,
                _ => -1
            };
            Require(expectedArgumentCount >= 0 && argumentCount == expectedArgumentCount && type.Name is null && type.Integer is null,
                $"Invalid type '{type.Kind}'.", owner.Name);
        }

        foreach (var argument in type.Arguments)
        {
            ValidateType(argument, owner, document, declarations, depth + 1);
        }

        if (type.Kind is "struct" or "enum" or "service" && declarations.TryGetValue(type.Name!, out var definition))
        {
            for (var i = 0; i < definition.Constraints.Count; i++)
            {
                if (definition.Constraints[i] == "value")
                {
                    Require(IsValueType(type.Arguments[i], owner)
                            || document.AllowsUnresolvedTypes && type.Arguments[i].Kind == "unresolved",
                        $"Type argument {i} for '{type.Name}' does not satisfy its value constraint.", owner.Name);
                }
            }
        }

        if (type.Kind is not ("struct" or "enum" or "service" or "unresolved"))
        {
            Require(type.Arguments.All(argument => argument.Kind is not ("integer" or "void" or "service" or "stream")),
                "Invalid container element type.", owner.Name);
        }
    }

    private static bool IsValueType(TypeShape type, ContractDeclaration owner) => type.Kind is
        "int8" or "int16" or "int32" or "int64" or "uint8" or "uint16" or "uint32" or "uint64"
        or "float" or "double" or "bool" or "enum"
        || type.Kind == "parameter" && owner.Constraints[(int)type.Integer!.Value] == "value";

    private static void ValidateInheritance(Dictionary<string, ContractDeclaration> declarations)
    {
        var complete = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations.Values)
        {
            var active = new HashSet<string>(StringComparer.Ordinal);
            var current = declaration;
            while (!complete.Contains(current.Name))
            {
                Require(active.Add(current.Name), $"Cyclic inheritance involving '{current.Name}'.", current.Name);
                if (current.BaseType?.Name is not { } name || !declarations.TryGetValue(name, out current!))
                {
                    break;
                }
            }

            complete.UnionWith(active);
        }
    }

    private static void ValidateFieldDefault(ContractField field, string location)
    {
        var type = field.Type;
        var value = field.Default;
        var expectedKind = type.Kind switch
        {
            "int8" or "int16" or "int32" or "int64" or "uint8" or "uint16" or "uint32" or "uint64" or "enum" => "integer",
            "float" or "double" => "float",
            "string" or "wstring" => "string",
            "bool" => "bool",
            "nullable" => "nullable",
            "maybe" => "nothing",
            "struct" or "bonded" => "struct",
            "parameter" => "generic",
            "meta_name" or "meta_full_name" => "meta",
            "unresolved" => value.Kind,
            _ => "empty"
        };
        Require(value.Kind == expectedKind, $"Default kind '{value.Kind}' does not match type '{type.Kind}'.", location);

        if (value.Kind == "integer" && type.Kind != "unresolved")
        {
            var number = BigInteger.Parse(value.Value, CultureInfo.InvariantCulture);
            var width = type.Kind switch
            {
                "int8" or "uint8" => 8,
                "int16" or "uint16" => 16,
                "int32" or "uint32" or "enum" => 32,
                _ => 64
            };
            var unsigned = type.Kind.StartsWith("uint", StringComparison.Ordinal);
            var maximum = (BigInteger.One << (unsigned ? width : width - 1)) - 1;
            var minimum = unsigned ? BigInteger.Zero : -maximum - 1;
            Require(number >= minimum && number <= maximum, $"Default value is out of range for '{type.Kind}'.", location);
        }
    }

    private static void ValidateDefault(ContractDefault value, string location)
    {
        Require(value is not null && value.Value is not null, "Invalid default value.", location);
        var valid = value!.Kind switch
        {
            "integer" => BigInteger.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "float" => double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
            "bool" => value.Value is "true" or "false",
            "string" or "symbol" or "meta" => true,
            "nothing" or "empty" or "nullable" or "struct" or "generic" => value.Value.Length == 0,
            _ => false
        };
        Require(valid, $"Invalid default kind or value '{value.Kind}'.", location);
    }

    internal static bool ValidName(string? name, bool qualified = true) => !string.IsNullOrWhiteSpace(name)
        && name.Split('.').All(part => part.Length != 0 && (char.IsLetter(part[0]) || part[0] == '_')
            && part.All(c => char.IsLetterOrDigit(c) || c == '_'))
        && (qualified || !name.Contains('.'));

    internal static void Require([DoesNotReturnIf(false)] bool condition, string message,
        string location = "schema", string id = DiagnosticIds.InvalidSchema)
    {
        if (!condition)
        {
            throw Invalid(message, location, id);
        }
    }
}
