using System;
using System.Linq;
using Bond.Parser.Syntax;

namespace Bond.Parser.CodeGeneration;

public static partial class CSharpGenerator
{
    private sealed partial class Emitter
    {
        private const string Runtime = "global::BondTools.Runtime.";

        private static readonly string[] SerializationMemberNames =
            ["Serialize", "Deserialize", "Write", "Read", "WriteFields", "ReadFields"];

        // A generic struct's methods take a codec per type parameter: ", BondCodec<T> codecT" and ", codecT".
        private string _codecParameters = "";
        private string _codecArguments = "";

        // The class whose Convert methods convert custom-mapped values to and from their Bond types.
        private string _converter = "";
        private StructDeclaration? _owner;

        // Compact Binary serialization with BondTools.Runtime. Fields are written in ordinal order, base fields first,
        // and optional fields equal to their defaults are omitted.
        private void EmitSerialization(StructDeclaration structure, string self)
        {
            var generic = structure.TypeParameters.Length != 0;
            foreach (var (name, location) in structure.Fields.Select(field => (field.Name, field.Location))
                .Append((structure.Name, structure.Location)))
            {
                if (SerializationMemberNames.Contains(name) || generic && name is "CreateCodec" or "StructCodec")
                {
                    Fail($"'{name}' conflicts with the generated {name} serialization member.", location);
                }
            }

            _owner = structure;
            _converter = ConverterName(structure, structure.Location);
            var parameters = structure.TypeParameters.Select(parameter => Identifier(parameter.Name, structure.Location, typeName: true))
                .ToArray();
            _codecParameters = string.Concat(parameters.Select(parameter => $", {Runtime}BondCodec<{parameter}> codec{parameter}"));
            _codecArguments = string.Concat(parameters.Select(parameter => $", codec{parameter}"));

            // A derived struct's methods hide its base's where their codec parameters are the same, as when neither is
            // generic, or when it passes its type parameters to its base in order.
            var baseType = structure.BaseType == null ? null : (BondType.TypeReference)UnwrapAlias(structure.BaseType, structure.Location);
            var baseName = baseType == null ? null : MapType(baseType, structure.Location).Name;
            var baseArguments = baseType == null ? "" : CodecArguments(baseType, structure.Location);
            var hides = baseType != null && baseType.TypeArguments.Select(argument => MapType(argument, structure.Location).Name)
                .SequenceEqual(parameters);

            var fields = structure.Fields.OrderBy(field => field.Ordinal).ToArray();
            EmitApi(self, hides, generic);
            if (generic)
            {
                EmitCodec(self, parameters, hides);
            }

            EmitWrite(self, baseName, baseArguments, fields);
            EmitRead(structure, self, baseName, baseArguments, hides, fields);
        }

        // The public API, each method a call into the runtime.
        private void EmitApi(string self, bool hides, bool generic)
        {
            var modifier = hides ? "new " : "";
            var codec = generic ? $"CreateCodec({_codecArguments.TrimStart(',', ' ')})" : $"{Runtime}BondCodec.Struct<{self}>()";
            Line(0);
            Line(2, $"public {modifier}byte[] Serialize({_codecParameters.TrimStart(',', ' ')}) =>");
            Line(3, $"{Runtime}CompactBinary.Serialize(this, {codec});");
            Line(0);
            Line(2, $"public {modifier}void Serialize(global::System.Buffers.IBufferWriter<byte> output{_codecParameters}) =>");
            Line(3, $"{Runtime}CompactBinary.Serialize(this, output, {codec});");
            Line(0);
            Line(2, $"public {modifier}void Serialize(global::System.IO.Stream output{_codecParameters}) =>");
            Line(3, $"{Runtime}CompactBinary.Serialize(this, output, {codec});");
            Line(0);
            Line(2, $"public static {modifier}{self} Deserialize(global::System.ReadOnlySpan<byte> data{_codecParameters}) =>");
            Line(3, $"{Runtime}CompactBinary.Deserialize(data, {codec});");
            Line(0);
            Line(2, $"public static {modifier}{self} Deserialize(global::System.Buffers.ReadOnlySequence<byte> data{_codecParameters}) =>");
            Line(3, $"{Runtime}CompactBinary.Deserialize(data, {codec});");
            Line(0);
            Line(2, $"public static {modifier}{self} Deserialize(global::System.IO.Stream input{_codecParameters}) =>");
            Line(3, $"{Runtime}CompactBinary.Deserialize(input, {codec});");

            // Overloads of the base's, which take the base type.
            Line(0);
            Line(2, $"public static {self} Deserialize(global::System.ReadOnlySpan<byte> data, {self} into{_codecParameters}) =>");
            Line(3, $"{Runtime}CompactBinary.Deserialize(data, into, {codec});");
            Line(0);
            Line(2, $"public static {self} Deserialize(global::System.Buffers.ReadOnlySequence<byte> data, {self} into{_codecParameters}) =>");
            Line(3, $"{Runtime}CompactBinary.Deserialize(data, into, {codec});");
            Line(0);
            Line(2, $"public static {self} Deserialize(global::System.IO.Stream input, {self} into{_codecParameters}) =>");
            Line(3, $"{Runtime}CompactBinary.Deserialize(input, into, {codec});");
        }

