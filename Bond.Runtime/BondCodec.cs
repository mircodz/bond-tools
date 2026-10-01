using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BondTools.Runtime;

/// <summary>
/// Writes and reads values of one Bond type. Generated generic structs take a codec for each type parameter,
/// because the CLR type alone does not determine the Bond type (for example, <c>string</c> and <c>wstring</c>).
/// </summary>
public abstract class BondCodec<T>
{
    public abstract WireType WireType { get; }

    public abstract void Write(ref CompactBinaryWriter writer, T value);

    public abstract T Read(ref CompactBinaryReader reader, WireType type);

    /// <summary>Whether an optional field holding the value is omitted.</summary>
    public abstract bool IsDefault(T value);
}

/// <summary>Codecs for every Bond type.</summary>
public static class BondCodec
{
    public static BondCodec<bool> Bool { get; } = new BoolCodec();

    public static BondCodec<sbyte> Int8 { get; } = new Int8Codec();

    public static BondCodec<short> Int16 { get; } = new Int16Codec();

    public static BondCodec<int> Int32 { get; } = new Int32Codec();

    public static BondCodec<long> Int64 { get; } = new Int64Codec();

    public static BondCodec<byte> UInt8 { get; } = new UInt8Codec();

    public static BondCodec<ushort> UInt16 { get; } = new UInt16Codec();

    public static BondCodec<uint> UInt32 { get; } = new UInt32Codec();

    public static BondCodec<ulong> UInt64 { get; } = new UInt64Codec();

    public static BondCodec<float> Float { get; } = new FloatCodec();

    public static BondCodec<double> Double { get; } = new DoubleCodec();

    public static BondCodec<string> String { get; } = new StringCodec();

    public static BondCodec<string> WString { get; } = new WStringCodec();

    public static BondCodec<ArraySegment<byte>> Blob { get; } = new BlobCodec();

    /// <summary>The codec of a non-generic generated struct.</summary>
    public static BondCodec<T> Struct<T>() where T : IBondStruct<T> => StructCodec<T>.Instance;

    public static BondCodec<TEnum> Enum<TEnum>() where TEnum : struct, Enum => EnumCodec<TEnum>.Instance;

    public static BondCodec<List<T>> Vector<T>(BondCodec<T> element) => new VectorCodec<T>(element);

    public static BondCodec<LinkedList<T>> List<T>(BondCodec<T> element) => new ListCodec<T>(element);

    public static BondCodec<HashSet<T>> Set<T>(BondCodec<T> element) => new SetCodec<T>(element);

    public static BondCodec<Dictionary<TKey, TValue>> Map<TKey, TValue>(BondCodec<TKey> key, BondCodec<TValue> value)
        where TKey : notnull => new MapCodec<TKey, TValue>(key, value);

    /// <summary>A <c>bonded&lt;T&gt;</c> value, read as its payload: see <see cref="BondedPayload{T}"/>.</summary>
    public static BondCodec<global::Bond.IBonded<T>> Bonded<T>(BondCodec<T> element) => new BondedCodec<T>(element);

    /// <summary>A custom-mapped type, converted to and from the Bond type of <paramref name="wire"/>.</summary>
    public static BondCodec<T> Converted<T, TWire>(BondCodec<TWire> wire, Func<T, TWire> toWire, Func<TWire, T> fromWire) =>
        new ConvertedCodec<T, TWire>(wire, toWire, fromWire);

    /// <summary>A nullable scalar, written as a list of zero or one elements.</summary>
    public static BondCodec<T?> Nullable<T>(BondCodec<T> element) where T : struct => new NullableCodec<T>(element);

    /// <summary>
    /// A nullable struct, container, string, or unconstrained type parameter, written as a list of zero or one elements.
    /// </summary>
    public static BondCodec<T?> NullableReference<T>(BondCodec<T> element) => new NullableReferenceCodec<T>(element);

    private sealed class BoolCodec : BondCodec<bool>
    {
        public override WireType WireType => WireType.Bool;
        public override void Write(ref CompactBinaryWriter writer, bool value) => writer.WriteBool(value);
        public override bool Read(ref CompactBinaryReader reader, WireType type) => reader.ReadBool(type);
        public override bool IsDefault(bool value) => !value;
    }

