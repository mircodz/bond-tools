using System.Threading.Tasks;
using Bond.Parser.CodeGeneration;
using static Bond.TestSupport.GeneratedCode;

namespace Bond.Parser.Tests;

public sealed class CSharpSerializationTests
{
    private const string Schema = """
        namespace Models
        struct Address { 0: string city; 1: vector<string> lines; }
        struct Person { 0: string id; 1: Address home; }
        struct Customer : Person {
            0: string name = "unnamed";
            1: Address work;
            2: vector<Address> previous;
            3: list<int32> scores;
            4: set<string> tags;
            5: map<string, Address> named;
            6: nullable<Address> billing;
            7: double rating = 1.5;
        }
        """;

    // The reference implementation, Bond.Runtime.CSharp with Compact Binary v1.
    private const string Upstream = """
        public static class Upstream
        {
            public static byte[] Write<T>(T value)
            {
                var output = new Bond.IO.Safe.OutputBuffer();
                Bond.Serialize.To(new Bond.Protocols.CompactBinaryWriter<Bond.IO.Safe.OutputBuffer>(output), value);
                return output.Data.ToArray();
            }

            public static T Read<T>(byte[] bytes) => Bond.Deserialize<T>.From(
                new Bond.Protocols.CompactBinaryReader<Bond.IO.Safe.InputBuffer>(new Bond.IO.Safe.InputBuffer(bytes)));

            // Requires both runtimes to reject a payload, or to accept it and read the same value; returns whether
            // they accepted it. Any other exception from ours fails the caller.
            public static bool Agree<T>(byte[] payload) where T : BondTools.Runtime.IBondStruct<T>
            {
                T ours = default;
                bool oursAccepted = true;
                try
                {
                    ours = T.Deserialize(payload);
                }
                catch (System.IO.EndOfStreamException)
                {
                    oursAccepted = false;
                }
                catch (System.IO.InvalidDataException)
                {
                    oursAccepted = false;
                }

                T theirs = default;
                bool theirsAccepted = true;
                try
                {
                    theirs = Read<T>(payload);
                }
                catch (Exception)
                {
                    theirsAccepted = false;
                }

                string hex = Convert.ToHexString(payload);
                if (oursAccepted != theirsAccepted)
                {
                    throw new Exception((oursAccepted ? "only ours accepts " : "only upstream accepts ") + hex);
                }

                if (oursAccepted && !ours.Serialize()
                    .SequenceEqual(theirs.Serialize()))
                {
                    throw new Exception("the runtimes read different values from " + hex);
                }

                return oursAccepted;
            }
        }
        """;

    private const string ValuesSchema = """
        namespace Models
        struct Leaf { 0: string name; 1: int32 id; }
        struct Values {
            0: string text;
            1: wstring wide;
            2: int32 i32;
            3: int64 i64;
            4: uint32 u32;
            5: uint64 u64;
            6: int16 i16;
            7: uint16 u16;
            8: vector<double> doubles;
            9: vector<float> floats;
            10: vector<int8> signed_bytes;
            11: vector<uint8> unsigned_bytes;
            12: vector<Leaf> leaves;
            13: map<string, Leaf> named;
            14: list<string> names;
            15: set<int64> ids;
            16: nullable<Leaf> maybe_leaf;
            17: blob data;
            18: double d;
            19: float f;
            20: bool flag;
            300: vector<int32> far;
        }
        """;

