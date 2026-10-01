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

        /// <summary>How <see cref="MapType"/> treats custom type mappings.</summary>
        private enum TypeMode
        {
            /// <summary>Custom mappings apply.</summary>
            Clr,

            /// <summary>The Bond type itself, which custom-mapped values are converted from.</summary>
            Wire,

            /// <summary>The wire type as a schema annotation, keeping blob tags nested in converted values.</summary>
            Schema
        }

        private void InitializeMappings()
        {
            var usings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in options.UsingNamespaces)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new GenerationException("A using namespace cannot be empty.", default);
                }

                var parts = NamespaceParts(name.Trim());
                var qualifiedName = string.Join(".", parts.Select(part => Identifier(part, default)));
                if (usings.Add(qualifiedName))
                {
                    _usingNamespaces.Add(qualifiedName);
                }
            }

            foreach (var specification in options.NamespaceMappings)
            {
                var (source, target) = SplitMapping(specification, "namespace");
                var sourceParts = NamespaceParts(source);
                var targetParts = NamespaceParts(target);
                if (!_namespaceMappings.TryAdd(string.Join(".", sourceParts), targetParts))
                {
                    Fail($"Namespace '{source}' is mapped more than once.", default);
                }
            }

            foreach (var specification in options.TypeMappings)
            {
                var (source, template) = SplitMapping(specification, "type");
                NamespaceParts(source);
                ValidateTypeTemplate(template);
                if (!_typeMappings.TryAdd(source, template))
                {
                    Fail($"Alias '{source}' is mapped more than once.", default);
                }
            }
        }

        private static (string Source, string Target) SplitMapping(string specification, string kind)
        {
            if (specification == null)
            {
                throw new GenerationException($"A {kind} mapping cannot be null.", default);
            }

            var separator = specification.IndexOf('=');
            if (separator <= 0 || separator == specification.Length - 1)
            {
                throw new GenerationException($"Invalid {kind} mapping '{specification}'; expected source=target.", default);
            }

            var source = specification[..separator].Trim();
            var target = specification[(separator + 1)..].Trim();
            if (source.Length == 0 || target.Length == 0)
            {
                throw new GenerationException($"Invalid {kind} mapping '{specification}'; source and target are required.", default);
            }

            return (source, target);
        }

        private static string[] NamespaceParts(string name)
        {
            var parts = name.Split('.');
            foreach (var part in parts)
            {
                Identifier(part, default);
            }

            return parts;
        }

        private string[] MapNamespace(string[] original) =>
            _namespaceMappings.GetValueOrDefault(string.Join(".", original), original);

        private bool TryGetAliasTemplate(AliasDeclaration alias, [NotNullWhen(true)] out string? template)
        {
            if (_typeMappings.TryGetValue(IdlFullName(alias), out template))
            {
                return true;
            }

            var isLocalAlias = ast.Declarations.Any(root => ReferenceEquals(root, alias));
            if (!isLocalAlias)
            {
                return false;
            }

            return _typeMappings.TryGetValue(alias.Name, out template);
        }

        private bool TryMapAlias(AliasDeclaration alias, BondType[] arguments, SourceLocation location,
            [NotNullWhen(true)] out MappedType? mapped)
        {
            mapped = null;
            if (!TryGetAliasTemplate(alias, out var template))
            {
                return false;
            }

            var name = ExpandTemplate(template, index =>
            {
                if (index >= arguments.Length)
                {
                    throw new GenerationException(
                        $"Type mapping for '{IdlFullName(alias)}' references missing argument {{{index}}}.", location);
                }

                return arguments[index] is BondType.IntTypeArg number
                    ? number.Value.ToString(CultureInfo.InvariantCulture)
                    : MapType(arguments[index], location).Name;
            });
            var substituted = Substitute(alias.AliasedType, alias, arguments);
            var wire = MapType(substituted, location, TypeMode.Wire);

            // A mapping to the type's own CLR representation needs no converter.
            if (name.Replace("global::", "", StringComparison.Ordinal) == wire.Name.Replace("global::", "", StringComparison.Ordinal))
            {
                mapped = wire;
                return true;
            }

            var schema = MapType(substituted, location, TypeMode.Schema);
            mapped = new MappedType(name, schema.SchemaType, IsScalar: wire.IsScalar,
                IsSchemaValueType: schema.IsSchemaValueType, IsCustom: true);
            return true;
        }

        // Custom-mapped values are converted by a BondTypeAliasConverter class in their struct's C# namespace, as with gbc.
        private string ConverterName(StructDeclaration owner, SourceLocation location) =>
            "global::" + string.Join(".", CSharpNamespace(owner).Select(part => Identifier(part, location))) + ".BondTypeAliasConverter";

        private string CustomDefaultValue(Field field, MappedType mapped, StructDeclaration owner)
        {
            var wireType = MapType(field.Type, field.Location, TypeMode.Wire);
            var value = WireDefaultValue(field, wireType) ?? $"default({wireType.Name})";
            if (!HasRuntimeCompatibleMappedDefault(field))
            {
                Fail($"The Bond runtime cannot preserve the non-zero or non-empty default of custom-mapped field '{field.Name}'. " +
                    "Keep its wire CLR type, or use a zero, empty, or nothing default.", field.Location);
            }

            return $"{ConverterName(owner, field.Location)}.Convert({value}, default({mapped.Name}))";
        }

        private bool HasRuntimeCompatibleMappedDefault(Field field)
        {
            switch (field.DefaultValue)
            {
                case null or Default.Nothing:
                    return true;
                case Default.Bool value:
                    return !value.Value;
                case Default.Integer value:
                    return value.Value.IsZero;
                case Default.Float value:
                    return BitConverter.DoubleToInt64Bits(value.Value) == 0;
                case Default.String value:
                    return value.Value.Length == 0;
                case Default.Enum value:
                    if (UnwrapAlias(field.Type, field.Location) is BondType.TypeReference
                        { Declaration: EnumDeclaration declaration })
                    {
                        long nextValue = 0;
                        foreach (var member in declaration.Constants)
                        {
                            var enumValue = member.Value is long explicitValue ? unchecked((int)explicitValue) : nextValue;
                            if (member.Name == value.Identifier)
                            {
                                return enumValue == 0;
                            }

                            nextValue = enumValue + 1;
                        }
                    }

                    return false;
                default:
                    return false;
            }
        }

        // The C# compiler validates the type itself; this only rejects text that is not a type name.
        private static void ValidateTypeTemplate(string template)
        {
            var depth = 0;
            foreach (var character in ExpandTemplate(template, _ => "T"))
            {
                depth += character switch
                {
                    '<' or '[' => 1,
                    '>' or ']' => -1,
                    _ => 0
                };
                if (depth < 0 || !(IsIdentifierPart(character) || character is '.' or ':' or '<' or '>' or ',' or '[' or ']' or '?' or '@' or ' '))
                {
                    throw new GenerationException($"Invalid CLR type in type mapping '{template}'.", default);
                }
            }

            if (depth != 0)
            {
                throw new GenerationException($"Unbalanced brackets in type mapping '{template}'.", default);
            }
        }

        private static string ExpandTemplate(string template, Func<int, string> argument)
        {
            var result = new StringBuilder();
            for (var position = 0; position < template.Length; position++)
            {
                if (template[position] == '}')
                {
                    throw new GenerationException("Unmatched '}' in type mapping.", default);
                }

                if (template[position] != '{')
                {
                    result.Append(template[position]);
                    continue;
                }

                var end = template.IndexOf('}', position + 1);
                if (end < 0 || !int.TryParse(template.AsSpan(position + 1, end - position - 1),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    throw new GenerationException("Type mapping placeholders must be non-negative indexes such as {0}.", default);
                }

                result.Append(argument(index));
                position = end;
            }

            return result.ToString();
        }
    }
}
