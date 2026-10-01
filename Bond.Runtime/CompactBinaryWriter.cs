using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace BondTools.Runtime;

/// <summary>Writes Bond Compact Binary (v1) to an <see cref="IBufferWriter{T}"/>.</summary>
/// <remarks>Call <see cref="Flush"/> to commit the written bytes to the output.</remarks>
public ref struct CompactBinaryWriter
{
    private const int MinimumBufferSize = 256;

    private const int MaxVarIntSize = 10;

    private readonly IBufferWriter<byte> _output;
    private Span<byte> _buffer;
    private int _position;

    public CompactBinaryWriter(IBufferWriter<byte> output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _buffer = default;
        _position = 0;
    }

    /// <summary>Commits the bytes written so far to the output.</summary>
    public void Flush()
    {
        _output.Advance(_position);
        _buffer = default;
        _position = 0;
    }

    public void WriteFieldBegin(WireType type, ushort id)
    {
        if (id <= 5)
        {
            WriteByte((byte)((uint)type | ((uint)id << 5)));
        }
        else if (id <= 0xFF)
        {
            WriteByte((byte)((uint)type | (6 << 5)));
            WriteByte((byte)id);
        }
        else
        {
            WriteByte((byte)((uint)type | (7 << 5)));
            BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), id);
            _position += 2;
        }
    }

    /// <summary>Ends a struct.</summary>
    public void WriteStructEnd() => WriteByte((byte)WireType.Stop);

    /// <summary>Ends the fields of a base struct, before those of the struct derived from it.</summary>
    public void WriteBaseEnd() => WriteByte((byte)WireType.StopBase);

    /// <summary>Begins a list, set, blob, or nullable value.</summary>
    public void WriteContainerBegin(int count, WireType elementType)
    {
        WriteByte((byte)elementType);
        WriteVarUInt64((uint)count);
    }

    public void WriteMapBegin(int count, WireType keyType, WireType valueType)
    {
        WriteByte((byte)keyType);
        WriteByte((byte)valueType);
        WriteVarUInt64((uint)count);
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt8(byte value) => WriteByte(value);

    public void WriteUInt16(ushort value) => WriteVarUInt64(value);

    public void WriteUInt32(uint value) => WriteVarUInt64(value);

    public void WriteUInt64(ulong value) => WriteVarUInt64(value);

    public void WriteInt8(sbyte value) => WriteByte((byte)value);

    public void WriteInt16(short value) => WriteVarUInt64(ZigZag(value));

    public void WriteInt32(int value) => WriteVarUInt64(ZigZag(value));

    public void WriteInt64(long value) => WriteVarUInt64(ZigZag(value));

    public void WriteFloat(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);
        _position += 4;
    }

    public void WriteDouble(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);
        _position += 8;
    }

    /// <summary>Writes a UTF-8 string, prefixed with its length in bytes.</summary>
    /// <remarks>Invalid UTF-16 is replaced with U+FFFD, as <see cref="Encoding.UTF8"/> does.</remarks>
    public void WriteString(string value)
    {
        var size = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt64((uint)size);

        // Reserve before reading the position: it may move to a new buffer and reset the position.
        var destination = Reserve(size);
        _position += Encoding.UTF8.GetBytes(value, destination);
    }

    /// <summary>Writes a UTF-16 string, prefixed with its length in characters.</summary>
    /// <remarks>Unpaired surrogates are replaced with U+FFFD, as <see cref="Encoding.Unicode"/> does.</remarks>
    public void WriteWString(string value)
    {
        WriteVarUInt64((uint)value.Length);
        var destination = Reserve(value.Length * 2);
        _position += Encoding.Unicode.GetBytes(value, destination);
    }

    /// <summary>Writes bytes as they are, such as the elements of a blob after its header.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Reserve(value.Length));
        _position += value.Length;
    }

    private void WriteByte(byte value)
    {
        Reserve(1)[0] = value;
        _position++;
    }

    private void WriteVarUInt64(ulong value)
    {
        var destination = Reserve(MaxVarIntSize);
        var size = 0;
        while (value >= 0x80)
        {
            destination[size++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[size++] = (byte)value;
        _position += size;
    }

    // Returns room for at least size bytes at the current position, moving to a new buffer if needed.
    private Span<byte> Reserve(int size)
    {
        if (_buffer.Length - _position < size)
        {
            _output.Advance(_position);
            _buffer = _output.GetSpan(Math.Max(size, MinimumBufferSize));
            _position = 0;
            if (_buffer.Length < size)
            {
                throw new InvalidOperationException("The output returned a buffer smaller than requested.");
            }
        }

        return _buffer[_position..];
    }

    private static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));

    private static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));
}