    // Fills every field of Models.Values from a number and a string.
    private const string Fill = """
        // Returns the smallest span allowed, so writes constantly cross buffer boundaries.
        public sealed class StingyBufferWriter : System.Buffers.IBufferWriter<byte>
        {
            private readonly List<byte> _written = new List<byte>();
            private byte[] _buffer = Array.Empty<byte>();

            public byte[] Written => _written.ToArray();

            public void Advance(int count)
            {
                if (count < 0 || count > _buffer.Length)
                {
                    throw new InvalidOperationException("advanced too far");
                }

                _written.AddRange(_buffer.AsSpan(0, count).ToArray());
                _buffer = Array.Empty<byte>();
            }

            public Memory<byte> GetMemory(int sizeHint = 0) => _buffer = new byte[Math.Max(sizeHint, 1)];

            public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        }

        public static Models.Values Fill(long n, string text)
        {
            var value = new Models.Values
            {
                text = text, wide = text, i32 = unchecked((int)n), i64 = n, u32 = unchecked((uint)n), u64 = unchecked((ulong)n),
                i16 = unchecked((short)n), u16 = unchecked((ushort)n), d = n * 0.5, f = n, flag = n % 2 == 0,
                maybe_leaf = new Models.Leaf { name = text, id = unchecked((int)n) },
                data = new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(text))
            };
            for (int i = 0; i < 3; i++)
            {
                value.doubles.Add(n + i);
                value.floats.Add(n - i);
                value.signed_bytes.Add(unchecked((sbyte)(n + i)));
                value.unsigned_bytes.Add(unchecked((byte)(n + i)));
                value.leaves.Add(new Models.Leaf { name = text + i, id = i });
                value.named[text + i] = new Models.Leaf { id = -i };
                value.names.AddLast(text + i);
                value.ids.Add(n + i);
                value.far.Add(unchecked((int)n) - i);
            }

            // Runs of one-byte, two-byte, and longer varints, so vector reads take every path: whole blocks of 16 or
            // 8 values, and single values around them.
            for (int i = 0; i < 80; i++)
            {
                value.far.Add(i < 20 ? i - 10 : i < 40 ? (i - 20) * 300 - 3000 : i < 50 ? unchecked((int)n) >> (i % 31) : i * 37 - 1000);
            }

            return value;
        }
        """;

    private static readonly CSharpGenerationOptions Options = new()
    {
        ModelFeatures = CSharpModelFeatures.Equality,
        Serialization = true
    };

    [Fact]
    public async Task ReadsMalformedPayloadsExactlyAsUpstreamDoes()
    {
        // Field headers are type | id << 5: 0x10 int32 a, 0x24 uint16 b, 0x4B list, 0x0A struct, 0x30 int32 after.
        RunScenario(await Generate("""
            namespace Models
            struct Small { 0: int32 a; 1: uint16 b; 2: vector<int32> items; }
            struct Outer { 0: Small inner; 1: int32 after; }
            """, Options), """
            static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

            // A repeated or out-of-order field is skipped: the first value wins.
            Require(Upstream.Agree<Models.Small>(Hex("10 02 10 04 00")), "repeated field");
            Require(Models.Small.Deserialize(Hex("10 02 10 04 00")).a == 1, "first value");
            Require(Upstream.Agree<Models.Small>(Hex("24 05 10 02 00")), "out-of-order field");

            // Varints never fail as too long: a 16-bit one ends after three bytes, a 32-bit one after five.
            Require(Upstream.Agree<Models.Small>(Hex("24 81 80 83 00")), "16-bit varint");
            Require(Upstream.Agree<Models.Small>(Hex("10 80 80 80 80 F1 00")), "32-bit varint");

            // A list's element type is the low five bits of its byte, checked even when the list is empty.
            Require(Upstream.Agree<Models.Small>(Hex("4B F0 01 02 00")), "element type bits");
            Require(!Upstream.Agree<Models.Small>(Hex("4B 09 00 00")), "empty list of the wrong type");

            // An end marker's long id is consumed like any field's.
            Require(Upstream.Agree<Models.Outer>(Hex("0A C0 05 30 04 00")), "end marker with a long id");
            """, Upstream);
    }

    [Fact]
    public async Task ReadsMutatedPayloadsExactlyAsUpstreamDoes()
    {
        RunScenario(await Generate(ValuesSchema, Options), """
            var random = new Random(1);
            byte[] bytes = Fixtures.Fill(-300, "naïve 😀 text").Serialize();
            int accepted = 0;
            for (int i = 0; i < 3000; i++)
            {
                var mutated = new List<byte>(bytes);
                for (int edit = random.Next(1, 4); edit > 0 && mutated.Count > 0; edit--)
                {
                    int at = random.Next(mutated.Count);
                    switch (random.Next(5))
                    {
                        case 0: mutated.RemoveRange(at, mutated.Count - at); break;
                        case 1: mutated[at] ^= (byte)(1 << random.Next(8)); break;
                        case 2: mutated[at] = (byte)random.Next(256); break;
                        case 3: mutated.Insert(at, (byte)random.Next(256)); break;
                        default: mutated.RemoveAt(at); break;
                    }
                }

                if (Upstream.Agree<Models.Values>(mutated.ToArray()))
                {
                    accepted++;
                }
            }

            Require(accepted > 300, $"only {accepted} mutations were accepted");
            """, Upstream + "public static class Fixtures { " + Fill + " }");
    }

