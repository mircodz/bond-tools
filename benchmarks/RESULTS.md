# Results

`dotnet run -c Release --project benchmarks` on .NET 10. Rows ending in `Upstream` are Bond.Runtime.CSharp 13.0.2 with Bond.IO.Unsafe buffers; ratios are against bond-tools. The i7 run is pinned to performance cores (`taskset -c 4-15`).

## Intel i7-12700KF

```
BenchmarkDotNet v0.15.8, Linux Arch Linux
12th Gen Intel Core i7-12700KF 0.80GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.110
  [Host]     : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3
  Job-DRYOBN : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3

IterationCount=12  LaunchCount=1  WarmupCount=6
```

| Method                    | Categories                 | Mean        | Error    | StdDev   | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|-------------------------- |--------------------------- |------------:|---------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,433.2 ns | 18.36 ns | 14.34 ns |  1.00 |    0.01 | 0.6180 | 0.0172 |    8080 B |        1.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  3,028.2 ns | 18.81 ns | 14.69 ns |  2.11 |    0.02 | 0.6180 | 0.0153 |    8080 B |        1.00 |
|                           |                            |             |          |          |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |  1,057.3 ns |  8.56 ns |  6.69 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  1,895.5 ns | 10.98 ns |  8.58 ns |  1.79 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |          |          |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    303.2 ns |  3.24 ns |  2.14 ns |  1.00 |    0.01 | 0.0367 |      - |     480 B |        1.00 |
| ReadIntsUpstream          | 100 int32, read            |    344.1 ns |  3.44 ns |  2.68 ns |  1.13 |    0.01 | 0.0367 |      - |     480 B |        1.00 |
|                           |                            |             |          |          |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |    176.6 ns |  1.54 ns |  1.20 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    231.9 ns |  2.26 ns |  1.76 ns |  1.31 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |          |          |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  2,451.6 ns | 36.78 ns | 26.59 ns |  1.00 |    0.01 | 0.6790 | 0.0191 |    8880 B |        1.00 |
| ReadItemsUpstream         | 100 structs, read          |  3,196.6 ns | 33.46 ns | 26.12 ns |  1.30 |    0.02 | 0.6790 | 0.0191 |    8880 B |        1.00 |
|                           |                            |             |          |          |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |  1,397.3 ns |  4.11 ns |  2.97 ns |  1.00 |    0.00 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  1,981.4 ns | 11.74 ns |  8.49 ns |  1.42 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |          |          |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  7,748.6 ns | 82.63 ns | 54.66 ns |  1.00 |    0.01 | 1.9226 | 0.1373 |   25192 B |        1.00 |
| ReadBigUpstream           | 4 KB order, read           | 11,445.5 ns | 87.77 ns | 63.46 ns |  1.48 |    0.01 | 1.8921 | 0.1373 |   24920 B |        0.99 |
|                           |                            |             |          |          |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  4,749.4 ns | 25.99 ns | 18.79 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  6,837.6 ns | 44.56 ns | 34.79 ns |  1.44 |    0.01 | 0.1144 |      - |    1560 B |          NA |
|                           |                            |             |          |          |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    621.0 ns |  5.63 ns |  4.39 ns |  1.00 |    0.01 | 0.1860 | 0.0010 |    2432 B |        1.00 |
| ReadOrderUpstream         | Order, read                |    791.5 ns |  8.85 ns |  5.86 ns |  1.27 |    0.01 | 0.1841 | 0.0010 |    2416 B |        0.99 |
|                           |                            |             |          |          |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    339.9 ns |  2.42 ns |  1.89 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    597.5 ns |  3.08 ns |  2.23 ns |  1.76 |    0.01 | 0.0114 |      - |     152 B |          NA |
|                           |                            |             |          |          |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    598.8 ns |  7.47 ns |  5.83 ns |  1.00 |    0.01 | 0.2413 | 0.0019 |    3152 B |        1.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,740.0 ns | 20.98 ns | 16.38 ns |  2.91 |    0.04 | 0.7801 | 0.0172 |   10216 B |        3.24 |
|                           |                            |             |          |          |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    258.0 ns |  1.38 ns |  1.00 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |  1,102.8 ns |  8.47 ns |  6.62 ns |  4.27 |    0.03 | 0.0038 |      - |      56 B |          NA |
|                           |                            |             |          |          |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,790.7 ns |  9.54 ns |  7.45 ns |  1.00 |    0.00 | 0.8469 | 0.0305 |   11072 B |        1.00 |
| ReadStringMapUpstream     | map<string, string>, read  |  3,899.6 ns | 33.16 ns | 25.89 ns |  1.40 |    0.01 | 1.3809 | 0.0687 |   18136 B |        1.64 |
|                           |                            |             |          |          |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |  1,570.0 ns |  9.58 ns |  6.92 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  2,935.5 ns | 23.54 ns | 18.38 ns |  1.87 |    0.01 | 0.0038 |      - |      56 B |          NA |