        // A generic struct's codec, for its uses as a type argument and for its API.
        private void EmitCodec(string self, string[] parameters, bool hides)
        {
            Line(0);
            Line(2, $"public static {(hides ? "new " : "")}{Runtime}BondCodec<{self}> CreateCodec({_codecParameters.TrimStart(',', ' ')}) =>");
            Line(3, $"new StructCodec({_codecArguments.TrimStart(',', ' ')});");
            Line(0);
            Line(2, $"private sealed class StructCodec : {Runtime}BondCodec<{self}>");
            Line(2, "{");
            foreach (var parameter in parameters)
            {
                Line(3, $"private readonly {Runtime}BondCodec<{parameter}> codec{parameter};");
            }

            Line(0);
            Line(3, $"public StructCodec({_codecParameters.TrimStart(',', ' ')})");
            Line(3, "{");
            foreach (var parameter in parameters)
            {
                Line(4, $"this.codec{parameter} = codec{parameter};");
            }

            Line(3, "}");
            Line(0);
            Line(3, $"public override {Runtime}WireType WireType => {Runtime}WireType.Struct;");
            Line(0);
            Line(3, $"public override void Write(ref {Runtime}CompactBinaryWriter writer, {self} value) =>");
            Line(4, $"{self}.Write(ref writer, value{_codecArguments});");
            Line(0);
            Line(3, $"public override {self} Read(ref {Runtime}CompactBinaryReader reader, {Runtime}WireType type)");
            Line(3, "{");
            Line(4, $"{Runtime}CompactBinaryReader.Expect(type, {Runtime}WireType.Struct);");
            Line(4, $"return {self}.Read(ref reader{_codecArguments});");
            Line(3, "}");
            Line(0);
            Line(3, $"public override {self} Read(ref {Runtime}CompactBinaryReader reader, {Runtime}WireType type, {self} into)");
            Line(3, "{");
            Line(4, $"{Runtime}CompactBinaryReader.Expect(type, {Runtime}WireType.Struct);");
            Line(4, $"return {self}.Read(ref reader, into{_codecArguments});");
            Line(3, "}");
            Line(0);
            Line(3, $"public override bool IsDefault({self} value) => false;");
            Line(2, "}");
        }

        private void EmitWrite(string self, string? baseName, string baseArguments, Field[] fields)
        {
            _modelVariables = 0;
            Line(0);
            Line(2, $"public static void Write(ref {Runtime}CompactBinaryWriter writer, {self} value{_codecParameters})");
            Line(2, "{");
            Line(3, $"WriteFields(ref writer, value{_codecArguments});");
            Line(3, "writer.WriteStructEnd();");
            Line(2, "}");
            Line(0);
            Line(2, $"protected static void WriteFields(ref {Runtime}CompactBinaryWriter writer, {self} value{_codecParameters})");
            Line(2, "{");
            if (baseName != null)
            {
                Line(3, $"{baseName}.WriteFields(ref writer, value{baseArguments});");
                Line(3, "writer.WriteBaseEnd();");
            }

            foreach (var field in fields)
            {
                var access = "value." + Identifier(field.Name, field.Location);
                var indent = 3;
                var condition = OmissionCondition(field, access);
                if (condition != null)
                {
                    Line(3, $"if ({condition})");
                    Line(3, "{");
                    indent = 4;
                }

                Line(indent, $"writer.WriteFieldBegin({Wire(field.Type, field.Location)}, {field.Ordinal});");
                EmitWriteValue(field.Type, access, field.Location, indent);
                if (condition != null)
                {
                    Line(3, "}");
                }
            }

            Line(2, "}");
        }