    [Fact]
    public async Task WritesBoundaryValuesLikeUpstream()
    {
        RunScenario(await Generate(ValuesSchema, Options), """
            string[] texts =
            {
                "", "a", new string('x', 42), new string('x', 43), new string('x', 127), new string('x', 128),
                new string('x', 5461), new string('x', 5462), new string('x', 20000), "é", new string('é', 42),
                new string('é', 43), new string('é', 64), "漢字", new string('漢', 2000), "😀", "a😀b", "\uD800", "a\uDC00b",
                "\uD83D", "ascii then é", new string('x', 31) + "é"
            };
            long[] numbers =
            {
                0, 1, -1, 63, -64, 64, -65, 127, 128, 255, 256, 8191, -8192, 8192, 16383, 16384, 32767, -32768, 65535,
                2097151, 2097152, 268435455, 268435456, int.MaxValue, int.MinValue, uint.MaxValue, 1L << 35, 1L << 42,
                1L << 49, (1L << 56) - 1, 1L << 56, long.MaxValue, long.MinValue, -2
            };
            foreach (string text in texts)
            {
                foreach (long n in numbers)
                {
                    Models.Values value = Fixtures.Fill(n, text);
                    byte[] expected = Upstream.Write(value);
                    byte[] actual = value.Serialize();
                    Require(expected.SequenceEqual(actual), $"bytes differ for {n} and a {text.Length}-char string");
                    var stingy = new Fixtures.StingyBufferWriter();
                    value.Serialize(stingy);
                    Require(expected.SequenceEqual(stingy.Written), $"bytes differ through small buffers for {n}");
                    Require(Models.Values.Deserialize(expected)
                        .Equals(Upstream.Read<Models.Values>(expected)), $"read differs for {n} and a {text.Length}-char string");
                }
            }
            """, Upstream + "public static class Fixtures { " + Fill + " }");
    }

    [Fact]
    public async Task RejectsEveryTruncationAndCorruptionWithInvalidDataOrEndOfStream()
    {
        RunScenario(await Generate(ValuesSchema, Options), """
            byte[] bytes = Fixtures.Fill(-300, "naïve 😀 text").Serialize();
            for (int length = 0; length < bytes.Length; length++)
            {
                try
                {
                    Models.Values.Deserialize(bytes.AsSpan(0, length));
                    throw new Exception($"a {length}-byte prefix was accepted");
                }
                catch (System.IO.EndOfStreamException)
                {
                }
                catch (System.IO.InvalidDataException)
                {
                }
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                foreach (byte corrupt in new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF, (byte)(bytes[i] ^ 0x01), (byte)(bytes[i] ^ 0x80) })
                {
                    byte[] copy = (byte[])bytes.Clone();
                    copy[i] = corrupt;
                    try
                    {
                        Models.Values.Deserialize(copy);
                    }
                    catch (System.IO.EndOfStreamException)
                    {
                    }
                    catch (System.IO.InvalidDataException)
                    {
                    }
                }
            }
            """, "public static class Fixtures { " + Fill + " }");
    }

