# Bond

<div align="center">
    <img src="https://count.getloli.com/get/@mircodz-bond-tools?theme=asoul&padding=3" /><br>
</div>

```bash
dotnet tool install -g bond-tools
bond check schema.bond
bond breaking schema.bond --against .git#branch=main --error-format=json
bond format schema.bond
bond format *.bond --check
```

## C# generation

```bash
bond generate csharp MyApp/order.bond -o MyApp/Generated --equality --to-string --clear --serialization
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

`--structs` stores nested structs and container elements inline. Against classes, on the same machine and payloads:

| Write | classes | structs | Δ |
|---|--:|--:|--:|
| Order with nested structs, strings and maps | 146 ns | 151 ns | +4% |
| 100 small structs | 787 ns | 788 ns | 0% |
| 100 points of two `double`s | 301 ns | 286 ns | -5% |

| Read | classes | structs | Δ | allocated |
|---|--:|--:|--:|--:|
| Order with nested structs, strings and maps | 530 ns | 517 ns | -2% | 2,432 → 2,032 B |
| Order, reused | 477 ns | 463 ns | -3% | 1,344 → 944 B |
| 100 small structs | 1.97 µs | 1.77 µs | -10% | 8,880 → 6,456 B |
| 100 small structs, reused | 2.11 µs | 1.83 µs | -13% | 8,000 → 4,000 B |
| 100 points of two `double`s | 877 ns | 649 ns | -26% | 4,080 → 1,656 B |
| 100 points, reused | 919 ns | 524 ns | -43% | 3,200 → 0 B |

`dotnet run -c Release --project benchmarks -- --filter '*'` runs them. [benchmarks](benchmarks/README.md) has the full results, also for an Apple M4 Pro.