        // Null means the field is always written: required fields, structs, and metadata names.
        private string? OmissionCondition(Field field, string access)
        {
            var type = UnwrapAlias(field.Type, field.Location);
            if (field.Modifier != FieldModifier.Optional || IsMetaType(type)
                || type is BondType.Bonded or BondType.TypeReference { Declaration: StructDeclaration or ForwardDeclaration })
            {
                return null;
            }

            var mapped = MapType(field.Type, field.Location);
            if (MappedWireType(field.Type, field.Location) is { } wireType)
            {
                return type switch
                {
                    BondType.Blob => $"{Convert(access, wireType, field.Location)} != default(global::System.ArraySegment<byte>)",
                    BondType.Vector or BondType.List or BondType.Set or BondType.Map => $"{access}.Count != 0",
                    _ => $"{access} != {CustomDefaultValue(field, mapped, _owner!)}"
                };
            }

            var element = type is BondType.Maybe maybe ? UnwrapAlias(maybe.ElementType, field.Location) : type;
            return element switch
            {
                BondType.TypeParameter parameter when type is not BondType.Maybe => $"!{Codec(parameter)}.IsDefault({access})",
                BondType.Blob => $"{access} != default(global::System.ArraySegment<byte>)",
                _ when type is BondType.Nullable or BondType.Maybe => $"{access} != null",
                BondType.Vector or BondType.List or BondType.Set or BondType.Map => $"{access}.Count != 0",
                _ => $"{access} != {WireDefaultValue(field, mapped) ?? $"default({mapped.Name})"}"
            };
        }

        private void EmitWriteValue(BondType type, string value, SourceLocation location, int indent)
        {
            if (MappedWireType(type, location) is { } wireType)
            {
                var converted = Fresh("wire");
                Line(indent, $"{MapType(wireType, location).Name} {converted} = {Convert(value, wireType, location)};");
                EmitWriteValue(wireType, converted, location, indent);
                return;
            }

            switch (UnwrapAlias(type, location))
            {
                case BondType.Maybe maybe:
                    EmitWriteValue(maybe.ElementType, NonNull(maybe.ElementType, value, location), location, indent);
                    break;
                case BondType.Nullable nullable:
                    // A nullable value is a list of zero or one elements.
                    Line(indent, $"if ({value} == null)");
                    Line(indent, "{");
                    Line(indent + 1, $"writer.WriteContainerBegin(0, {Wire(nullable.ElementType, location)});");
                    Line(indent, "}");
                    Line(indent, "else");
                    Line(indent, "{");
                    Line(indent + 1, $"writer.WriteContainerBegin(1, {Wire(nullable.ElementType, location)});");
                    EmitWriteValue(nullable.ElementType, NonNull(nullable.ElementType, value, location), location, indent + 1);
                    Line(indent, "}");
                    break;
                case BondType.Vector or BondType.List or BondType.Set:
                    {
                        var element = ElementType(UnwrapAlias(type, location));
                        var item = Fresh("item");
                        Line(indent, $"writer.WriteContainerBegin({value}.Count, {Wire(element, location)});");
                        Line(indent, $"foreach ({MapType(element, location).Name} {item} in {value})");
                        Line(indent, "{");
                        EmitWriteValue(element, item, location, indent + 1);
                        Line(indent, "}");
                        break;
                    }
                case BondType.Map map:
                    {
                        var entry = Fresh("entry");
                        var key = MapType(map.KeyType, location).Name;
                        var item = MapType(map.ValueType, location).Name;
                        Line(indent, $"writer.WriteMapBegin({value}.Count, {Wire(map.KeyType, location)}, {Wire(map.ValueType, location)});");
                        Line(indent, $"foreach (global::System.Collections.Generic.KeyValuePair<{key}, {item}> {entry} in {value})");
                        Line(indent, "{");
                        EmitWriteValue(map.KeyType, entry + ".Key", location, indent + 1);
                        EmitWriteValue(map.ValueType, entry + ".Value", location, indent + 1);
                        Line(indent, "}");
                        break;
                    }
                case BondType.Blob:
                    Line(indent, $"writer.WriteContainerBegin({value}.Count, {Runtime}WireType.Int8);");
                    Line(indent, $"writer.WriteBytes({value});");
                    break;
                case BondType.TypeReference { Declaration: StructDeclaration or ForwardDeclaration } reference:
                    Line(indent, $"{MapType(reference, location).Name}.Write(ref writer, {value}{CodecArguments(reference, location)});");
                    break;
                case BondType.TypeParameter parameter:
                    Line(indent, $"{Codec(parameter)}.Write(ref writer, {value});");
                    break;
                case BondType.Bonded bonded:
                    Line(indent, $"{Runtime}BondedPayload<{MapType(bonded.StructType, location).Name}>.Write(ref writer, {value});");
                    break;
                case BondType.TypeReference { Declaration: EnumDeclaration }:
                    Line(indent, $"writer.WriteInt32((int){value});");
                    break;
                case var scalar:
                    Line(indent, $"writer.Write{ScalarName(scalar, location)}({value});");
                    break;
            }
        }