    [Fact]
    public async Task ReadsAndWritesVarintsExactlyAsUpstreamDoes()
    {
        // Varints take a different decoder by length, by distance from the end of the data, and by CPU (pext and pdep
        // where they are fast): random bytes, mostly continuing, cover every length and overlong form at every
        // distance from the end, read until the data runs out.
        RunScenario(await Generate(ValuesSchema, Options), """
            var random = new Random(1);
            int values = 0;
            for (int n = 0; n < 20000; n++)
            {
                var bytes = new byte[random.Next(1, 24)];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)(random.Next(10) < 7 ? 0x80 | random.Next(128) : random.Next(256));
                }

                foreach (int width in new[] { 16, 32, 64 })
                {
                    values += Varints.Compare(bytes, width);
                }
            }

            Require(values > 100000, $"only {values} varints were read");
            var edges = new List<ulong> { 0, ulong.MaxValue };
            for (int bits = 1; bits < 64; bits++)
            {
                edges.AddRange(new[] { (1UL << bits) - 1, 1UL << bits, (1UL << bits) + 1 });
            }

            for (int n = 0; n < 2000; n++)
            {
                edges.Add((ulong)random.NextInt64() >> random.Next(64));
            }

            foreach (ulong value in edges)
            {
                var ours = new System.Buffers.ArrayBufferWriter<byte>();
                var writer = new BondTools.Runtime.CompactBinaryWriter(ours);
                writer.WriteUInt64(value);
                writer.WriteUInt32((uint)value);
                writer.WriteInt64((long)value);
                writer.WriteInt32((int)value);
                writer.Flush();
                var output = new Bond.IO.Safe.OutputBuffer();
                var upstream = new Bond.Protocols.CompactBinaryWriter<Bond.IO.Safe.OutputBuffer>(output);
                upstream.WriteUInt64(value);
                upstream.WriteUInt32((uint)value);
                upstream.WriteInt64((long)value);
                upstream.WriteInt32((int)value);
                Require(output.Data.AsSpan().SequenceEqual(ours.WrittenSpan), $"bytes differ for {value:X}");
            }
            """, """
            public static class Varints
            {
                // Reads varints until the data runs out, with both runtimes; returns how many were read.
                public static int Compare(byte[] bytes, int width)
                {
                    var ours = new List<ulong>();
                    Exception oursError = null;
                    try
                    {
                        var reader = new BondTools.Runtime.CompactBinaryReader(bytes);
                        while (true)
                        {
                            ours.Add(width switch
                            {
                                16 => reader.ReadUInt16(BondTools.Runtime.WireType.UInt16),
                                32 => reader.ReadUInt32(BondTools.Runtime.WireType.UInt32),
                                _ => reader.ReadUInt64(BondTools.Runtime.WireType.UInt64),
                            });
                        }
                    }
                    catch (Exception e)
                    {
                        oursError = e;
                    }

                    var theirs = new List<ulong>();
                    Exception theirsError = null;
                    try
                    {
                        var reader = new Bond.Protocols.CompactBinaryReader<Bond.IO.Safe.InputBuffer>(new Bond.IO.Safe.InputBuffer(bytes));
                        while (true)
                        {
                            theirs.Add(width switch
                            {
                                16 => reader.ReadUInt16(),
                                32 => reader.ReadUInt32(),
                                _ => reader.ReadUInt64(),
                            });
                        }
                    }
                    catch (Exception e)
                    {
                        theirsError = e;
                    }

                    if (!ours.SequenceEqual(theirs) || oursError?.GetType() != theirsError?.GetType())
                    {
                        throw new Exception($"{width}-bit varints in {Convert.ToHexString(bytes)}: ours [{string.Join(",", ours)}] "
                            + $"then {oursError?.GetType().Name}, upstream [{string.Join(",", theirs)}] then {theirsError?.GetType().Name}");
                    }

                    return ours.Count;
                }
            }
            """);
    }

    [Fact]
    public async Task SerializesThroughEveryOverload()
    {
        RunScenario(await Generate(Schema, Options), """
            var customer = new Models.Customer { id = "c1", name = "Ada" };
            customer.previous.Add(new Models.Address { city = "Paris" });
            byte[] expected = Upstream.Write(customer);

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            customer.Serialize(buffer);
            var stream = new System.IO.MemoryStream();
            customer.Serialize(stream);
            Require(customer.Serialize().SequenceEqual(expected), "byte array");
            Require(buffer.WrittenSpan.SequenceEqual(expected), "buffer writer");
            Require(stream.ToArray().SequenceEqual(expected), "stream");

            // Three segments, so that the payload is not contiguous.
            var first = new Segment(expected.AsMemory(0, 3));
            var last = first.Append(expected.AsMemory(3, 5)).Append(expected.AsMemory(8));
            var sequence = new System.Buffers.ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            Require(Models.Customer.Deserialize(sequence).Equals(customer), "sequence");
            Require(Models.Customer.Deserialize(new System.IO.MemoryStream(expected)).Equals(customer), "stream");
            Require(Generic.Deserialize<Models.Customer>(expected).Equals(customer), "generic");
            Require(Models.Person.Deserialize(expected).id == "c1", "base reading a derived payload");
            """, Upstream + """
            public static class Generic
            {
                public static T Deserialize<T>(byte[] bytes) where T : BondTools.Runtime.IBondStruct<T> => T.Deserialize(bytes);
            }

            public sealed class Segment : System.Buffers.ReadOnlySequenceSegment<byte>
            {
                public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

                public Segment Append(ReadOnlyMemory<byte> memory)
                {
                    var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
                    Next = next;
                    return next;
                }
            }
            """);
    }

