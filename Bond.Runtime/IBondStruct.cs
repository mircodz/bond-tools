using System;
using System.Buffers;
using System.IO;

namespace BondTools.Runtime;

/// <summary>A struct with generated Compact Binary (v1) serialization.</summary>
public interface IBondStruct<TSelf> where TSelf : IBondStruct<TSelf>
{
    /// <summary>Writes a value, including the fields of its bases.</summary>
    static abstract void Write(ref CompactBinaryWriter writer, TSelf value);

    /// <summary>Reads a value, skipping fields it does not know.</summary>
    static abstract TSelf Read(ref CompactBinaryReader reader);

    /// <summary>Reads into an existing instance, like <see cref="Deserialize(ReadOnlySpan{byte}, TSelf)"/>.</summary>
    static abstract TSelf Read(ref CompactBinaryReader reader, TSelf value);

    byte[] Serialize();

    void Serialize(IBufferWriter<byte> output);

    void Serialize(Stream output);

    /// <exception cref="InvalidDataException">The data is not a valid payload.</exception>
    /// <exception cref="EndOfStreamException">The data ends before the payload does.</exception>
    static abstract TSelf Deserialize(ReadOnlySpan<byte> data);

    static abstract TSelf Deserialize(ReadOnlySequence<byte> data);

    /// <summary>Deserializes a payload that takes the rest of the stream.</summary>
    static abstract TSelf Deserialize(Stream input);

    /// <summary>
    /// Deserializes into an existing instance and returns it. The payload's fields replace the instance's, its nested
    /// structs are read into the instance's own, and its container elements are appended; fields the payload omits
    /// keep their values. To read into a used instance as into a new one, call its generated Clear() first.
    /// </summary>
    static abstract TSelf Deserialize(ReadOnlySpan<byte> data, TSelf into);

    static abstract TSelf Deserialize(ReadOnlySequence<byte> data, TSelf into);

    static abstract TSelf Deserialize(Stream input, TSelf into);
}