        private void EmitRead(StructDeclaration structure, string self, string? baseName, string baseArguments, bool hides,
            Field[] fields)
        {
            _modelVariables = 0;
            Line(0);
            Line(2, $"public static {(hides ? "new " : "")}{self} Read(ref {Runtime}CompactBinaryReader reader{_codecParameters}) =>");
            Line(3, $"Read(ref reader, new {self}(){_codecArguments});");
            Line(0);
            Line(2, $"public static {self} Read(ref {Runtime}CompactBinaryReader reader, {self} value{_codecParameters})");
            Line(2, "{");
            Line(3, "reader.ReadStructBegin();");
            Line(3, $"ReadFields(ref reader, value{_codecArguments});");
            Line(3, "reader.ReadStructEnd();");
            Line(3, "return value;");
            Line(2, "}");
            Line(0);
            Line(2, $"protected static void ReadFields(ref {Runtime}CompactBinaryReader reader, {self} value{_codecParameters})");
            Line(2, "{");
            if (baseName != null)
            {
                Line(3, $"{baseName}.ReadFields(ref reader, value{baseArguments});");
            }

            var required = fields.Where(field => field.Modifier == FieldModifier.Required).ToArray();
            foreach (var field in required)
            {
                Line(3, $"bool read{field.Ordinal} = false;");
            }

            Line(3, "int previous = -1;");
            Line(3, $"while (reader.ReadFieldBegin(out {Runtime}WireType type, out ushort id))");
            Line(3, "{");
            Line(4, "if (id <= previous)");
            Line(4, "{");
            Line(5, "reader.Skip(type);");
            Line(5, "continue;");
            Line(4, "}");
            Line(0);
            Line(4, "previous = id;");
            Line(4, "switch (id)");
            Line(4, "{");
            foreach (var field in fields)
            {
                var access = "value." + Identifier(field.Name, field.Location);
                Line(5, $"case {field.Ordinal}:");
                Line(5, "{");
                EmitReadValue(field.Type, "type", read => $"{access} = {read};", field.Location, 6, into: access);
                if (field.Modifier == FieldModifier.Required)
                {
                    Line(6, $"read{field.Ordinal} = true;");
                }

                Line(6, "break;");
                Line(5, "}");
            }

            Line(5, "default:");
            Line(5, "{");
            Line(6, "reader.Skip(type);");
            Line(6, "break;");
            Line(5, "}");
            Line(4, "}");
            Line(3, "}");
            foreach (var field in required)
            {
                Line(0);
                Line(3, $"if (!read{field.Ordinal})");
                Line(3, "{");
                Line(4, $"throw {Runtime}CompactBinaryReader.MissingRequiredField({Literal(structure.Name)}, {Literal(field.Name)});");
                Line(3, "}");
            }

            Line(2, "}");
        }