    [Fact]
    public async Task SerializesGenericStructsAsUpstreamDoes()
    {
        RunScenario(await Generate("""
            namespace Models
            struct Item { 0: int32 id; 1: string name; }
            enum Color { Red, Green, Blue }
            struct Box<T> { 0: T value; 1: nullable<T> maybe; 2: vector<T> items; 3: map<string, T> named; 4: list<T> linked; }
            struct ValueBox<T : value> { 0: T value; 1: nullable<T> maybe; 2: vector<nullable<T>> holes; }
            struct Pair<K, V> : Box<V> { 0: K key; }
            struct Derived : Box<Item> { 0: string extra; }
            struct Tree<T> { 0: T label; 1: vector<Tree<T>> children; }
            struct Holder {
                0: Box<Item> items;
                1: Box<wstring> names;
                2: ValueBox<Color> color;
                3: Pair<int64, Box<Item>> nested;
                4: Box<vector<Item>> lists;
                5: Derived derived;
                6: ValueBox<double> number;
                7: Box<map<string, int32>> maps;
                8: Tree<string> tree;
            }
            """, Options), """
            var holder = new Models.Holder();
            holder.items.value = new Models.Item { id = 1, name = "one" };
            holder.items.maybe = new Models.Item { id = 2 };
            holder.items.items.Add(new Models.Item { id = 3 });
            holder.items.named["x"] = new Models.Item { id = 4 };
            holder.items.linked.AddLast(new Models.Item { id = 5 });
            holder.names.value = "wide ✓";
            holder.names.items.Add("");
            holder.names.maybe = "m";
            holder.color.value = Models.Color.Blue;
            holder.color.maybe = Models.Color.Green;
            holder.color.holes.Add(null);
            holder.color.holes.Add(Models.Color.Red);
            holder.nested.key = -42;
            holder.nested.value = new Models.Box<Models.Item>();
            holder.nested.value.items.Add(new Models.Item { id = 6 });
            holder.lists.value = new List<Models.Item> { new Models.Item { id = 7 } };
            holder.derived.extra = "e";
            holder.derived.value = new Models.Item { id = 8 };
            holder.number.value = 1.5;
            holder.maps.value = new Dictionary<string, int> { ["k"] = 9 };
            holder.tree.label = "root";
            holder.tree.children.Add(new Models.Tree<string> { label = "leaf" });

            byte[] expected = Upstream.Write(holder);
            Require(expected.SequenceEqual(holder.Serialize()), "bytes differ from upstream");
            Require(Models.Holder.Deserialize(expected).Serialize().SequenceEqual(expected), "read differs");
            Require(Upstream.Read<Models.Holder>(expected).Serialize().SequenceEqual(expected), "upstream read differs");
            Require(Models.Holder.Deserialize(Upstream.Write(new Models.Holder())).Serialize()
                .SequenceEqual(Upstream.Write(new Models.Holder())), "defaults differ");

            // A generic struct at the top level takes its type arguments' codecs.
            var codec = BondTools.Runtime.BondCodec.Struct<Models.Item>();
            Require(holder.items.Serialize(codec).SequenceEqual(Upstream.Write(holder.items)), "top-level generic bytes");
            Require(Models.Box<Models.Item>.Deserialize(Upstream.Write(holder.items), codec).items[0].id == 3, "top-level generic read");

            var random = new Random(3);
            for (int i = 0; i < 2000; i++)
            {
                var mutated = expected.ToArray();
                for (int edit = random.Next(1, 3); edit > 0; edit--)
                {
                    mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
                }

                Upstream.Agree<Models.Holder>(random.Next(4) == 0 ? mutated[..random.Next(mutated.Length)] : mutated);
            }
            """, Upstream);
    }