    private sealed class Int8Codec : BondCodec<sbyte>
    {
        public override WireType WireType => WireType.Int8;
        public override void Write(ref CompactBinaryWriter writer, sbyte value) => writer.WriteInt8(value);
        public override sbyte Read(ref CompactBinaryReader reader, WireType type) => reader.ReadInt8(type);
        public override bool IsDefault(sbyte value) => value == 0;
    }

    private sealed class Int16Codec : BondCodec<short>
    {
        public override WireType WireType => WireType.Int16;
        public override void Write(ref CompactBinaryWriter writer, short value) => writer.WriteInt16(value);
        public override short Read(ref CompactBinaryReader reader, WireType type) => reader.ReadInt16(type);
        public override bool IsDefault(short value) => value == 0;
    }

    private sealed class Int32Codec : BondCodec<int>
    {
        public override WireType WireType => WireType.Int32;
        public override void Write(ref CompactBinaryWriter writer, int value) => writer.WriteInt32(value);
        public override int Read(ref CompactBinaryReader reader, WireType type) => reader.ReadInt32(type);
        public override bool IsDefault(int value) => value == 0;
    }

    private sealed class Int64Codec : BondCodec<long>
    {
        public override WireType WireType => WireType.Int64;
        public override void Write(ref CompactBinaryWriter writer, long value) => writer.WriteInt64(value);
        public override long Read(ref CompactBinaryReader reader, WireType type) => reader.ReadInt64(type);
        public override bool IsDefault(long value) => value == 0;
    }

    private sealed class UInt8Codec : BondCodec<byte>
    {
        public override WireType WireType => WireType.UInt8;
        public override void Write(ref CompactBinaryWriter writer, byte value) => writer.WriteUInt8(value);
        public override byte Read(ref CompactBinaryReader reader, WireType type) => reader.ReadUInt8(type);
        public override bool IsDefault(byte value) => value == 0;
    }

    private sealed class UInt16Codec : BondCodec<ushort>
    {
        public override WireType WireType => WireType.UInt16;
        public override void Write(ref CompactBinaryWriter writer, ushort value) => writer.WriteUInt16(value);
        public override ushort Read(ref CompactBinaryReader reader, WireType type) => reader.ReadUInt16(type);
        public override bool IsDefault(ushort value) => value == 0;
    }

    private sealed class UInt32Codec : BondCodec<uint>
    {
        public override WireType WireType => WireType.UInt32;
        public override void Write(ref CompactBinaryWriter writer, uint value) => writer.WriteUInt32(value);
        public override uint Read(ref CompactBinaryReader reader, WireType type) => reader.ReadUInt32(type);
        public override bool IsDefault(uint value) => value == 0;
    }

    private sealed class UInt64Codec : BondCodec<ulong>
    {
        public override WireType WireType => WireType.UInt64;
        public override void Write(ref CompactBinaryWriter writer, ulong value) => writer.WriteUInt64(value);
        public override ulong Read(ref CompactBinaryReader reader, WireType type) => reader.ReadUInt64(type);
        public override bool IsDefault(ulong value) => value == 0;
    }

    private sealed class FloatCodec : BondCodec<float>
    {
        public override WireType WireType => WireType.Float;
        public override void Write(ref CompactBinaryWriter writer, float value) => writer.WriteFloat(value);
        public override float Read(ref CompactBinaryReader reader, WireType type) => reader.ReadFloat(type);
        public override bool IsDefault(float value) => value == 0;
    }

    private sealed class DoubleCodec : BondCodec<double>
    {
        public override WireType WireType => WireType.Double;
        public override void Write(ref CompactBinaryWriter writer, double value) => writer.WriteDouble(value);
        public override double Read(ref CompactBinaryReader reader, WireType type) => reader.ReadDouble(type);
        public override bool IsDefault(double value) => value == 0;
    }

    private sealed class StringCodec : BondCodec<string>
    {
        public override WireType WireType => WireType.String;
        public override void Write(ref CompactBinaryWriter writer, string value) => writer.WriteString(value);
        public override string Read(ref CompactBinaryReader reader, WireType type) => reader.ReadString(type);
        public override bool IsDefault(string value) => value.Length == 0;
    }

    private sealed class WStringCodec : BondCodec<string>
    {
        public override WireType WireType => WireType.WString;
        public override void Write(ref CompactBinaryWriter writer, string value) => writer.WriteWString(value);
        public override string Read(ref CompactBinaryReader reader, WireType type) => reader.ReadWString(type);
        public override bool IsDefault(string value) => value.Length == 0;
    }