        // Emits statements that read a value with the given wire type and pass it to assign. A struct or container
        // field is read into the instance the constructor created (into), so a read allocates nothing it then
        // discards; that also means a payload read into a used instance appends to its containers.
        private void EmitReadValue(BondType type, string wire, Func<string, string> assign, SourceLocation location, int indent,
            string? into = null, bool wireChecked = false)
        {
            if (MappedWireType(type, location) is { } wireType)
            {
                var clr = MapType(type, location).Name;
                EmitReadValue(wireType, wire, read => assign($"{_converter}.Convert({read}, default({clr}))"), location, indent,
                    wireChecked: wireChecked);
                return;
            }

            var unwrapped = UnwrapAlias(type, location);
            switch (unwrapped)
            {
                case BondType.Maybe maybe:
                    EmitReadValue(maybe.ElementType, wire, assign, location, indent);
                    break;
                case BondType.Nullable nullable:
                    {
                        var result = Fresh("value");
                        Line(indent, $"{MapType(type, location).Name} {result} = default;");
                        EmitReadItems(wire, "List", nullable.ElementType, item => $"{result} = {item};", location, indent);
                        Line(indent, assign(result));
                        break;
                    }
                case BondType.Vector or BondType.List or BondType.Set:
                    {
                        var collection = MapType(type, location).Name;
                        var items = Fresh("items");
                        var element = ElementType(unwrapped);
                        var (wireKind, add) = unwrapped switch
                        {
                            BondType.Vector => ("List", "Add"),
                            BondType.List => ("List", "AddLast"),
                            _ => ("Set", "Add")
                        };

                        // LinkedList has no capacity; the others are sized from the element count.
                        var sized = unwrapped is not BondType.List;
                        EmitReadItems(wire, wireKind, element, item => $"{items}.{add}({item});", location, indent,
                            count => EmitCollection(collection, items, sized ? count : "", into, indent));
                        if (into == null)
                        {
                            Line(indent, assign(items));
                        }

                        break;
                    }
                case BondType.Map map:
                    {
                        var dictionary = MapType(type, location).Name;
                        var result = Fresh("map");
                        var count = Fresh("count");
                        var keyWire = Fresh("key");
                        var valueWire = Fresh("value");
                        var index = Fresh("i");
                        var key = Fresh("key");
                        Line(indent, $"int {count} = reader.ReadMapBegin({wire}, out {Runtime}WireType {keyWire}, out {Runtime}WireType {valueWire});");
                        EmitCollection(dictionary, result, count, into, indent);
                        EmitElementCheck(map.KeyType, keyWire, location, indent);
                        var valueChecked = EmitElementCheck(map.ValueType, valueWire, location, indent);
                        Line(indent, $"for (int {index} = 0; {index} < {count}; {index}++)");
                        Line(indent, "{");
                        EmitReadValue(map.KeyType, keyWire, read => $"{MapType(map.KeyType, location).Name} {key} = {read};", location, indent + 1);
                        EmitReadValue(map.ValueType, valueWire, read => $"{result}[{key}] = {read};", location, indent + 1,
                            wireChecked: valueChecked);
                        Line(indent, "}");
                        if (into == null)
                        {
                            Line(indent, assign(result));
                        }

                        break;
                    }
                case BondType.Blob:
                    Line(indent, assign($"reader.ReadBlob({wire})"));
                    break;
                case BondType.TypeReference { Declaration: StructDeclaration or ForwardDeclaration } reference:
                    {
                        var name = MapType(reference, location).Name;
                        var codecs = CodecArguments(reference, location);
                        if (!wireChecked)
                        {
                            Line(indent, $"{Runtime}CompactBinaryReader.Expect({wire}, {Runtime}WireType.Struct);");
                        }

                        Line(indent, into == null
                            ? assign($"{name}.Read(ref reader{codecs})")
                            : $"{name}.Read(ref reader, {into} ??= new {name}(){codecs});");
                        break;
                    }
                case BondType.TypeParameter parameter:
                    Line(indent, assign($"{Codec(parameter)}.Read(ref reader, {wire})"));
                    break;
                case BondType.Bonded bonded:
                    Line(indent, assign($"{Runtime}BondedPayload<{MapType(bonded.StructType, location).Name}>.Read(" +
                        $"ref reader, {wire}, {CodecFor(bonded.StructType, location)})"));
                    break;
                case BondType.TypeReference { Declaration: EnumDeclaration }:
                    Line(indent, assign($"({MapType(unwrapped, location).Name})reader.ReadInt32({wire})"));
                    break;
                case var scalar:
                    Line(indent, assign($"reader.Read{ScalarName(scalar, location)}({wire})"));
                    break;
            }
        }