    [Fact]
    public async Task KeepsBondedPayloadsAsUpstreamDoes()
    {
        RunScenario(await Generate("""
            namespace Models
            struct Payload { 0: int32 id; 1: string text; }
            struct Rich { 0: int32 id; 1: string text; 2: vector<int32> extra; }
            struct Wrapper<T> { 0: bonded<T> content; }
            struct Envelope {
                0: bonded<Payload> payload;
                1: vector<bonded<Payload>> many;
                2: map<string, bonded<Payload>> named;
                3: Wrapper<Payload> wrapped;
            }
            """, Options), """
            var envelope = new Models.Envelope();
            envelope.payload = new Bond.Bonded<Models.Payload>(new Models.Payload { id = 1, text = "one" });
            envelope.many.Add(new Bond.Bonded<Models.Payload>(new Models.Payload { id = 2 }));
            envelope.named["n"] = new Bond.Bonded<Models.Payload>(new Models.Payload { text = "three" });
            envelope.wrapped.content = new Bond.Bonded<Models.Payload>(new Models.Payload { id = 4 });

            byte[] expected = Upstream.Write(envelope);
            Require(expected.SequenceEqual(envelope.Serialize()), "bytes differ from upstream");

            // Read payloads deserialize on demand and are written back unchanged.
            var read = Models.Envelope.Deserialize(expected);
            Require(read.payload is BondTools.Runtime.BondedPayload<Models.Payload>, "not kept as a payload");
            Require(read.payload.Deserialize().text == "one" && read.many[0].Deserialize().id == 2, "deserialized value");
            Require(read.named["n"].Deserialize().text == "three" && read.wrapped.content.Deserialize().id == 4, "nested");
            Require(read.Serialize().SequenceEqual(expected), "written back differently");
            Require(Upstream.Read<Models.Envelope>(read.Serialize()).payload.Deserialize().text == "one", "upstream read");

            // A payload keeps the fields its type does not know, through a round trip.
            var rich = new Models.Rich { id = 5, text = "rich" };
            rich.extra.Add(6);
            envelope.payload = new BondTools.Runtime.BondedPayload<Models.Payload>(rich.Serialize(),
                BondTools.Runtime.BondCodec.Struct<Models.Payload>());
            var again = Models.Envelope.Deserialize(envelope.Serialize());
            Require(again.payload.Deserialize().id == 5, "known fields");
            Require(again.payload.Deserialize<Models.Rich>().extra.Single() == 6, "unknown fields lost");
            Require(again.payload.Convert<Models.Rich>().Deserialize().extra.Single() == 6, "converted");
            Require(Upstream.Read<Models.Envelope>(envelope.Serialize()).payload.Deserialize<Models.Rich>().extra.Single() == 6,
                "unknown fields lost for upstream");
            """, Upstream);
    }

