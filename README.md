# Bond

<div align="center">
    <img src="https://count.getloli.com/get/@mircodz-bond-tools?theme=asoul&padding=3" /><br>
</div>

```bash
dotnet tool install -g bond-tools
bond check schema.bond
bond breaking schema.bond --against .git#branch=main --error-format=json
bond format schema.bond
bond format schema.bond --check
```

## C# generation

```bash
bond generate csharp MyApp/order.bond -o MyApp/Generated --clone --equality --to-string --clear --serialization
```

More: [BondTools.Runtime](Bond.Runtime/README.md).

## Performance

Mean time per call on an Intel i7-12700KF with .NET 10; upstream is Bond.Runtime.CSharp 13.0.2 with Bond.IO.Unsafe buffers. Reused reads clear an instance and deserialize into it.

| Write | bond-tools | upstream | Δ |
|---|--:|--:|--:|
| Order with nested structs, strings and maps | 184 ns | 612 ns | -70% |
| 100 small structs | 970 ns | 1.99 µs | -51% |
| 100 `int32` | 100 ns | 216 ns | -54% |
| 1,000 `double` | 1.06 µs | 2.13 µs | -50% |
| `map<string, string>`, 100 entries | 656 ns | 2.93 µs | -78% |
| `map<int32, double>`, 100 entries | 209 ns | 1.16 µs | -82% |
| 4 KB order using most Bond types | 3.49 µs | 7.89 µs | -56% |

| Read | bond-tools | reused | upstream | Δ | Δ reused |
|---|--:|--:|--:|--:|--:|
| Order with nested structs, strings and maps | 528 ns | 473 ns | 828 ns | -36% | -43% |
| 100 small structs | 2.02 µs | 2.14 µs | 3.46 µs | -42% | -38% |
| 100 `int32` | 186 ns | 158 ns | 376 ns | -50% | -58% |
| 1,000 `double` | 1.48 µs | 1.18 µs | 3.37 µs | -56% | -65% |
| `map<string, string>`, 100 entries | 2.43 µs | 2.64 µs | 4.14 µs | -41% | -36% |
| `map<int32, double>`, 100 entries | 546 ns | 433 ns | 1.78 µs | -69% | -76% |
| 4 KB order using most Bond types | 7.72 µs | 7.58 µs | 11.8 µs | -35% | -36% |

`dotnet run -c Release --project benchmarks` runs them. [benchmarks](benchmarks/README.md) has the full results, also for an Apple M4 Pro.
