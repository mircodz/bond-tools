using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BondTools.Runtime;

// Classes (the baseline) against structs generated from the same schemas with --structs, on the same payloads. Structs
// keep nested structs and container elements inline, so reads allocate no object per element. The Into reads clear an
// instance and deserialize into it.
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
public class StructBenchmarks
{
    private readonly ArrayBufferWriter<byte> _output = new(64 * 1024);

    private Bench.Order _order;
    private Bench.Items _items;
    private Bench.Points _points;
    private Bench.Structs.Order _orderStruct;
    private Bench.Structs.Items _itemsStruct;
    private Bench.Structs.Points _pointsStruct;
    private byte[] _orderBytes, _itemBytes, _pointBytes;

    private readonly Bench.Order _orderInto = new();
    private readonly Bench.Items _itemsInto = new();
    private readonly Bench.Points _pointsInto = new();

    // Not readonly, so that they're cleared in place rather than as copies.
    private Bench.Structs.Order _orderStructInto = new();
    private Bench.Structs.Items _itemsStructInto = new();
    private Bench.Structs.Points _pointsStructInto = new();

    [GlobalSetup]
    public void Setup()
    {
        _order = Benchmarks.CreateOrder();
        _items = Benchmarks.CreateItems();
        _points = new Bench.Points();
        for (var i = 0; i < 100; i++)
        {
            _points.values.Add(new Bench.Point { x = i * 0.5, y = i * -1.25 });
        }

        (_orderBytes, _itemBytes, _pointBytes) = (_order.Serialize(), _items.Serialize(), _points.Serialize());
        _orderStruct = Read<Bench.Structs.Order>(_orderBytes);
        _itemsStruct = Read<Bench.Structs.Items>(_itemBytes);
        _pointsStruct = Read<Bench.Structs.Points>(_pointBytes);
    }

    // The structs must write the bytes the classes write, or the comparison means nothing.
    private static T Read<T>(byte[] bytes) where T : IBondStruct<T>
    {
        var value = T.Deserialize(bytes);
        if (!value.Serialize().AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException($"{typeof(T).Name}: classes and structs write different bytes.");
        }

        return value;
    }

    private void Write<T>(T value) where T : IBondStruct<T>
    {
        _output.ResetWrittenCount();
        value.Serialize(_output);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Order, write")]
    public void WriteOrder() => Write(_order);

    [Benchmark, BenchmarkCategory("Order, write")]
    public void WriteOrderStruct() => Write(_orderStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("Order, read")]
    public Bench.Order ReadOrder() => Bench.Order.Deserialize(_orderBytes);

    [Benchmark, BenchmarkCategory("Order, read")]
    public Bench.Order ReadOrderInto()
    {
        _orderInto.Clear();
        return Bench.Order.Deserialize(_orderBytes, _orderInto);
    }

    [Benchmark, BenchmarkCategory("Order, read")]
    public Bench.Structs.Order ReadOrderStruct() => Bench.Structs.Order.Deserialize(_orderBytes);

    [Benchmark, BenchmarkCategory("Order, read")]
    public Bench.Structs.Order ReadOrderStructInto()
    {
        _orderStructInto.Clear();
        return _orderStructInto = Bench.Structs.Order.Deserialize(_orderBytes, _orderStructInto);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("100 structs, write")]
    public void WriteItems() => Write(_items);

    [Benchmark, BenchmarkCategory("100 structs, write")]
    public void WriteItemsStruct() => Write(_itemsStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("100 structs, read")]
    public Bench.Items ReadItems() => Bench.Items.Deserialize(_itemBytes);

    [Benchmark, BenchmarkCategory("100 structs, read")]
    public Bench.Items ReadItemsInto()
    {
        _itemsInto.Clear();
        return Bench.Items.Deserialize(_itemBytes, _itemsInto);
    }

    [Benchmark, BenchmarkCategory("100 structs, read")]
    public Bench.Structs.Items ReadItemsStruct() => Bench.Structs.Items.Deserialize(_itemBytes);

    [Benchmark, BenchmarkCategory("100 structs, read")]
    public Bench.Structs.Items ReadItemsStructInto()
    {
        _itemsStructInto.Clear();
        return _itemsStructInto = Bench.Structs.Items.Deserialize(_itemBytes, _itemsStructInto);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("100 points, write")]
    public void WritePoints() => Write(_points);

    [Benchmark, BenchmarkCategory("100 points, write")]
    public void WritePointsStruct() => Write(_pointsStruct);

    [Benchmark(Baseline = true), BenchmarkCategory("100 points, read")]
    public Bench.Points ReadPoints() => Bench.Points.Deserialize(_pointBytes);

    [Benchmark, BenchmarkCategory("100 points, read")]
    public Bench.Points ReadPointsInto()
    {
        _pointsInto.Clear();
        return Bench.Points.Deserialize(_pointBytes, _pointsInto);
    }

    [Benchmark, BenchmarkCategory("100 points, read")]
    public Bench.Structs.Points ReadPointsStruct() => Bench.Structs.Points.Deserialize(_pointBytes);

    [Benchmark, BenchmarkCategory("100 points, read")]
    public Bench.Structs.Points ReadPointsStructInto()
    {
        _pointsStructInto.Clear();
        return _pointsStructInto = Bench.Structs.Points.Deserialize(_pointBytes, _pointsStructInto);
    }
}
