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
        return document with
        {
            Declarations = ReadOnly(document.Declarations.OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => d with
            {
                Constraints = ReadOnly(d.Constraints),
                BaseType = d.BaseType is null ? null : Freeze(d.BaseType),
                Fields = ReadOnly(d.Fields.OrderBy(f => f.Ordinal).Select(f => f with
                {
                    Type = Freeze(f.Type), Default = CanonicalDefault(f.Default)
                })),
                Constants = ReadOnly(d.Constants.OrderBy(c => c.Name, StringComparer.Ordinal)),
                Methods = ReadOnly(d.Methods.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => m with
                {
                    Input = Freeze(m.Input), Result = Freeze(m.Result)
                }))
            }))
        };
    }

    private static TypeShape Freeze(TypeShape type) => type with { Arguments = ReadOnly(type.Arguments.Select(Freeze)) };

    private static ContractDefault CanonicalDefault(ContractDefault value) => value.Kind switch
    {
        "integer" => value with { Value = BigInteger.Parse(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) },
        "float" => value with { Value = double.Parse(value.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture) },
        _ => value
    };

    private static void ValidateContract(SchemaContract document)
    {
        Require(document.Declarations is not null, "Missing schema declarations.");
        var declarations = new Dictionary<string, ContractDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in document.Declarations!)
        {
            Require(declaration is not null && ValidName(declaration.Name), "Invalid declaration name.");
            var d = declaration!;
            if (!declarations.TryAdd(d.Name, d))
                throw Invalid($"Duplicate declaration '{d.Name}'.", d.Name, DiagnosticIds.DuplicateDeclaration);
            Require(d.Kind is "struct" or "forward" or "enum" or "service", $"Invalid declaration kind '{d.Kind}'.", d.Name);
            Require(d.Constraints is not null && d.Constraints.All(c => c is "none" or "value"),
                "Invalid generic constraints.", d.Name);
            Require(d.Fields is not null && d.Constants is not null && d.Methods is not null, "Missing declaration members.", d.Name);
            Require(d.Kind == "struct" || d.Fields!.Count == 0, "Only structs can have fields.", d.Name);
            Require(d.Kind == "enum" || d.Constants!.Count == 0, "Only enums can have constants.", d.Name);
            Require(d.Kind != "enum" || d.Constraints.Count == 0, "Enums cannot have generic parameters.", d.Name);
            Require(d.Kind == "service" || d.Methods!.Count == 0, "Only services can have methods.", d.Name);
            Require(d.Kind is "struct" or "service" || d.BaseType is null, "Invalid base type.", d.Name);
            var ordinals = new HashSet<int>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in d.Fields!)
            {
                Require(field is not null && ValidName(field.Name, qualified: false), "Invalid field name.", d.Name);
                var f = field!;
                Require(f.Ordinal >= 0 && f.Ordinal <= ushort.MaxValue && f.JsonName is not null, "Invalid field ordinal or JSON name.", d.Name);
                if (!ordinals.Add(f.Ordinal) || !names.Add(f.Name))
                    throw Invalid($"Duplicate field ordinal or name '{f.Ordinal}: {f.Name}'.", d.Name, DiagnosticIds.DuplicateField);
                Require(f.Modifier is "optional" or "required" or "required_optional", "Invalid field modifier.", d.Name);
                ValidateDefault(f.Default, d.Name + "." + f.Name);
            }
            names.Clear();
            foreach (var constant in d.Constants!)
            {
                Require(constant is not null && ValidName(constant.Name, qualified: false), "Invalid enum member.", d.Name);
                if (!names.Add(constant!.Name))
                    throw Invalid($"Duplicate enum member '{constant.Name}'.", d.Name, DiagnosticIds.DuplicateEnumMember);
            }
            names.Clear();
            foreach (var method in d.Methods!)
            {
                Require(method is not null && ValidName(method.Name, qualified: false), "Invalid method name.", d.Name);
                if (!names.Add(method!.Name))
                    throw Invalid($"Duplicate method '{method.Name}'.", d.Name, DiagnosticIds.DuplicateMethod);
                Require(method.Kind is "function" or "event", "Invalid method kind.", d.Name);
            }
        }
        foreach (var d in declarations.Values)
        {
            if (d.BaseType is not null)
            {
                ValidateType(d.BaseType, d, document, declarations);
                Require(d.BaseType.Kind == (d.Kind == "service" ? "service" : "struct")
                    || document.AllowsUnresolvedTypes && d.BaseType.Kind == "unresolved", "Invalid inheritance type.", d.Name);
            }
            foreach (var f in d.Fields)
            {
                ValidateType(f.Type, d, document, declarations);
                Require(f.Type.Kind is not ("integer" or "void" or "stream" or "service"), "Invalid field type.", d.Name + "." + f.Name);
                ValidateFieldDefault(f, d.Name + "." + f.Name);
            }
            foreach (var m in d.Methods)
            {
                ValidateType(m.Input, d, document, declarations);
                ValidateType(m.Result, d, document, declarations);
            }
        }
        ValidateInheritance(declarations);
    }

    private static void ValidateType(TypeShape type, ContractDeclaration owner, SchemaContract document,
        Dictionary<string, ContractDeclaration> declarations, int depth = 0)
    {
        Require(type is not null && depth < 64 && type.Arguments is not null, "Invalid or excessively nested type expression.", owner.Name);
        var t = type!;
        var count = t.Arguments.Count;
        if (t.Kind is "struct" or "enum" or "service" or "unresolved")
        {
            Require(ValidName(t.Name) && t.Integer is null, "Invalid named type.", owner.Name);
            if (t.Kind == "unresolved")
                Require(document.AllowsUnresolvedTypes, $"Unresolved type '{t.Name}'.", owner.Name, DiagnosticIds.UnresolvedType);
            else if (declarations.TryGetValue(t.Name!, out var target))
            {
                Require(t.Kind == (target.Kind == "forward" ? "struct" : target.Kind), $"Type kind does not match '{t.Name}'.", owner.Name);
                Require(count == target.Constraints.Count, $"Type '{t.Name}' has the wrong number of generic arguments.", owner.Name);
                if (target.Kind == "forward" && !document.AllowsUnresolvedTypes)
                    throw Invalid($"No complete definition for '{t.Name}'; its layout cannot be established.", owner.Name, DiagnosticIds.IncompleteDefinition);
            }
            else
                Require(!document.IncludesImports || document.AllowsUnresolvedTypes,
                    $"Missing definition for type '{t.Name}'.", owner.Name, DiagnosticIds.UnresolvedType);
        }
        else if (t.Kind is "parameter" or "integer")
        {
            Require(t.Name is null && t.Integer is not null && count == 0, "Invalid generic argument.", owner.Name);
            if (t.Kind == "parameter") Require(t.Integer >= 0 && t.Integer < owner.Constraints.Count, "Invalid generic parameter position.", owner.Name);
        }
        else
        {
            var expected = t.Kind switch
            {
                "map" => 2,
                "list" or "vector" or "set" or "nullable" or "maybe" or "bonded" or "stream" => 1,
                "int8" or "int16" or "int32" or "int64" or "uint8" or "uint16" or "uint32" or "uint64"
                    or "float" or "double" or "string" or "wstring" or "bool" or "blob" or "meta_name" or "meta_full_name" or "void" => 0,
                _ => -1
            };
            Require(expected >= 0 && count == expected && t.Name is null && t.Integer is null, $"Invalid type '{t.Kind}'.", owner.Name);
        }
        foreach (var argument in t.Arguments) ValidateType(argument, owner, document, declarations, depth + 1);
        if (t.Kind is "struct" or "enum" or "service" && declarations.TryGetValue(t.Name!, out var definition))
            for (var i = 0; i < definition.Constraints.Count; i++)
                if (definition.Constraints[i] == "value")
                    Require(IsValueType(t.Arguments[i], owner)
                            || document.AllowsUnresolvedTypes && t.Arguments[i].Kind == "unresolved",
                        $"Type argument {i} for '{t.Name}' does not satisfy its value constraint.", owner.Name);
        if (t.Kind is not ("struct" or "enum" or "service" or "unresolved"))
            Require(t.Arguments.All(a => a.Kind is not ("integer" or "void" or "service" or "stream")),
                "Invalid container element type.", owner.Name);
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
                if (current.BaseType?.Name is not { } name || !declarations.TryGetValue(name, out current!)) break;
            }
            complete.UnionWith(active);
        }
    }

    private static void ValidateFieldDefault(ContractField field, string location)
    {
        var type = field.Type;
        var value = field.Default;
        var expected = type.Kind switch
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
        Require(value.Kind == expected, $"Default kind '{value.Kind}' does not match type '{type.Kind}'.", location);
        if (value.Kind == "integer" && type.Kind != "unresolved")
        {
            var number = BigInteger.Parse(value.Value, CultureInfo.InvariantCulture);
            var width = type.Kind switch
            {
                "int8" or "uint8" => 8, "int16" or "uint16" => 16,
                "int32" or "uint32" or "enum" => 32, _ => 64
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

    internal static void Require([DoesNotReturnIf(false)] bool condition, string message, string location = "schema", string id = DiagnosticIds.InvalidSchema)
    {
        if (!condition) throw Invalid(message, location, id);
    }
}
