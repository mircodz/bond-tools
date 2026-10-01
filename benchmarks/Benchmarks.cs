using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Bond;
using Bond.IO.Unsafe;
using BondTools.Runtime;
using Bench;
using UpstreamReader = Bond.Protocols.CompactBinaryReader<Bond.IO.Unsafe.InputBuffer>;
using UpstreamWriter = Bond.Protocols.CompactBinaryWriter<Bond.IO.Unsafe.OutputBuffer>;

// Each payload is written and read by BondTools.Runtime (the baseline) and by Bond.Runtime.CSharp, reusing buffers
// and upstream's serializers. Upstream uses Bond.IO.Unsafe buffers, the faster ones by Bond's own advice.
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(launchCount: 1, warmupCount: 6, iterationCount: 12)]
public class Benchmarks
{
    private readonly ArrayBufferWriter<byte> _output = new(64 * 1024);
    private readonly OutputBuffer _upstreamOutput = new(64 * 1024);

    private Order _order;
    private Items _items;
    private Int32s _ints;
    private Doubles _doubles;
    private StringMap _stringMap;
    private IntDoubleMap _intDoubleMap;
    private Bench.Shop.Order _big;

    private byte[] _orderBytes, _itemBytes, _intBytes, _doubleBytes, _stringMapBytes, _intDoubleMapBytes, _bigBytes;
    private InputBuffer _orderInput, _itemInput, _intInput, _doubleInput, _stringMapInput, _intDoubleMapInput, _bigInput;

    [GlobalSetup]
    public void Setup()
    {
        _order = new Order { id = 42, paid = true };
        _order.customer.name = "Ada Lovelace";
        _order.customer.address.street = "1 Main St";
        _order.customer.address.city = "London";
        _order.customer.address.zip = "N1";
        _order.customer.emails.Add("ada@example.com");
        _order.customer.emails.Add("ada@work.example.com");
        _order.customer.attributes["tier"] = "gold";
        _order.customer.attributes["lang"] = "en";
        for (var i = 0; i < 10; i++)
        {
            _order.lines.Add(new Line { sku = "SKU-" + i, quantity = (uint)i + 1, price = 9.99 * i });
            _order.codes.Add(i * 7);
        }

        _order.properties["channel"] = "web";
        _order.properties["coupon"] = "NONE";
        _order.labels.Add("priority");
        _order.labels.Add("gift");

        _items = new Items();
        _ints = new Int32s();
        _doubles = new Doubles();
        _stringMap = new StringMap();
        _intDoubleMap = new IntDoubleMap();
        for (var i = 0; i < 100; i++)
        {
            _items.values.Add(new Item { id = i, value = i * 1000L, name = "item-" + i });
            _ints.values.Add(i * 37 - 1000);
            _stringMap.values["key-" + i] = "value-" + i;
            _intDoubleMap.values[i * 37] = i * 9.99;
        }

        for (var i = 0; i < 1000; i++)
        {
            _doubles.values.Add(i * 1.25);
        }

        // About 4 KB: eight lines and three shipments, the rest random but fixed by the seed.
        var filler = new Filler(new Random(42), maxItems: 4);
        _big = filler.Create<Bench.Shop.Order>();
        _big.lines.Clear();
        _big.shipments.Clear();
        for (var i = 0; i < 8; i++)
        {
            _big.lines.Add(filler.Create<Bench.Shop.Line>());
        }

        for (var i = 0; i < 3; i++)
        {
            _big.shipments["S" + i] = filler.Create<Bench.Shop.Shipment>();
        }

        _orderBytes = Bytes(_order);
        _itemBytes = Bytes(_items);
        _intBytes = Bytes(_ints);
        _doubleBytes = Bytes(_doubles);
        _stringMapBytes = Bytes(_stringMap);
        _intDoubleMapBytes = Bytes(_intDoubleMap);
        _bigBytes = Bytes(_big);
        (_orderInput, _itemInput, _intInput, _doubleInput) = (new(_orderBytes), new(_itemBytes), new(_intBytes), new(_doubleBytes));
        (_stringMapInput, _intDoubleMapInput, _bigInput) = (new(_stringMapBytes), new(_intDoubleMapBytes), new(_bigBytes));
    }

    // Both runtimes must write the same bytes, or the comparison means nothing.
    private static byte[] Bytes<T>(T value) where T : IBondStruct<T>
    {
        var output = new OutputBuffer();
        Upstream<T>.Serializer.Serialize(value, new UpstreamWriter(output));
        var bytes = value.Serialize();
        if (!output.Data.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException($"{typeof(T).Name}: the runtimes write different bytes.");
        }

        return bytes;
    }

    private void Write<T>(T value) where T : IBondStruct<T>
    {
        _output.ResetWrittenCount();
        value.Serialize(_output);
    }

    private void WriteUpstream<T>(T value)
    {
        _upstreamOutput.Position = 0;
        Upstream<T>.Serializer.Serialize(value, new UpstreamWriter(_upstreamOutput));
    }