## Apple M4 Pro

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M4 Pro, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a
  Job-DRYOBN : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a

IterationCount=12  LaunchCount=1  WarmupCount=6
```

| Method                    | Categories                 |        Mean |     Error |    StdDev | Ratio | RatioSD |   Gen0 |   Gen1 | Allocated | Alloc Ratio |
|---------------------------|----------------------------|------------:|----------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,157.7 ns |  13.61 ns |  10.62 ns |  1.00 |    0.01 | 0.9651 | 0.0267 |    8080 B |        1.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  3,892.9 ns |  49.55 ns |  38.69 ns |  3.36 |    0.04 | 0.9613 | 0.0229 |    8080 B |        1.00 |
|                           |                            |             |           |           |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |    948.7 ns |   7.74 ns |   5.12 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  2,727.8 ns | 157.97 ns | 123.33 ns |  2.88 |    0.13 |      - |      - |         - |          NA |
|                           |                            |             |           |           |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    341.4 ns |   2.30 ns |   1.79 ns |  1.00 |    0.01 | 0.0572 |      - |     480 B |        1.00 |
| ReadIntsUpstream          | 100 int32, read            |    280.9 ns |   3.01 ns |   2.35 ns |  0.82 |    0.01 | 0.0572 |      - |     480 B |        1.00 |
|                           |                            |             |           |           |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |    138.9 ns |   0.90 ns |   0.70 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    191.5 ns |   1.33 ns |   0.88 ns |  1.38 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |           |           |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  2,009.7 ns |   7.19 ns |   4.75 ns |  1.00 |    0.00 | 1.0605 | 0.0305 |    8880 B |        1.00 |
| ReadItemsUpstream         | 100 structs, read          |  2,572.3 ns |  27.54 ns |  21.50 ns |  1.28 |    0.01 | 1.0605 | 0.0305 |    8880 B |        1.00 |
|                           |                            |             |           |           |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |  1,018.8 ns |   6.89 ns |   4.98 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  1,614.0 ns |  15.18 ns |  11.85 ns |  1.58 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |           |           |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  6,228.0 ns |  46.18 ns |  36.06 ns |  1.00 |    0.01 | 3.0060 | 0.2365 |   25192 B |        1.00 |
| ReadBigUpstream           | 4 KB order, read           | 10,039.9 ns |  87.85 ns |  68.58 ns |  1.61 |    0.01 | 2.9755 | 0.2289 |   24920 B |        0.99 |
|                           |                            |             |           |           |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  3,776.7 ns |  51.15 ns |  39.93 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  5,603.4 ns |  61.84 ns |  48.28 ns |  1.48 |    0.02 | 0.1831 |      - |    1560 B |          NA |
|                           |                            |             |           |           |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    449.6 ns |   4.99 ns |   3.90 ns |  1.00 |    0.01 | 0.2904 | 0.0024 |    2432 B |        1.00 |
| ReadOrderUpstream         | Order, read                |    614.0 ns |   7.91 ns |   6.17 ns |  1.37 |    0.02 | 0.2880 | 0.0019 |    2416 B |        0.99 |
|                           |                            |             |           |           |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    267.0 ns |   2.53 ns |   1.97 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    442.1 ns |   2.40 ns |   1.88 ns |  1.66 |    0.01 | 0.0181 |      - |     152 B |          NA |
|                           |                            |             |           |           |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    440.3 ns |   5.00 ns |   3.90 ns |  1.00 |    0.01 | 0.3767 | 0.0043 |    3152 B |        1.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,759.1 ns |  17.64 ns |  12.76 ns |  4.00 |    0.04 | 1.2207 | 0.0191 |   10216 B |        3.24 |
|                           |                            |             |           |           |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    224.4 ns |   1.37 ns |   1.07 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |    789.5 ns |   5.75 ns |   4.49 ns |  3.52 |    0.03 | 0.0067 |      - |      56 B |          NA |
|                           |                            |             |           |           |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,632.0 ns |  20.10 ns |  14.54 ns |  1.00 |    0.01 | 1.3199 | 0.0496 |   11072 B |        1.00 |
| ReadStringMapUpstream     | map<string, string>, read  |  3,342.2 ns |  36.69 ns |  28.65 ns |  1.27 |    0.01 | 2.1667 | 0.1068 |   18136 B |        1.64 |
|                           |                            |             |           |           |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |  1,096.7 ns |   9.43 ns |   7.36 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  1,961.1 ns |  17.54 ns |  12.68 ns |  1.79 |    0.02 | 0.0038 |      - |      56 B |          NA |
