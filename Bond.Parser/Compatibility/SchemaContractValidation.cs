using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Bond.Parser.Compatibility;

/// <summary>
/// Builds a contract and checks the semantic rules the parser skips with --ignore-imports, or that a hand-built AST
/// may violate. Structural rules (names, duplicates, kinds) are enforced while building.
/// </summary>
internal static class SchemaContractValidation
{
    internal static SchemaContract Create(Syntax.Bond schema, bool includeImports, bool allowUnresolvedTypes)
    {
        SchemaContract contract;
        try
        {
            contract = new SchemaContractBuilder(includeImports, allowUnresolvedTypes).Build(schema);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw Invalid($"Malformed schema: {ex.Message}");
        }

        Validate(contract);
        return contract;
    }

    internal static InvalidDataException Invalid(string message, string location = "schema",
        string id = DiagnosticIds.InvalidSchema)
    {
        var error = new InvalidDataException(message);
        error.Data["Location"] = location;
        error.Data["Id"] = id;
        return error;
    }

    private static void Validate(SchemaContract contract)
    {
        var declarations = contract.Declarations.ToDictionary(declaration => declaration.Name, StringComparer.Ordinal);
        foreach (var declaration in contract.Declarations)
        {
            if (declaration.BaseType is not null)
            {
                ValidateType(declaration.BaseType, declaration, contract, declarations);
                Require(declaration.BaseType.Kind == (declaration.Kind == "service" ? "service" : "struct")
                    || contract.AllowsUnresolvedTypes && declaration.BaseType.Kind == "unresolved", "Invalid inheritance type.", declaration.Name);
            }

            foreach (var field in declaration.Fields)
            {
                ValidateType(field.Type, declaration, contract, declarations);
                Require(field.Type.Kind is not ("integer" or "void" or "stream" or "service"),
                    "Invalid field type.", declaration.Name + "." + field.Name);
                ValidateFieldDefault(field, declaration.Name + "." + field.Name);
            }

            foreach (var method in declaration.Methods)
            {
                ValidateType(method.Input, declaration, contract, declarations);
                ValidateType(method.Result, declaration, contract, declarations);
            }
        }

        ValidateInheritance(declarations);
    }

    private static void ValidateType(TypeShape type, ContractDeclaration owner, SchemaContract contract,
        Dictionary<string, ContractDeclaration> declarations)
    {
        if (type.Kind is "struct" or "enum" or "service")
        {
            if (declarations.TryGetValue(type.Name!, out var target))
            {
                Require(type.Kind == (target.Kind == "forward" ? "struct" : target.Kind), $"Type kind does not match '{type.Name}'.", owner.Name);
                Require(type.Arguments.Count == target.Constraints.Count, $"Type '{type.Name}' has the wrong number of generic arguments.", owner.Name);
                if (target.Kind == "forward" && !contract.AllowsUnresolvedTypes)
                {
                    throw Invalid($"No complete definition for '{type.Name}'; its layout cannot be established.", owner.Name, DiagnosticIds.IncompleteDefinition);
                }

                for (var i = 0; i < target.Constraints.Count; i++)
                {
                    Require(target.Constraints[i] != "value" || IsValueType(type.Arguments[i], owner)
                            || contract.AllowsUnresolvedTypes && type.Arguments[i].Kind == "unresolved",
                        $"Type argument {i} for '{type.Name}' does not satisfy its value constraint.", owner.Name);
                }
            }
            else
            {
                Require(!contract.IncludesImports || contract.AllowsUnresolvedTypes,
                    $"Missing definition for type '{type.Name}'.", owner.Name, DiagnosticIds.UnresolvedType);
            }
        }
        else if (type.Kind is not "unresolved")
        {
            Require(type.Arguments.All(argument => argument.Kind is not ("integer" or "void" or "service" or "stream")),
                "Invalid container element type.", owner.Name);
        }

        foreach (var argument in type.Arguments)
        {
            ValidateType(argument, owner, contract, declarations);
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
