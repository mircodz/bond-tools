using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Bond.Parser.Tests.Fuzzing;

/// <summary>
/// A random schema that Bond accepts: enums, aliases, and generic, derived and view structs whose fields nest every
/// Bond type, with random ordinals, modifiers and defaults. The same seed always gives the same schema.
/// </summary>
internal sealed class RandomSchema
{
    private const int MaxDepth = 3;

    private static readonly string[] Scalars =
        ["bool", "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "float", "double"];

    // Valid Bond field names that C# needs to escape.
    private static readonly string[] Keywords = ["class", "event", "object", "base", "params", "operator", "int", "this"];

    private static readonly string[] Texts =
    [
        "\"\"", "\"text\"", "\"q\\\"uote\"", "\"back\\\\slash\"", "\"tab\\tnew\\nline\"", "\"\\u00e9t\\u00e9\"",
        "\"naïve 漢字\"", "\"\\x41\\101\"", "\"\\U0001F600\""
    ];

    private readonly Random _random;
    private readonly List<EnumInfo> _enums = [];
    private readonly List<AliasInfo> _aliases = [];
    private readonly List<StructInfo> _structs = [];
    private readonly StringBuilder _text = new();

    // Set while generating a base type's arguments or a struct argument, which bonded<T> takes: upstream loses wstring
    // and nullable there, as Bond.Runtime/README.md says, so neither is generated, nor an alias that may hide them.
    private bool _plain;

    public RandomSchema(int seed)
    {
        _random = new Random(seed);
        Namespace = $"Fuzz.N{seed}";
        _text.Append($"namespace {Namespace}\n\n");

        for (var i = _random.Next(3); i > 0; i--)
        {
            AddEnum();
        }

        for (var i = _random.Next(3); i > 0; i--)
        {
            AddAlias();
        }

        var count = _random.Next(1, 6);
        for (var i = 0; i < count; i++)
        {
            AddStruct(last: i == count - 1);
        }
    }

    public string Namespace { get; }

    public string Text => _text.ToString();

    /// <summary>The structs that aren't generic, so can be serialized on their own.</summary>
    public IReadOnlyList<string> Roots => _structs.Where(s => s.Params.Length == 0).Select(s => s.Name).ToList();

    private enum Shape { Scalar, Enum, Text, Blob, Struct, Container, Nullable, Bonded, ValueParam, StructParam, OtherParam }

    // What a type parameter may be instantiated with: scalars and enums (`T : value`), structs, or strings and
    // containers. Each kind is only used where all its instantiations are valid: as a key, in bonded<T>, with nothing.
    private enum ParamKind { Value, Struct, Other }

    private sealed record TypeRef(string Text, Shape Shape, string? Scalar = null, EnumInfo? Enum = null)
    {
        public bool IsKey => Shape is Shape.Scalar or Shape.Enum or Shape.Text or Shape.ValueParam;
    }

    private sealed record Param(string Name, ParamKind Kind);

    private sealed record EnumInfo(string Name, string[] Constants);

    // A generic alias has one parameter, used as a key when KeyParam is set.
    private sealed record AliasInfo(string Name, TypeRef? Target, string? Template, bool KeyParam);

    private sealed record StructInfo(string Name, Param[] Params, List<string> FieldNames);

    private void AddEnum()
    {
        var name = $"E{_enums.Count}";
        var constants = Enumerable.Range(0, _random.Next(1, 5)).Select(i => $"{name}C{i}").ToArray();
        var values = new List<string>();
        foreach (var constant in constants)
        {
            values.Add(_random.Next(5) < 3
                ? constant
                : $"{constant} = {Pick([0, 1, -1, 7, 100, -100, 65535, int.MinValue, _random.Next(-1000, 1000)])}");
        }

        _enums.Add(new EnumInfo(name, constants));
        _text.Append($"enum {name} {{ {string.Join(", ", values)} }}\n\n");
    }

    private void AddAlias()
    {
        var name = $"A{_aliases.Count}";
        if (_random.Next(2) == 0)
        {
            var (template, keyParam) = Pick([("vector<{0}>", false), ("map<string, {0}>", false), ("set<{0}>", true)]);
            _aliases.Add(new AliasInfo(name, null, template, keyParam));
            _text.Append($"using {name}<T> = {string.Format(CultureInfo.InvariantCulture, template, "T")};\n\n");
        }
        else
        {
            var target = Type([], depth: 1);
            _aliases.Add(new AliasInfo(name, target with { Text = name }, null, false));
            _text.Append($"using {name} = {target.Text};\n\n");
        }
    }

    private void AddStruct(bool last)
    {
        var name = $"S{_structs.Count}";
        var plain = _structs.Where(s => s.Params.Length == 0 && s.FieldNames.Count > 0).ToList();
        if (!last && plain.Count > 0 && _random.Next(10) == 0)
        {
            var target = Pick(plain);
            var fields = target.FieldNames.Where(_ => _random.Next(2) == 0).DefaultIfEmpty(target.FieldNames[0]).ToList();
            _structs.Add(new StructInfo(name, [], fields));
            _text.Append($"struct {name} view_of {target.Name} {{ {string.Join("; ", fields)} }}\n\n");
            return;
        }

        var kinds = new List<ParamKind> { ParamKind.Value, ParamKind.Other };
        if (_structs.Any(s => s.Params.Length == 0))
        {
            kinds.Add(ParamKind.Struct);
        }

        var parameters = last || _random.Next(4) != 0
            ? []
            : Enumerable.Range(0, _random.Next(1, 3)).Select(i => new Param($"T{i}", Pick(kinds))).ToArray();
        var info = new StructInfo(name, parameters, []);

        var header = new StringBuilder($"struct {name}");
        if (parameters.Length > 0)
        {
            header.Append('<')
                .AppendJoin(", ", parameters.Select(p => p.Kind == ParamKind.Value ? $"{p.Name} : value" : p.Name))
                .Append('>');
        }

        if (_structs.Count > 0 && _random.Next(5) == 0)
        {
            header.Append(" : ").Append(Plain(() => Instantiate(Pick(_structs), parameters, depth: 1)).Text);
        }

        var lines = new List<string>();
        var ordinals = new HashSet<int>();
        var names = new HashSet<string>();
        for (var i = _random.Next(9); i > 0; i--)
        {
            var ordinal = Ordinal(ordinals);
            var fieldName = _random.Next(20) == 0 ? Pick(Keywords) : $"f{ordinal}";
            if (!names.Add(fieldName))
            {
                fieldName = $"f{ordinal}";
                names.Add(fieldName);
            }

            if (_random.Next(30) == 0)
            {
                lines.Add($"    {ordinal}: {Pick(["bond_meta::name", "bond_meta::full_name"])} {fieldName};");
            }
            else
            {
                var type = Type(parameters, depth: 0);
                var modifier = Pick(["", "", "", "", "", "", "optional ", "required ", "required_optional "]);
                var defaultValue = Default(type, modifier.Trim());
                lines.Add($"    {ordinal}: {modifier}{type.Text} {fieldName}{(defaultValue == null ? "" : " = " + defaultValue)};");
            }

            info.FieldNames.Add(fieldName);
        }

        _structs.Add(info);
        _text.Append(header).Append(" {\n");
        foreach (var line in lines.OrderBy(_ => _random.Next()))
        {
            _text.Append(line).Append('\n');
        }

        _text.Append("}\n\n");
    }

    // Mostly ordinals that fit in a field header's three bits, some in one more byte, a few in two.
    private int Ordinal(HashSet<int> taken)
    {
        while (true)
        {
            var ordinal = _random.Next(10) switch
            {
                < 6 => _random.Next(16),
                < 9 => _random.Next(16, 300),
                _ => _random.Next(300, 65536)
            };
            if (taken.Add(ordinal))
            {
                return ordinal;
            }
        }
    }

    private TypeRef Type(Param[] scope, int depth)
    {
        var options = new List<(int Weight, Func<TypeRef> Make)>
        {
            (30, ScalarType),
            (10, TextType),
            (3, () => new TypeRef("blob", Shape.Blob))
        };
        if (_enums.Count > 0)
        {
            options.Add((8, EnumType));
        }

        if (_structs.Count > 0)
        {
            options.Add((12, () => Instantiate(Pick(_structs), scope, depth + 1)));
        }

        if (scope.Length > 0)
        {
            options.Add((12, () => ParamType(Pick(scope))));
        }

        if (_aliases.Count > 0 && !_plain)
        {
            options.Add((5, () => AliasType(scope, depth)));
        }

        if (depth < MaxDepth)
        {
            options.Add((6, () => Container("vector", Type(scope, depth + 1))));
            options.Add((4, () => Container("list", Type(scope, depth + 1))));
            options.Add((4, () => new TypeRef($"set<{Key(scope).Text}>", Shape.Container)));
            options.Add((6, () => new TypeRef($"map<{Key(scope).Text}, {Type(scope, depth + 1).Text}>", Shape.Container)));
            if (!_plain)
            {
                options.Add((6, () => Nullable(scope, depth)));
            }

            if (_structs.Count > 0 || scope.Any(p => p.Kind == ParamKind.Struct))
            {
                options.Add((3, () => new TypeRef($"bonded<{StructArgument(scope, depth + 1).Text}>", Shape.Bonded)));
            }
        }

        var pick = _random.Next(options.Sum(option => option.Weight));
        foreach (var (weight, make) in options)
        {
            if ((pick -= weight) < 0)
            {
                return make();
            }
        }

        throw new InvalidOperationException("unreachable");
    }

    private TypeRef ScalarType()
    {
        var scalar = Pick(Scalars);
        return new TypeRef(scalar, Shape.Scalar, scalar);
    }

    private TypeRef TextType() => new(_plain ? "string" : Pick(["string", "wstring"]), Shape.Text);

    private TypeRef EnumType()
    {
        var enumeration = Pick(_enums);
        return new TypeRef(enumeration.Name, Shape.Enum, Enum: enumeration);
    }

    private static TypeRef ParamType(Param param) => new(param.Name, param.Kind switch
    {
        ParamKind.Value => Shape.ValueParam,
        ParamKind.Struct => Shape.StructParam,
        _ => Shape.OtherParam
    });

    private static TypeRef Container(string kind, TypeRef element) => new($"{kind}<{element.Text}>", Shape.Container);

    private TypeRef Nullable(Param[] scope, int depth)
    {
        var element = Type(scope, depth + 1);
        return element.Shape is Shape.Nullable or Shape.Bonded
            ? Container("vector", element)
            : new TypeRef($"nullable<{element.Text}>", Shape.Nullable);
    }

    private TypeRef Key(Param[] scope)
    {
        var options = new List<Func<TypeRef>> { ScalarType, TextType };
        if (_enums.Count > 0)
        {
            options.Add(EnumType);
        }

        var valueParams = scope.Where(p => p.Kind == ParamKind.Value).ToList();
        if (valueParams.Count > 0)
        {
            options.Add(() => ParamType(Pick(valueParams)));
        }

        var keyAliases = _aliases.Where(alias => alias.Target is { IsKey: true } && !_plain).ToList();
        if (keyAliases.Count > 0)
        {
            options.Add(() => Pick(keyAliases).Target!);
        }

        return Pick(options)();
    }

    private TypeRef AliasType(Param[] scope, int depth)
    {
        var alias = Pick(_aliases);
        if (alias.Target != null)
        {
            return alias.Target;
        }

        var argument = alias.KeyParam ? Key(scope) : Type(scope, depth + 1);
        return new TypeRef($"{alias.Name}<{argument.Text}>", Shape.Container);
    }

    private TypeRef Instantiate(StructInfo structure, Param[] scope, int depth) =>
        structure.Params.Length == 0
            ? new TypeRef(structure.Name, Shape.Struct)
            : new TypeRef(
                $"{structure.Name}<{string.Join(", ", structure.Params.Select(p => Argument(p.Kind, scope, depth).Text))}>",
                Shape.Struct);

    private TypeRef Argument(ParamKind kind, Param[] scope, int depth)
    {
        var own = scope.Where(p => p.Kind == kind).ToList();
        if (own.Count > 0 && _random.Next(4) == 0)
        {
            return ParamType(Pick(own));
        }

        return kind switch
        {
            ParamKind.Value => _enums.Count > 0 && _random.Next(4) == 0 ? EnumType() : ScalarType(),
            ParamKind.Struct => StructArgument([], depth),
            _ => _random.Next(3) == 0
                ? TextType()
                : Container(Pick(["vector", "list"]), depth < MaxDepth ? Type([], depth + 1) : ScalarType())
        };
    }

    // A struct for bonded<T> or for a struct type parameter: a type parameter in scope, or a struct defined so far.
    private TypeRef StructArgument(Param[] scope, int depth)
    {
        var own = scope.Where(p => p.Kind == ParamKind.Struct).ToList();
        if (own.Count > 0 && (_structs.Count == 0 || _random.Next(3) == 0))
        {
            return ParamType(Pick(own));
        }

        var candidates = _structs.Where(s => s.Params.Length == 0 || depth < MaxDepth).ToList();
        return Plain(() => Instantiate(Pick(candidates.Count > 0 ? candidates : _structs), [], depth + 1));
    }

    private TypeRef Plain(Func<TypeRef> make)
    {
        var outer = _plain;
        _plain = true;
        try
        {
            return make();
        }
        finally
        {
            _plain = outer;
        }
    }

    // Enum fields need a default unless required; containers, nullables and type parameters only take nothing; structs,
    // blobs and bonded values none. Nothing has no serialized form, so fields that are always written never default to it.
    private string? Default(TypeRef type, string modifier)
    {
        var required = modifier == "required";
        var written = required || modifier == "required_optional";
        var roll = _random.Next(20);
        switch (type.Shape)
        {
            case Shape.Scalar:
                return roll < 7 ? null : roll < 18 ? ScalarDefault(type.Scalar!) : written ? null : "nothing";
            case Shape.Enum:
                return required && roll < 10 ? null : roll < 17 || written ? Pick(type.Enum!.Constants) : "nothing";
            case Shape.Text:
                return roll < 8 ? null
                    : roll < 17 ? (type.Text == "wstring" && roll % 2 == 0 ? "L" : "") + Pick(Texts)
                    : written ? null : "nothing";
            case Shape.Container or Shape.Nullable or Shape.ValueParam or Shape.OtherParam:
                return roll < 16 || written ? null : "nothing";
            default:
                return null;
        }
    }

    private string ScalarDefault(string scalar)
    {
        if (scalar == "bool")
        {
            return Pick(["true", "false"]);
        }

        if (scalar is "float" or "double")
        {
            return Pick(["0.0", "-0.0", "1.5", "-2.25", "3", "-7", "1e3", "2.5e-3", "0.1"]);
        }

        var signed = scalar.StartsWith("int", StringComparison.Ordinal);
        var bits = int.Parse(scalar.AsSpan(signed ? 3 : 4), CultureInfo.InvariantCulture);
        var max = signed ? (1UL << (bits - 1)) - 1 : bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        return _random.Next(6) switch
        {
            0 => "0",
            1 => "1",
            2 => max.ToString(CultureInfo.InvariantCulture),
            3 => signed
                ? "-" + (max + 1).ToString(CultureInfo.InvariantCulture)
                : "0x" + max.ToString("X", CultureInfo.InvariantCulture),
            4 => signed ? "-1" : "127",
            _ => _random.Next(100).ToString(CultureInfo.InvariantCulture)
        };
    }

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];
}
