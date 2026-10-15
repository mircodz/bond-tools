using System;
using System.Buffers;
using System.IO;

namespace BondTools.Runtime;

/// <summary>Serializes values with Compact Binary (v1), given their codec.</summary>
/// <remarks>The generated <c>Serialize</c> and <c>Deserialize</c> methods of every struct call these.</remarks>
public static class CompactBinary
{
    // One buffer per thread for the overloads that copy the bytes out, so a call allocates only its result.
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_buffer;

    public static byte[] Serialize<T>(T value, BondCodec<T> codec)
    {
        var buffer = RentBuffer();
        try
        {
            Serialize(value, buffer, codec);
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            ReturnBuffer(buffer);
        }
    }

    public static void Serialize<T>(T value, IBufferWriter<byte> output, BondCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(codec);
        var writer = new CompactBinaryWriter(output);
        codec.Write(ref writer, value);
        writer.Flush();
    }

    public static void Serialize<T>(T value, Stream output, BondCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(output);
        var buffer = RentBuffer();
        try
        {
            Serialize(value, buffer, codec);
            output.Write(buffer.WrittenSpan);
        }
        finally
        {
            ReturnBuffer(buffer);
        }
    }

    // Taken out while in use, so a serialization nested in another (from a converter, say) gets its own.
    private static ArrayBufferWriter<byte> RentBuffer()
    {
        var buffer = t_buffer ?? new ArrayBufferWriter<byte>(1024);
        t_buffer = null;
        return buffer;
    }

    private static void ReturnBuffer(ArrayBufferWriter<byte> buffer)
    {
        if (buffer.Capacity <= 1024 * 1024)
        {
            buffer.ResetWrittenCount();
            t_buffer = buffer;
        }
    }

    /// <exception cref="InvalidDataException">The data is not a valid payload.</exception>
    /// <exception cref="EndOfStreamException">The data ends before the payload does.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> data, BondCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var reader = new CompactBinaryReader(data);
        return codec.Read(ref reader, codec.WireType);
    }

    public static T Deserialize<T>(ReadOnlySequence<byte> data, BondCodec<T> codec) =>
        Deserialize(data.IsSingleSegment ? data.FirstSpan : data.ToArray(), codec);

    /// <summary>Deserializes into an existing instance, as <see cref="IBondStruct{TSelf}"/> explains.</summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> data, T into, BondCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(codec);
        var reader = new CompactBinaryReader(data);
        return codec.Read(ref reader, codec.WireType, into);
    }

    public static T Deserialize<T>(ReadOnlySequence<byte> data, T into, BondCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(codec);
        return Deserialize(data.IsSingleSegment ? data.FirstSpan : data.ToArray(), into, codec);
    }

    public static T Deserialize<T>(Stream input, T into, BondCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(codec);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        return Deserialize(data, into, codec);
    }

    /// <summary>Deserializes a payload that takes the rest of the stream.</summary>
    public static T Deserialize<T>(Stream input, BondCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), codec);
    }
}