        // Declares a collection variable: a new one sized by capacity, or the existing one at into, grown to fit.
        private void EmitCollection(string type, string variable, string capacity, string? into, int indent)
        {
            if (into == null)
            {
                Line(indent, $"{type} {variable} = new {type}({capacity});");
                return;
            }

            Line(indent, $"{type} {variable} = {into} ??= new {type}({capacity});");
            if (capacity.Length == 0)
            {
                return;
            }

            Line(indent, $"{variable}.EnsureCapacity({variable}.Count + {capacity});");
        }

        private void EmitReadItems(string wire, string wireKind, BondType element, Func<string, string> add,
            SourceLocation location, int indent, Action<string>? create = null)
        {
            var count = Fresh("count");
            var elementWire = Fresh("element");
            var index = Fresh("i");
            Line(indent, $"int {count} = reader.ReadContainerBegin({wire}, {Runtime}WireType.{wireKind}, out {Runtime}WireType {elementWire});");
            create?.Invoke(count);
            var elementChecked = EmitElementCheck(element, elementWire, location, indent);
            Line(indent, $"for (int {index} = 0; {index} < {count}; {index}++)");
            Line(indent, "{");
            EmitReadValue(element, elementWire, add, location, indent + 1, wireChecked: elementChecked);
            Line(indent, "}");
        }

        // Checks the wire type of a container's elements (or a map's keys or values) before reading any, even in an
        // empty container. Returns whether that check makes the per-element check redundant.
        private bool EmitElementCheck(BondType element, string elementWire, SourceLocation location, int indent)
        {
            Line(indent, $"{Runtime}CompactBinaryReader.ExpectCompatible({elementWire}, {Wire(element, location)});");
            return UnwrapAlias(element, location) is BondType.TypeReference { Declaration: StructDeclaration or ForwardDeclaration };
        }

        // The Bond type of a custom-mapped alias (the type its values are converted to), or null for any other type.
        private BondType? MappedWireType(BondType type, SourceLocation location)
        {
            while (type is BondType.TypeReference { Declaration: var declaration } reference
                && Canonical(declaration) is AliasDeclaration alias)
            {
                var substituted = Substitute(alias.AliasedType, alias, reference.TypeArguments);
                if (TryMapAlias(alias, reference.TypeArguments, location, out var mapped) && mapped.IsCustom)
                {
                    if (ContainsMappedAlias(substituted, location))
                    {
                        Fail($"Serialization of '{IdlFullName(alias)}' is not supported: its Bond type contains another custom-mapped type.",
                            location);
                    }

                    return substituted;
                }

                type = substituted;
            }

            return null;
        }

        private bool ContainsMappedAlias(BondType type, SourceLocation location) => MappedWireType(type, location) != null
            || UnwrapAlias(type, location) switch
            {
                BondType.Vector vector => ContainsMappedAlias(vector.ElementType, location),
                BondType.List list => ContainsMappedAlias(list.ElementType, location),
                BondType.Set set => ContainsMappedAlias(set.KeyType, location),
                BondType.Map map => ContainsMappedAlias(map.KeyType, location) || ContainsMappedAlias(map.ValueType, location),
                BondType.Nullable nullable => ContainsMappedAlias(nullable.ElementType, location),
                BondType.Maybe maybe => ContainsMappedAlias(maybe.ElementType, location),
                BondType.TypeReference reference => reference.TypeArguments.Any(argument => ContainsMappedAlias(argument, location)),
                _ => false
            };

        private string Convert(string value, BondType wireType, SourceLocation location) =>
            $"{_converter}.Convert({value}, default({MapType(wireType, location).Name}))";

        private static string Codec(BondType.TypeParameter parameter) => "codec" + parameter.Param.Name;

        // The codecs for a generic struct's type arguments, as arguments after the others: ", BondCodec.Int32".
        private string CodecArguments(BondType.TypeReference reference, SourceLocation location) =>
            string.Concat(reference.TypeArguments.Select(argument => ", " + CodecFor(argument, location)));