    private sealed class BlobCodec : BondCodec<ArraySegment<byte>>
    {
        public override WireType WireType => WireType.List;

        public override void Write(ref CompactBinaryWriter writer, ArraySegment<byte> value)
        {
            writer.WriteContainerBegin(value.Count, WireType.Int8);
            writer.WriteBytes(value);
        }

        public override ArraySegment<byte> Read(ref CompactBinaryReader reader, WireType type) => reader.ReadBlob(type);
        public override bool IsDefault(ArraySegment<byte> value) => value == default;
    }

    private sealed class StructCodec<T> : BondCodec<T> where T : IBondStruct<T>
    {
        internal static readonly StructCodec<T> Instance = new();

        public override WireType WireType => WireType.Struct;
        public override void Write(ref CompactBinaryWriter writer, T value) => T.Write(ref writer, value);

        public override T Read(ref CompactBinaryReader reader, WireType type)
        {
            CompactBinaryReader.Expect(type, WireType.Struct);
            return T.Read(ref reader);
        }

        public override bool IsDefault(T value) => false;
    }

    // Bond enums are 32-bit, as are the enums generated for them.
    private sealed class EnumCodec<TEnum> : BondCodec<TEnum> where TEnum : struct, Enum
    {
        internal static readonly EnumCodec<TEnum> Instance = new();

        public override WireType WireType => WireType.Int32;
        public override void Write(ref CompactBinaryWriter writer, TEnum value) => writer.WriteInt32(Unsafe.As<TEnum, int>(ref value));

        public override TEnum Read(ref CompactBinaryReader reader, WireType type)
        {
            var value = reader.ReadInt32(type);
            return Unsafe.As<int, TEnum>(ref value);
        }

        public override bool IsDefault(TEnum value) => Unsafe.As<TEnum, int>(ref value) == 0;
    }

    private sealed class VectorCodec<T>(BondCodec<T> element) : BondCodec<List<T>>
    {
        public override WireType WireType => WireType.List;

        public override void Write(ref CompactBinaryWriter writer, List<T> value)
        {
            writer.WriteContainerBegin(value.Count, element.WireType);
            foreach (var item in value)
            {
                element.Write(ref writer, item);
            }
        }

        public override List<T> Read(ref CompactBinaryReader reader, WireType type)
        {
            var count = reader.ReadContainerBegin(type, WireType.List, out var elementType);
            CompactBinaryReader.ExpectCompatible(elementType, element.WireType);
            var items = new List<T>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(element.Read(ref reader, elementType));
            }

            return items;
        }