    [Fact]
    public async Task ConvertsCustomMappedTypesAsUpstreamDoes()
    {
        var code = await Generate("""
            namespace Models
            using Timestamp = int64;
            using Name = string;
            using Items<T> = vector<T>;
            struct Box<T> { 0: T value; 1: vector<T> values; }
            struct Event {
                0: Timestamp created = 0;
                1: nullable<Timestamp> updated;
                2: Timestamp deleted = nothing;
                3: vector<Timestamp> history;
                4: map<Name, Timestamp> marks;
                5: Name name;
                6: Items<int32> items;
                7: Box<Timestamp> boxed;
                8: required Timestamp due;
            }
            """, new CSharpGenerationOptions
        {
            ModelFeatures = CSharpModelFeatures.Equality,
            Serialization = true,
            TypeMappings = ["Models.Timestamp=System.DateTime", "Models.Name=Models.PersonName", "Models.Items=System.Collections.Generic.LinkedList<{0}>"]
        });
        RunScenario(code, """
            var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            var value = new Models.Event { created = start, deleted = start.AddDays(1), name = new Models.PersonName("Ada") };
            value.updated = start.AddHours(1);
            value.history.Add(start);
            value.history.Add(DateTime.UnixEpoch);
            value.marks[new Models.PersonName("m")] = start.AddMinutes(1);
            value.items.AddLast(7);
            value.boxed.value = start.AddSeconds(1);
            value.boxed.values.Add(start.AddSeconds(2));
            value.due = start.AddDays(2);

            byte[] expected = Upstream.Write(value);
            Require(expected.SequenceEqual(value.Serialize()), "bytes differ from upstream");
            var read = Models.Event.Deserialize(expected);
            Require(read.Serialize().SequenceEqual(expected), "read differs");
            Require(read.created == start && read.updated == start.AddHours(1) && read.deleted == start.AddDays(1), "scalars");
            Require(read.history[1] == DateTime.UnixEpoch && read.marks[new Models.PersonName("m")] == start.AddMinutes(1), "containers");
            Require(read.name.Value == "Ada" && read.items.First.Value == 7 && read.boxed.values[0] == start.AddSeconds(2), "others");
            Require(Upstream.Read<Models.Event>(expected).Serialize().SequenceEqual(expected), "upstream read differs");

            // The mapped zero default is omitted, and an empty payload reads it back.
            var empty = new Models.Event();
            Require(Upstream.Write(empty).SequenceEqual(empty.Serialize()), "defaults differ");
            Require(Models.Event.Deserialize(empty.Serialize()).created == DateTime.UnixEpoch, "default read");
            """, Upstream + """
            namespace Models
            {
                public readonly record struct PersonName(string Value);

                public static class BondTypeAliasConverter
                {
                    public static DateTime Convert(long value, DateTime unused) => DateTime.UnixEpoch.AddTicks(value);
                    public static long Convert(DateTime value, long unused) => (value - DateTime.UnixEpoch).Ticks;
                    public static PersonName Convert(string value, PersonName unused) => new(value);
                    public static string Convert(PersonName value, string unused) => value.Value ?? "";
                    public static LinkedList<T> Convert<T>(List<T> value, LinkedList<T> unused) => new(value);
                    public static List<T> Convert<T>(LinkedList<T> value, List<T> unused) => new(value);
                }
            }
            """);
    }

    [Fact]
    public async Task WritesTheBytesUpstreamWritesAndReadsThemBack()
    {
        RunScenario(await Generate(Schema, Options), """
            var customer = new Models.Customer { id = "c1", name = "Ada", rating = 0 };
            customer.home.city = "London";
            customer.home.lines.Add("1 Main St");
            customer.previous.Add(new Models.Address { city = "Paris" });
            customer.scores.AddLast(-3);
            customer.tags.Add("vip");
            customer.named["office"] = new Models.Address { city = "Berlin" };
            customer.billing = new Models.Address { city = "Rome" };

            byte[] expected = Upstream.Write(customer);
            byte[] actual = customer.Serialize();
            Require(expected.SequenceEqual(actual), "bytes differ from upstream");
            Require(Models.Customer.Deserialize(expected).Equals(customer), "read differs");
            Require(Upstream.Read<Models.Customer>(actual).Equals(customer), "upstream read differs");

            var empty = Models.Customer.Deserialize(Upstream.Write(new Models.Customer()));
            Require(empty.Equals(new Models.Customer()), "defaults differ");
            """, Upstream);
    }

    [Fact]
    public async Task ReadingIntoAnInstanceFillsItsStructsAndAppendsToItsContainers()
    {
        RunScenario(await Generate(Schema, Options), """
            var payload = new Models.Customer { id = "new" };
            payload.home.lines.Add("b");
            payload.previous.Add(new Models.Address { city = "B" });
            byte[] bytes = payload.Serialize();

            var target = new Models.Customer { name = "kept" };
            Models.Address home = target.home;
            home.city = "kept";
            home.lines.Add("a");
            target.previous.Add(new Models.Address { city = "A" });

            var reader = new BondTools.Runtime.CompactBinaryReader(bytes);
            Require(ReferenceEquals(Models.Customer.Read(ref reader, target), target), "returned another instance");
            Require(target.id == "new" && target.name == "kept", "scalar fields");
            Require(ReferenceEquals(target.home, home) && home.city == "kept", "nested struct was replaced");
            Require(string.Join(",", home.lines) == "a,b", "nested container was not appended to");
            Require(string.Join(",", target.previous.Select(address => address.city)) == "A,B", "container was not appended to");
            """);
    }
}