        // An expression for the codec of a type, for a generic struct's type argument.
        private string CodecFor(BondType type, SourceLocation location)
        {
            if (MappedWireType(type, location) is { } wireType)
            {
                var clr = MapType(type, location).Name;
                var bond = MapType(wireType, location).Name;
                return $"{Runtime}BondCodec.Converted<{clr}, {bond}>({CodecFor(wireType, location)}, " +
                    $"value => {_converter}.Convert(value, default({bond})), value => {_converter}.Convert(value, default({clr})))";
            }

            return UnwrapAlias(type, location) switch
            {
                BondType.TypeParameter parameter => Codec(parameter),
                BondType.TypeReference { Declaration: StructDeclaration or ForwardDeclaration, TypeArguments.Length: 0 } reference =>
                    $"{Runtime}BondCodec.Struct<{MapType(reference, location).Name}>()",
                BondType.TypeReference { Declaration: StructDeclaration or ForwardDeclaration } reference =>
                    $"{MapType(reference, location).Name}.CreateCodec({CodecArguments(reference, location)[2..]})",
                BondType.TypeReference { Declaration: EnumDeclaration } => $"{Runtime}BondCodec.Enum<{MapType(type, location).Name}>()",
                BondType.Vector vector => $"{Runtime}BondCodec.Vector({CodecFor(vector.ElementType, location)})",
                BondType.List list => $"{Runtime}BondCodec.List({CodecFor(list.ElementType, location)})",
                BondType.Set set => $"{Runtime}BondCodec.Set({CodecFor(set.KeyType, location)})",
                BondType.Map map => $"{Runtime}BondCodec.Map({CodecFor(map.KeyType, location)}, {CodecFor(map.ValueType, location)})",
                BondType.Nullable nullable => MapType(nullable.ElementType, location).IsScalar
                    ? $"{Runtime}BondCodec.Nullable({CodecFor(nullable.ElementType, location)})"
                    : $"{Runtime}BondCodec.NullableReference({CodecFor(nullable.ElementType, location)})",
                BondType.Blob => $"{Runtime}BondCodec.Blob",
                BondType.Bonded bonded => $"{Runtime}BondCodec.Bonded({CodecFor(bonded.StructType, location)})",
                var scalar => $"{Runtime}BondCodec.{ScalarName(scalar, location)}"
            };
        }

        // Nullable scalars are represented as Nullable<T>; other nullable values are references or blobs.
        private string NonNull(BondType element, string value, SourceLocation location) =>
            MapType(element, location).IsScalar ? value + ".Value" : value;

        private static BondType ElementType(BondType container) => container switch
        {
            BondType.Vector vector => vector.ElementType,
            BondType.List list => list.ElementType,
            BondType.Set set => set.KeyType,
            _ => throw new InvalidOperationException($"'{container}' is not a sequence.")
        };

        // An expression for a type's wire type: a constant, or a generic struct's codec's.
        private string Wire(BondType type, SourceLocation location) => UnwrapAlias(type, location) switch
        {
            BondType.Maybe maybe => Wire(maybe.ElementType, location),
            BondType.TypeParameter parameter => Codec(parameter) + ".WireType",
            var other => Runtime + "WireType." + WireName(other, location)
        };

        private static string WireName(BondType type, SourceLocation location) => type switch
        {
            BondType.Vector or BondType.List or BondType.Nullable or BondType.Blob => "List",
            BondType.Set => "Set",
            BondType.Bonded => "Struct",
            BondType.Map => "Map",
            BondType.TypeReference { Declaration: StructDeclaration or ForwardDeclaration } => "Struct",
            BondType.TypeReference { Declaration: EnumDeclaration } => "Int32",
            var scalar => ScalarName(scalar, location)
        };

        private static string ScalarName(BondType type, SourceLocation location) => type switch
        {
            BondType.Bool => "Bool",
            BondType.Int8 => "Int8",
            BondType.Int16 => "Int16",
            BondType.Int32 => "Int32",
            BondType.Int64 => "Int64",
            BondType.UInt8 => "UInt8",
            BondType.UInt16 => "UInt16",
            BondType.UInt32 => "UInt32",
            BondType.UInt64 => "UInt64",
            BondType.Float => "Float",
            BondType.Double => "Double",
            BondType.String or BondType.MetaName or BondType.MetaFullName => "String",
            BondType.WString => "WString",
            _ => throw new GenerationException($"Serialization of '{type}' is not supported yet.", location)
        };
    }
}
