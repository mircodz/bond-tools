using System;
using System.Buffers;
using System.IO;

namespace BondTools.Runtime;

/// <summary>Serializes values with Compact Binary (v1), given their codec.</summary>
/// <remarks>The generated <c>Serialize</c> and <c>Deserialize</c> methods of every struct call these.</remarks>
public static class CompactBinary
{
    public static byte[] Serialize<T>(T value, BondCodec<T> codec)
    {
        var output = new ArrayBufferWriter<byte>();
        Serialize(value, output, codec);
        return output.WrittenSpan.ToArray();
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
        var buffer = new ArrayBufferWriter<byte>();
        Serialize(value, buffer, codec);
        output.Write(buffer.WrittenSpan);
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

    /// <summary>Deserializes a payload that takes the rest of the stream.</summary>
    public static T Deserialize<T>(Stream input, BondCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), codec);
    }
}
