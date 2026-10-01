using System;
using Bond;
using Bond.IO.Safe;
using UpstreamReader = Bond.Protocols.CompactBinaryReader<Bond.IO.Safe.InputBuffer>;

namespace BondTools.Runtime;

/// <summary>A <c>bonded&lt;T&gt;</c> field's value as read: the payload of a struct, deserialized on demand.</summary>
/// <remarks>
/// It is written back as it was read, including any fields <typeparamref name="T"/> does not know. The members of
/// <see cref="IBonded"/> that convert to other types or protocols are left to upstream Bond.
/// </remarks>
public sealed class BondedPayload<T> : IBonded<T>
{
    private readonly byte[] _payload;
    private readonly BondCodec<T> _codec;

    public BondedPayload(byte[] payload, BondCodec<T> codec)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <summary>The struct's Compact Binary (v1) encoding.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    public T Deserialize() => CompactBinary.Deserialize(_payload, _codec);

    public U Deserialize<U>() => global::Bond.Deserialize<U>.From(UpstreamReader());

    public IBonded<U> Convert<U>() => new Bonded<U, UpstreamReader>(UpstreamReader());

    public void Serialize<W>(W writer) => Transcode<T>.FromTo(UpstreamReader(), writer);

    private UpstreamReader UpstreamReader() => new(new InputBuffer(_payload));

    /// <summary>Writes a <c>bonded&lt;T&gt;</c> value; a payload is written as it was read.</summary>
    public static void Write(ref CompactBinaryWriter writer, IBonded<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is BondedPayload<T> payload)
        {
            writer.WriteBytes(payload._payload);
            return;
        }

        var output = new OutputBuffer();
        value.Serialize(new Bond.Protocols.CompactBinaryWriter<OutputBuffer>(output));
        writer.WriteBytes(output.Data);
    }

    public static BondedPayload<T> Read(ref CompactBinaryReader reader, WireType type, BondCodec<T> codec)
    {
        CompactBinaryReader.Expect(type, WireType.Struct);
        return new BondedPayload<T>(reader.ReadStructBytes(), codec);
    }
}
