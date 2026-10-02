using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace BondTools.Runtime;

/// <summary>Reads Bond Compact Binary (v1) from a span.</summary>
/// <remarks>
/// Values are read with the wire type found in the payload, which may be a narrower type of the same signedness
/// (or <see cref="WireType.Float"/> for doubles). Invalid or truncated payloads throw
/// <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/>.
/// </remarks>
public ref struct CompactBinaryReader
{
    private const int MaxDepth = 64;

    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private int _depth;
    private bool _atBaseEnd;

    public CompactBinaryReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _depth = 0;
        _atBaseEnd = false;
    }

    /// <summary>The number of bytes read so far.</summary>
    public readonly int Position => _position;

    public void ReadStructBegin() => Enter();

    /// <summary>Ends a struct, skipping the fields of derived structs if the payload was written by one.</summary>
    public void ReadStructEnd()
    {
        while (_atBaseEnd)
        {
            while (ReadFieldBegin(out var type, out _))
            {
                Skip(type);
            }
        }

        _depth--;
    }

    /// <summary>Reads a field header, returning false at the end of the fields of a struct or of its base.</summary>
    public bool ReadFieldBegin(out WireType type, out ushort id)
    {
        var header = ReadByte();
        type = (WireType)(header & 0x1F);

        id = (ushort)(header >> 5);
        if (id == 6)
        {
            id = ReadByte();
        }
        else if (id == 7)
        {
            id = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        }

        if (type <= WireType.StopBase)
        {
            _atBaseEnd = type == WireType.StopBase;
            return false;
        }

        return true;
    }

    /// <summary>Begins a list, set, blob, or nullable value, returning its element count.</summary>
    public int ReadContainerBegin(WireType type, WireType expected, out WireType elementType)
    {
        Expect(type, expected);

        elementType = (WireType)(ReadByte() & 0x1F);
        return ReadCount(elementBytes: 1);
    }

    public int ReadMapBegin(WireType type, out WireType keyType, out WireType valueType)
    {
        Expect(type, WireType.Map);
        keyType = (WireType)ReadByte();
        valueType = (WireType)ReadByte();
        return ReadCount(elementBytes: 2);
    }

    public bool ReadBool(WireType type)
    {
        Expect(type, WireType.Bool);
        return ReadByte() != 0;
    }

    public byte ReadUInt8(WireType type)
    {
        Expect(type, WireType.UInt8);
        return ReadByte();
    }

    public ushort ReadUInt16(WireType type) =>
        type == WireType.UInt16 ? ReadVarUInt16() : (ushort)ReadWidened(type, WireType.UInt16);

    public uint ReadUInt32(WireType type) =>
        type == WireType.UInt32 ? ReadVarUInt32() : (uint)ReadWidened(type, WireType.UInt32);

    public ulong ReadUInt64(WireType type) =>
        type == WireType.UInt64 ? ReadVarUInt64() : (ulong)ReadWidened(type, WireType.UInt64);

    public sbyte ReadInt8(WireType type)
    {
        Expect(type, WireType.Int8);
        return (sbyte)ReadByte();
    }

    public short ReadInt16(WireType type) =>
        type == WireType.Int16 ? DecodeZigZag(ReadVarUInt16()) : (short)ReadWidened(type, WireType.Int16);

    public int ReadInt32(WireType type) =>
        type == WireType.Int32 ? DecodeZigZag(ReadVarUInt32()) : (int)ReadWidened(type, WireType.Int32);

    public long ReadInt64(WireType type) =>
        type == WireType.Int64 ? DecodeZigZag(ReadVarUInt64()) : ReadWidened(type, WireType.Int64);

    public float ReadFloat(WireType type)
    {
        Expect(type, WireType.Float);
        return BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    }

    public double ReadDouble(WireType type) =>
        type == WireType.Double ? BinaryPrimitives.ReadDoubleLittleEndian(Take(8)) : ReadFloat(type);

    /// <summary>Reads a UTF-8 string.</summary>
    public string ReadString(WireType type)
    {
        Expect(type, WireType.String);
        var bytes = Take(ReadLength());

        // ASCII, the common case, is widened as Latin-1, which decodes it as UTF-8 does, only faster.
        return Ascii.IsValid(bytes) ? Encoding.Latin1.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Reads a UTF-16 string.</summary>
    public string ReadWString(WireType type)
    {
        Expect(type, WireType.WString);
        return Encoding.Unicode.GetString(Take(ReadLength(), elementSize: 2));
    }

    /// <summary>Reads a blob, copying its bytes.</summary>
    public ArraySegment<byte> ReadBlob(WireType type)
    {
        var count = ReadContainerBegin(type, WireType.List, out var elementType);
        Expect(elementType, WireType.Int8);
        return new ArraySegment<byte>(Take(count).ToArray());
    }

    /// <summary>Reads the encoding of a struct without deserializing it.</summary>
    public byte[] ReadStructBytes()
    {
        var start = _position;
        Skip(WireType.Struct);
        return _data[start.._position].ToArray();
    }

    /// <summary>Skips a value of the given wire type, such as a field unknown to the reader's schema.</summary>
    public void Skip(WireType type)
    {
        switch (type)
        {
            case WireType.Bool or WireType.UInt8 or WireType.Int8:
                Take(1);
                break;
            case WireType.UInt16 or WireType.Int16:
                ReadVarUInt16();
                break;
            case WireType.UInt32 or WireType.Int32:
                ReadVarUInt32();
                break;
            case WireType.UInt64 or WireType.Int64:
                ReadVarUInt64();
                break;
            case WireType.Float:
                Take(4);
                break;
            case WireType.Double:
                Take(8);
                break;
            case WireType.String:
                Take(ReadLength());
                break;
            case WireType.WString:
                Take(ReadLength(), elementSize: 2);
                break;
            case WireType.List or WireType.Set:
                {
                    var count = ReadContainerBegin(type, type, out var elementType);
                    Enter();
                    for (var i = 0; i < count; i++)
                    {
                        Skip(elementType);
                    }

                    _depth--;
                    break;
                }
            case WireType.Map:
                {
                    var count = ReadMapBegin(type, out var keyType, out var valueType);
                    Enter();
                    for (var i = 0; i < count; i++)
                    {
                        Skip(keyType);
                        Skip(valueType);
                    }

                    _depth--;
                    break;
                }
            case WireType.Struct:
                ReadStructBegin();
                do
                {
                    while (ReadFieldBegin(out var fieldType, out _))
                    {
                        Skip(fieldType);
                    }
                }
                while (_atBaseEnd);

                _depth--;
                break;
            default:
                throw new InvalidDataException($"Invalid Bond wire type {(byte)type}.");
        }
    }

    /// <summary>Throws if a value's wire type is not the expected one, e.g. before reading a nested struct.</summary>
    public static void Expect(WireType actual, WireType expected)
    {
        if (actual != expected)
        {
            ThrowInvalidType(expected, actual);
        }
    }

    /// <summary>Throws unless a wire type is the expected one or a narrower one that widens to it.</summary>
    public static void ExpectCompatible(WireType actual, WireType expected)
    {
        if (actual != expected && !Widens(actual, expected))
        {
            ThrowInvalidType(expected, actual);
        }
    }

    /// <summary>Creates the exception for a payload that lacks a required field.</summary>
    public static InvalidDataException MissingRequiredField(string structure, string field) =>
        new($"Required field {structure}.{field} is missing from the Bond payload.");

    private void Enter()
    {
        if (++_depth > MaxDepth)
        {
            throw new InvalidDataException($"Bond payload is nested deeper than {MaxDepth} levels.");
        }
    }

    private static bool Widens(WireType type, WireType expected) => expected switch
    {
        WireType.UInt16 => type == WireType.UInt8,
        WireType.UInt32 => type is WireType.UInt16 or WireType.UInt8,
        WireType.UInt64 => type is WireType.UInt32 or WireType.UInt16 or WireType.UInt8,
        WireType.Int16 => type == WireType.Int8,
        WireType.Int32 => type is WireType.Int16 or WireType.Int8,
        WireType.Int64 => type is WireType.Int32 or WireType.Int16 or WireType.Int8,
        WireType.Double => type == WireType.Float,
        _ => false
    };

    // Widens a value of a narrower wire type of the same signedness, or throws.
    private long ReadWidened(WireType type, WireType expected)
    {
        if (!Widens(type, expected))
        {
            ThrowInvalidType(expected, type);
        }

        return type switch
        {
            WireType.UInt8 => ReadByte(),
            WireType.Int8 => (sbyte)ReadByte(),
            WireType.UInt16 => ReadVarUInt16(),
            WireType.Int16 => DecodeZigZag(ReadVarUInt16()),
            WireType.UInt32 => ReadVarUInt32(),
            _ => DecodeZigZag(ReadVarUInt32())
        };
    }

    private byte ReadByte()
    {
        if (_position >= _data.Length)
        {
            ThrowEndOfStream();
        }

        return _data[_position++];
    }

    // The next count elements of elementSize bytes, after checking that they are in the data.
    private ReadOnlySpan<byte> Take(int count, int elementSize = 1)
    {
        var size = (long)count * elementSize;
        if (size > _data.Length - _position)
        {
            ThrowEndOfStream();
        }

        var taken = _data.Slice(_position, (int)size);
        _position += (int)size;
        return taken;
    }

    private int ReadLength()
    {
        var length = ReadVarUInt32();
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("Bond length is out of range.");
        }

        return (int)length;
    }

    // Every value occupies at least one byte, so a count beyond the remaining data is invalid. This also bounds the
    // memory preallocated for containers by the size of the payload.
    private int ReadCount(int elementBytes)
    {
        var count = ReadVarUInt32();
        if (count > (uint)(_data.Length - _position) / (uint)elementBytes)
        {
            ThrowEndOfStream();
        }

        return (int)count;
    }

    // Varints of one or two bytes (values below 2^14) are read inline, longer ones out of line. Written out once per
    // width: shared helpers measured slower.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadVarUInt16()
    {
        var position = _position;
        var data = _data;
        if ((uint)position + 1 < (uint)data.Length)
        {
            ref var p = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), position);
            uint value = p;
            if (value < 0x80)
            {
                _position = position + 1;
                return (ushort)value;
            }

            uint next = Unsafe.Add(ref p, 1);
            if (next < 0x80)
            {
                _position = position + 2;
                return (ushort)((value & 0x7F) | (next << 7));
            }
        }

        return (ushort)ReadVarUInt(lastByte: 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadVarUInt32()
    {
        var position = _position;
        var data = _data;
        if ((uint)position + 1 < (uint)data.Length)
        {
            ref var p = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), position);
            uint value = p;
            if (value < 0x80)
            {
                _position = position + 1;
                return value;
            }

            uint next = Unsafe.Add(ref p, 1);
            if (next < 0x80)
            {
                _position = position + 2;
                return (value & 0x7F) | (next << 7);
            }
        }

        return ReadVarUInt32Slow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ReadVarUInt64()
    {
        var position = _position;
        var data = _data;
        if ((uint)position + 1 < (uint)data.Length)
        {
            ref var p = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), position);
            ulong value = p;
            if (value < 0x80)
            {
                _position = position + 1;
                return value;
            }

            ulong next = Unsafe.Add(ref p, 1);
            if (next < 0x80)
            {
                _position = position + 2;
                return (value & 0x7F) | (next << 7);
            }
        }

        return ReadVarUInt64Slow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint ReadVarUInt32Slow()
    {
        var position = _position;
        if (FastPdep.IsSupported && _data.Length - position >= 8)
        {
            // Ends at the first byte that does not continue, or at the fifth, whose high bits fall off the uint.
            var bytes = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), position));
            var length = (BitOperations.TrailingZeroCount((~bytes & 0x80_8080_8080UL) | 0x80_0000_0000UL) + 1) >> 3;
            _position = position + length;
            return (uint)Bmi2.X64.ParallelBitExtract(Bmi2.X64.ZeroHighBits(bytes, (ulong)length * 8), 0x7F_7F7F_7F7FUL);
        }

        return (uint)ReadVarUInt(lastByte: 4);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong ReadVarUInt64Slow()
    {
        var position = _position;
        if (FastPdep.IsSupported && _data.Length - position >= 10)
        {
            const ulong Payload = 0x7F7F_7F7F_7F7F_7F7FUL;
            ref var p = ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), position);
            var bytes = Unsafe.ReadUnaligned<ulong>(ref p);
            var ends = ~bytes & 0x8080_8080_8080_8080UL;
            if (ends != 0)
            {
                var length = (BitOperations.TrailingZeroCount(ends) + 1) >> 3;
                _position = position + length;
                return Bmi2.X64.ParallelBitExtract(Bmi2.X64.ZeroHighBits(bytes, (ulong)length * 8), Payload);
            }

            // Eight bytes continue: the ninth is taken whole, and a tenth skipped if the ninth continues too.
            ulong ninth = Unsafe.Add(ref p, 8);
            _position = position + (ninth >= 0x80 ? 10 : 9);
            return Bmi2.X64.ParallelBitExtract(bytes, Payload) | (ninth << 56);
        }

        return ReadVarUInt(lastByte: 8);
    }

    // Where the longest varint of the type fits in the remaining data: at most lastByte + 1 bytes, the last taken
    // whole (and for 64 bits, a tenth skipped if the ninth continues), never rejected as too long. Near the end of the
    // data: up to ten bytes, each checked against the end.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong ReadVarUInt(int lastByte)
    {
        var longest = lastByte == 8 ? 10 : lastByte + 1;
        if (_data.Length - _position < longest)
        {
            return ReadVarUInt64Checked();
        }

        var data = _data;
        var position = _position;
        ulong result = 0;
        for (var i = 0; ; i++)
        {
            ulong b = data[position++];
            if (i == lastByte)
            {
                if (lastByte == 8 && b >= 0x80)
                {
                    position++;
                }

                _position = position;
                return result | (b << (7 * i));
            }

            result |= (b & 0x7F) << (7 * i);
            if (b < 0x80)
            {
                _position = position;
                return result;
            }
        }
    }

    private ulong ReadVarUInt64Checked()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            ulong b = ReadByte();
            result |= (b & 0x7F) << shift;
            if (b < 0x80)
            {
                break;
            }
        }

        return result;
    }

    private static short DecodeZigZag(ushort value) => (short)((value >> 1) ^ -(value & 1));

    private static int DecodeZigZag(uint value) => (int)(value >> 1) ^ -(int)(value & 1);

    private static long DecodeZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    [DoesNotReturn]
    private static void ThrowEndOfStream() => throw new EndOfStreamException("Bond payload ended unexpectedly.");

    [DoesNotReturn]
    private static void ThrowInvalidType(WireType expected, WireType actual) =>
        throw new InvalidDataException($"Invalid Bond wire type {actual}, expected {expected}.");
}