        public override bool IsDefault(List<T> value) => value.Count == 0;
    }

    private sealed class ListCodec<T>(BondCodec<T> element) : BondCodec<LinkedList<T>>
    {
        public override WireType WireType => WireType.List;

        public override void Write(ref CompactBinaryWriter writer, LinkedList<T> value)
        {
            writer.WriteContainerBegin(value.Count, element.WireType);
            foreach (var item in value)
            {
                element.Write(ref writer, item);
            }
        }

        public override LinkedList<T> Read(ref CompactBinaryReader reader, WireType type)
        {
            var count = reader.ReadContainerBegin(type, WireType.List, out var elementType);
            CompactBinaryReader.ExpectCompatible(elementType, element.WireType);
            var items = new LinkedList<T>();
            for (var i = 0; i < count; i++)
            {
                items.AddLast(element.Read(ref reader, elementType));
            }

            return items;
        }

        public override bool IsDefault(LinkedList<T> value) => value.Count == 0;
    }

    private sealed class SetCodec<T>(BondCodec<T> element) : BondCodec<HashSet<T>>
    {
        public override WireType WireType => WireType.Set;

        public override void Write(ref CompactBinaryWriter writer, HashSet<T> value)
        {
            writer.WriteContainerBegin(value.Count, element.WireType);
            foreach (var item in value)
            {
                element.Write(ref writer, item);
            }
        }

        public override HashSet<T> Read(ref CompactBinaryReader reader, WireType type)
        {
            var count = reader.ReadContainerBegin(type, WireType.Set, out var elementType);
            CompactBinaryReader.ExpectCompatible(elementType, element.WireType);
            var items = new HashSet<T>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(element.Read(ref reader, elementType));
            }

            return items;
        }

        public override bool IsDefault(HashSet<T> value) => value.Count == 0;
    }

    private sealed class MapCodec<TKey, TValue>(BondCodec<TKey> key, BondCodec<TValue> value)
        : BondCodec<Dictionary<TKey, TValue>> where TKey : notnull
    {
        public override WireType WireType => WireType.Map;

        public override void Write(ref CompactBinaryWriter writer, Dictionary<TKey, TValue> map)
        {
            writer.WriteMapBegin(map.Count, key.WireType, value.WireType);
            foreach (var entry in map)
            {
                key.Write(ref writer, entry.Key);
                value.Write(ref writer, entry.Value);
            }
        }

        public override Dictionary<TKey, TValue> Read(ref CompactBinaryReader reader, WireType type)
        {
            var count = reader.ReadMapBegin(type, out var keyType, out var valueType);
            CompactBinaryReader.ExpectCompatible(keyType, key.WireType);
            CompactBinaryReader.ExpectCompatible(valueType, value.WireType);
            var map = new Dictionary<TKey, TValue>(count);
            for (var i = 0; i < count; i++)
            {
                var entryKey = key.Read(ref reader, keyType);
                map[entryKey] = value.Read(ref reader, valueType);
            }

            return map;
        }

        public override bool IsDefault(Dictionary<TKey, TValue> map) => map.Count == 0;
    }

    private sealed class NullableCodec<T>(BondCodec<T> element) : BondCodec<T?> where T : struct
    {
        public override WireType WireType => WireType.List;

        public override void Write(ref CompactBinaryWriter writer, T? value)
        {
            writer.WriteContainerBegin(value.HasValue ? 1 : 0, element.WireType);
            if (value.HasValue)
            {
                element.Write(ref writer, value.Value);
            }
        }

        public override T? Read(ref CompactBinaryReader reader, WireType type)
        {
            var count = reader.ReadContainerBegin(type, WireType.List, out var elementType);
            CompactBinaryReader.ExpectCompatible(elementType, element.WireType);
            T? result = null;
            for (var i = 0; i < count; i++)
            {
                result = element.Read(ref reader, elementType);
            }

            return result;
        }

        public override bool IsDefault(T? value) => !value.HasValue;
    }

    private sealed class NullableReferenceCodec<T>(BondCodec<T> element) : BondCodec<T?>
    {
        public override WireType WireType => WireType.List;

        public override void Write(ref CompactBinaryWriter writer, T? value)
        {
            writer.WriteContainerBegin(value is null ? 0 : 1, element.WireType);
            if (value is not null)
            {
                element.Write(ref writer, value);
            }
        }

        public override T? Read(ref CompactBinaryReader reader, WireType type)
        {
            var count = reader.ReadContainerBegin(type, WireType.List, out var elementType);
            CompactBinaryReader.ExpectCompatible(elementType, element.WireType);
            T? result = default;
            for (var i = 0; i < count; i++)
            {
                result = element.Read(ref reader, elementType);
            }

            return result;
        }

        public override bool IsDefault(T? value) => value is null;
    }

    private sealed class BondedCodec<T>(BondCodec<T> element) : BondCodec<global::Bond.IBonded<T>>
    {
        public override WireType WireType => WireType.Struct;

        public override void Write(ref CompactBinaryWriter writer, global::Bond.IBonded<T> value) =>
            BondedPayload<T>.Write(ref writer, value);

        public override global::Bond.IBonded<T> Read(ref CompactBinaryReader reader, WireType type) =>
            BondedPayload<T>.Read(ref reader, type, element);

        public override bool IsDefault(global::Bond.IBonded<T> value) => false;
    }

    private sealed class ConvertedCodec<T, TWire>(BondCodec<TWire> wire, Func<T, TWire> toWire, Func<TWire, T> fromWire)
        : BondCodec<T>
    {
        public override WireType WireType => wire.WireType;
        public override void Write(ref CompactBinaryWriter writer, T value) => wire.Write(ref writer, toWire(value));
        public override T Read(ref CompactBinaryReader reader, WireType type) => fromWire(wire.Read(ref reader, type));

        public override bool IsDefault(T value) => wire.WireType is WireType.List or WireType.Set or WireType.Map or WireType.Struct
            ? wire.IsDefault(toWire(value))
            : EqualityComparer<T>.Default.Equals(value, default!);
    }
}