    private static T ReadUpstream<T>(InputBuffer input)
    {
        input.Position = 0;
        return Upstream<T>.Deserializer.Deserialize<T>(new UpstreamReader(input));
    }

    private static class Upstream<T>
    {
        public static readonly Serializer<UpstreamWriter> Serializer = new(typeof(T));
        public static readonly Deserializer<UpstreamReader> Deserializer = new(typeof(T));
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Order, write")]
    public void WriteOrder() => Write(_order);

    [Benchmark, BenchmarkCategory("Order, write")]
    public void WriteOrderUpstream() => WriteUpstream(_order);

    [Benchmark(Baseline = true), BenchmarkCategory("Order, read")]
    public Order ReadOrder() => Order.Deserialize(_orderBytes);

    [Benchmark, BenchmarkCategory("Order, read")]
    public Order ReadOrderUpstream() => ReadUpstream<Order>(_orderInput);

    [Benchmark(Baseline = true), BenchmarkCategory("100 structs, write")]
    public void WriteItems() => Write(_items);

    [Benchmark, BenchmarkCategory("100 structs, write")]
    public void WriteItemsUpstream() => WriteUpstream(_items);

    [Benchmark(Baseline = true), BenchmarkCategory("100 structs, read")]
    public Items ReadItems() => Items.Deserialize(_itemBytes);

    [Benchmark, BenchmarkCategory("100 structs, read")]
    public Items ReadItemsUpstream() => ReadUpstream<Items>(_itemInput);

    [Benchmark(Baseline = true), BenchmarkCategory("100 int32, write")]
    public void WriteInts() => Write(_ints);

    [Benchmark, BenchmarkCategory("100 int32, write")]
    public void WriteIntsUpstream() => WriteUpstream(_ints);

    [Benchmark(Baseline = true), BenchmarkCategory("100 int32, read")]
    public Int32s ReadInts() => Int32s.Deserialize(_intBytes);

    [Benchmark, BenchmarkCategory("100 int32, read")]
    public Int32s ReadIntsUpstream() => ReadUpstream<Int32s>(_intInput);

    [Benchmark(Baseline = true), BenchmarkCategory("1,000 double, write")]
    public void WriteDoubles() => Write(_doubles);

    [Benchmark, BenchmarkCategory("1,000 double, write")]
    public void WriteDoublesUpstream() => WriteUpstream(_doubles);

    [Benchmark(Baseline = true), BenchmarkCategory("1,000 double, read")]
    public Doubles ReadDoubles() => Doubles.Deserialize(_doubleBytes);

    [Benchmark, BenchmarkCategory("1,000 double, read")]
    public Doubles ReadDoublesUpstream() => ReadUpstream<Doubles>(_doubleInput);

    [Benchmark(Baseline = true), BenchmarkCategory("map<string, string>, write")]
    public void WriteStringMap() => Write(_stringMap);

    [Benchmark, BenchmarkCategory("map<string, string>, write")]
    public void WriteStringMapUpstream() => WriteUpstream(_stringMap);

    [Benchmark(Baseline = true), BenchmarkCategory("map<string, string>, read")]
    public StringMap ReadStringMap() => StringMap.Deserialize(_stringMapBytes);

    [Benchmark, BenchmarkCategory("map<string, string>, read")]
    public StringMap ReadStringMapUpstream() => ReadUpstream<StringMap>(_stringMapInput);

    [Benchmark(Baseline = true), BenchmarkCategory("map<int32, double>, write")]
    public void WriteIntDoubleMap() => Write(_intDoubleMap);

    [Benchmark, BenchmarkCategory("map<int32, double>, write")]
    public void WriteIntDoubleMapUpstream() => WriteUpstream(_intDoubleMap);

    [Benchmark(Baseline = true), BenchmarkCategory("map<int32, double>, read")]
    public IntDoubleMap ReadIntDoubleMap() => IntDoubleMap.Deserialize(_intDoubleMapBytes);

    [Benchmark, BenchmarkCategory("map<int32, double>, read")]
    public IntDoubleMap ReadIntDoubleMapUpstream() => ReadUpstream<IntDoubleMap>(_intDoubleMapInput);

    [Benchmark(Baseline = true), BenchmarkCategory("4 KB order, write")]
    public void WriteBig() => Write(_big);

    [Benchmark, BenchmarkCategory("4 KB order, write")]
    public void WriteBigUpstream() => WriteUpstream(_big);

    [Benchmark(Baseline = true), BenchmarkCategory("4 KB order, read")]
    public Bench.Shop.Order ReadBig() => Bench.Shop.Order.Deserialize(_bigBytes);

    [Benchmark, BenchmarkCategory("4 KB order, read")]
    public Bench.Shop.Order ReadBigUpstream() => ReadUpstream<Bench.Shop.Order>(_bigInput);
}
