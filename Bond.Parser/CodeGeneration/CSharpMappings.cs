using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using Bond.Parser.Syntax;

namespace Bond.Parser.CodeGeneration;

public static partial class CSharpGenerator
{
    private sealed partial class Emitter
    {
        private readonly List<string> _usingNamespaces = [];
        private readonly Dictionary<string, string[]> _namespaceMappings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _typeMappings = new(StringComparer.Ordinal);
        private int _suppressTypeMappings;

        private void InitializeMappings()
        {
            if ((options.ModelFeatures & ~CSharpModelFeatures.All) != 0)
                Fail("Unknown C# model feature selection.", default);
            var usings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in options.UsingNamespaces)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new GenerationException("A using namespace cannot be empty.", default);
                var parts = NamespaceParts(name.Trim());
                var qualifiedName = string.Join(".", parts.Select(part => Identifier(part, default)));
                if (usings.Add(qualifiedName))
                    _usingNamespaces.Add(qualifiedName);
            }
            foreach (var specification in options.NamespaceMappings)
            {
                var (source, target) = SplitMapping(specification, "namespace");
                var sourceParts = NamespaceParts(source);
                var targetParts = NamespaceParts(target);
                if (!_namespaceMappings.TryAdd(string.Join(".", sourceParts), targetParts))
                    Fail($"Namespace '{source}' is mapped more than once.", default);
            }
            foreach (var specification in options.TypeMappings)
            {
                var (source, template) = SplitMapping(specification, "type");
                NamespaceParts(source);
                ValidateTypeTemplate(template);
                if (!_typeMappings.TryAdd(source, template))
                    Fail($"Alias '{source}' is mapped more than once.", default);
            }
        }

        private static (string Source, string Target) SplitMapping(string specification, string kind)
        {
            if (specification == null)
                throw new GenerationException($"A {kind} mapping cannot be null.", default);
            var separator = specification.IndexOf('=');
            if (separator <= 0 || separator == specification.Length - 1)
                throw new GenerationException($"Invalid {kind} mapping '{specification}'; expected source=target.", default);
            var source = specification[..separator].Trim();
            var target = specification[(separator + 1)..].Trim();
            if (source.Length == 0 || target.Length == 0)
                throw new GenerationException($"Invalid {kind} mapping '{specification}'; source and target are required.", default);
            return (source, target);
        }

        private static string[] NamespaceParts(string name)
        {
            var parts = name.Split('.');
            foreach (var part in parts)
                Identifier(part, default);
            return parts;
        }

        private string[] MapNamespace(string[] original) =>
            _namespaceMappings.GetValueOrDefault(string.Join(".", original), original);

        private bool TryMapAlias(AliasDeclaration alias, BondType[] arguments, SourceLocation location,
            [NotNullWhen(true)] out MappedType? mapped)
        {
            mapped = null;
            if (_suppressTypeMappings != 0)
                return false;
            var identity = string.Join(".", IdlNamespace(alias).Append(alias.Name));
            if (!_typeMappings.TryGetValue(identity, out var template)
                && !(ast.Declarations.Any(root => ReferenceEquals(root, alias))
                    && _typeMappings.TryGetValue(alias.Name, out template)))
                return false;

            var typeParameters = new HashSet<string>(StringComparer.Ordinal);
            var expanded = ExpandTemplate(template, index =>
            {
                if (index >= arguments.Length)
                    throw new GenerationException($"Type mapping for '{identity}' references missing argument {{{index}}}.", location);
                var argument = arguments[index];
                CollectParameters(argument, typeParameters);
                return argument is BondType.IntTypeArg number
                    ? number.Value.ToString(CultureInfo.InvariantCulture)
                    : MapType(argument, location).Name;
            });
            var name = new ClrTypeParser(expanded, typeParameters, location).Parse();
            var underlying = WithoutTypeMappings(() => MapType(Substitute(alias.AliasedType, alias, arguments), location));
            if (name == underlying.Name)
            {
                mapped = underlying;
                return true;
            }
            mapped = new MappedType(name, underlying.SchemaType, IsScalar: underlying.IsScalar,
                IsValueType: KnownValueTypes.Contains(name), IsSchemaValueType: underlying.IsSchemaValueType,
                IsCustom: true);
            return true;
        }

        private T WithoutTypeMappings<T>(Func<T> action)
        {
            _suppressTypeMappings++;
            try
            {
                return action();
            }
            finally
            {
                _suppressTypeMappings--;
            }
        }

        private string CustomDefaultValue(Field field, MappedType mapped, StructDeclaration owner)
        {
            var wire = WithoutTypeMappings(() => MapType(field.Type, field.Location));
            var value = DefaultValue(field, wire) ?? $"default({wire.Name})";
            if (!HasRuntimeCompatibleMappedDefault(field))
                Fail($"The Bond runtime cannot preserve the non-zero or non-empty default of custom-mapped field '{field.Name}'. " +
                    "Keep its wire CLR type, or use a zero, empty, or nothing default.", field.Location);
            var converter = "global::" + string.Join(".", CSharpNamespace(owner)
                .Select(part => Identifier(part, field.Location))) + ".BondTypeAliasConverter";
            return $"{converter}.Convert({value}, default({mapped.Name}))";
        }

        private bool HasRuntimeCompatibleMappedDefault(Field field)
        {
            switch (field.DefaultValue)
            {
                case null or Default.Nothing: return true;
                case Default.Bool value: return !value.Value;
                case Default.Integer value: return value.Value.IsZero;
                case Default.Float value: return BitConverter.DoubleToInt64Bits(value.Value) == 0;
                case Default.String value: return value.Value.Length == 0;
                case Default.Enum value:
                    if (UnwrapAlias(field.Type, field.Location) is BondType.TypeReference
                        { Declaration: EnumDeclaration declaration })
                    {
                        long next = 0;
                        foreach (var member in declaration.Constants)
                        {
                            var number = member.Value is long explicitValue ? unchecked((int)explicitValue) : next;
                            if (member.Name == value.Identifier)
                                return number == 0;
                            next = number + 1;
                        }
                    }
                    return false;
                default: return false;
            }
        }

        private static void CollectParameters(BondType type, HashSet<string> names)
        {
            if (type is BondType.TypeParameter parameter)
                names.Add(parameter.Param.Name);
            foreach (var child in Children(type))
                CollectParameters(child, names);
        }

        private static void ValidateTypeTemplate(string template)
        {
            var parameters = new HashSet<string>(StringComparer.Ordinal);
            var expanded = ExpandTemplate(template, index =>
            {
                var name = "__BondTypeParameter" + index.ToString(CultureInfo.InvariantCulture);
                parameters.Add(name);
                return name;
            });
            new ClrTypeParser(expanded, parameters, default).Parse();
        }

        private static string ExpandTemplate(string template, Func<int, string> argument)
        {
            var result = new StringBuilder();
            for (var i = 0; i < template.Length; i++)
            {
                if (template[i] == '}')
                    throw new GenerationException("Unmatched '}' in type mapping.", default);
                if (template[i] != '{')
                {
                    result.Append(template[i]);
                    continue;
                }
                var end = template.IndexOf('}', i + 1);
                if (end < 0 || !int.TryParse(template.AsSpan(i + 1, end - i - 1),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    throw new GenerationException("Type mapping placeholders must be non-negative indexes such as {0}.", default);
                result.Append(argument(index));
                i = end;
            }
            return result.ToString();
        }

        private static readonly Dictionary<string, string> PrimitiveNames = new(StringComparer.Ordinal)
        {
            ["System.Boolean"] = "bool", ["System.Byte"] = "byte", ["System.SByte"] = "sbyte",
            ["System.Int16"] = "short", ["System.UInt16"] = "ushort", ["System.Int32"] = "int",
            ["System.UInt32"] = "uint", ["System.Int64"] = "long", ["System.UInt64"] = "ulong",
            ["System.Single"] = "float", ["System.Double"] = "double", ["System.Decimal"] = "decimal",
            ["System.Char"] = "char", ["System.String"] = "string", ["System.Object"] = "object"
        };

        private static readonly HashSet<string> KnownValueTypes = new(StringComparer.Ordinal)
        {
            "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
            "float", "double", "decimal", "char", "nint", "nuint",
            "global::System.DateTime", "global::System.DateTimeOffset", "global::System.DateOnly",
            "global::System.TimeOnly", "global::System.TimeSpan", "global::System.Guid",
            "global::System.IntPtr", "global::System.UIntPtr", "global::System.Numerics.BigInteger"
        };

        private static bool IsPrimitiveName(string name) =>
            PrimitiveNames.Values.Contains(name) || name is "nint" or "nuint";

        private sealed class ClrTypeParser(string text, HashSet<string> parameters, SourceLocation location)
        {
            private int _position;

            public string Parse()
            {
                var result = ReadType();
                Space();
                if (_position != text.Length)
                    Invalid();
                return result;
            }

            private string ReadType()
            {
                Space();
                var global = text.AsSpan(_position).StartsWith("global::", StringComparison.Ordinal);
                if (global)
                    _position += "global::".Length;
                var segments = new List<string> { Segment() };
                while (Take('.'))
                    segments.Add(Segment());
                var name = string.Join(".", segments);
                if (global && segments.Count == 1 && IsPrimitiveName(name))
                    Invalid();
                if (PrimitiveNames.TryGetValue(name, out var primitive))
                    name = primitive;
                else if (segments.Count > 1 || global)
                    name = "global::" + name;
                else if (!IsPrimitiveName(name) && !parameters.Contains(name.TrimStart('@'))
                    && !name.StartsWith("__BondTypeParameter", StringComparison.Ordinal))
                    throw new GenerationException($"Type mapping '{text}' must use fully qualified CLR type names.", location);

                while (Take('['))
                {
                    if (!Take(']'))
                        throw new GenerationException("Only single-dimensional arrays are supported in type mappings.", location);
                    name += "[]";
                }
                return name;
            }

            private string Segment()
            {
                Space();
                var escaped = Take('@');
                var start = _position;
                while (_position < text.Length && IsIdentifierPart(text[_position]))
                    _position++;
                if (_position == start)
                    Invalid();
                var name = text[start.._position];
                var identifier = Identifier(name, location, typeName: true);
                if (!escaped && IsPrimitiveName(name))
                    identifier = name;
                if (Take('<'))
                {
                    var arguments = new List<string> { ReadType() };
                    while (Take(','))
                        arguments.Add(ReadType());
                    if (!Take('>'))
                        Invalid();
                    identifier += "<" + string.Join(", ", arguments) + ">";
                }
                return identifier;
            }

            private bool Take(char character)
            {
                Space();
                if (_position >= text.Length || text[_position] != character)
                    return false;
                _position++;
                return true;
            }

            private void Space()
            {
                while (_position < text.Length && char.IsWhiteSpace(text[_position]))
                    _position++;
            }

            private void Invalid() =>
                throw new GenerationException($"Invalid CLR type syntax in mapping '{text}'.", location);
        }
    }
}
