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

| Method                    | Categories                 | Mean         | Error      | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|-------------------------- |--------------------------- |-------------:|-----------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,444.09 ns |  18.064 ns | 13.062 ns |  1.00 |    0.01 | 0.6180 | 0.0172 |    8080 B |        1.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  3,232.43 ns |  15.259 ns | 10.093 ns |  2.24 |    0.02 | 0.6180 | 0.0153 |    8080 B |        1.00 |
|                           |                            |              |            |           |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |  1,053.58 ns |   7.288 ns |  5.690 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  1,694.99 ns |   7.314 ns |  4.838 ns |  1.61 |    0.01 |      - |      - |         - |          NA |
|                           |                            |              |            |           |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    181.59 ns |   1.510 ns |  1.179 ns |  1.00 |    0.01 | 0.0367 |      - |     480 B |        1.00 |
| ReadIntsUpstream          | 100 int32, read            |    343.96 ns |   3.068 ns |  2.395 ns |  1.89 |    0.02 | 0.0367 |      - |     480 B |        1.00 |
|                           |                            |              |            |           |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |     99.40 ns |   2.844 ns |  2.056 ns |  1.00 |    0.03 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    227.66 ns |   1.056 ns |  0.825 ns |  2.29 |    0.05 |      - |      - |         - |          NA |
|                           |                            |              |            |           |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  2,261.03 ns |  24.026 ns | 15.892 ns |  1.00 |    0.01 | 0.6790 | 0.0191 |    8880 B |        1.00 |
| ReadItemsUpstream         | 100 structs, read          |  3,335.26 ns |  35.646 ns | 25.774 ns |  1.48 |    0.01 | 0.6790 | 0.0191 |    8880 B |        1.00 |
|                           |                            |              |            |           |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |  1,404.08 ns |  10.068 ns |  7.860 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  1,946.34 ns |   8.240 ns |  5.958 ns |  1.39 |    0.01 |      - |      - |         - |          NA |
|                           |                            |              |            |           |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  7,518.83 ns |  83.181 ns | 64.942 ns |  1.00 |    0.01 | 1.9226 | 0.1450 |   25192 B |        1.00 |
| ReadBigUpstream           | 4 KB order, read           | 11,428.90 ns | 130.369 ns | 94.266 ns |  1.52 |    0.02 | 1.8921 | 0.1373 |   24920 B |        0.99 |
|                           |                            |              |            |           |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  4,102.92 ns |  27.765 ns | 21.677 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  6,797.33 ns |  53.665 ns | 41.898 ns |  1.66 |    0.01 | 0.1144 |      - |    1560 B |          NA |
|                           |                            |              |            |           |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    617.10 ns |   3.630 ns |  2.625 ns |  1.00 |    0.01 | 0.1860 | 0.0010 |    2432 B |        1.00 |
| ReadOrderUpstream         | Order, read                |    792.22 ns |  11.949 ns |  8.640 ns |  1.28 |    0.01 | 0.1841 | 0.0010 |    2416 B |        0.99 |
|                           |                            |              |            |           |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    296.68 ns |   3.503 ns |  2.735 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    586.45 ns |   2.659 ns |  1.759 ns |  1.98 |    0.02 | 0.0114 |      - |     152 B |          NA |
|                           |                            |              |            |           |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    512.68 ns |   4.672 ns |  3.648 ns |  1.00 |    0.01 | 0.2413 | 0.0019 |    3152 B |        1.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,735.54 ns |  71.599 ns | 55.900 ns |  3.39 |    0.11 | 0.7801 | 0.0172 |   10216 B |        3.24 |
|                           |                            |              |            |           |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    203.04 ns |   1.936 ns |  1.280 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |  1,107.65 ns |   3.909 ns |  3.052 ns |  5.46 |    0.04 | 0.0038 |      - |      56 B |          NA |
|                           |                            |              |            |           |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,785.66 ns |  24.780 ns | 16.390 ns |  1.00 |    0.01 | 0.8469 | 0.0305 |   11072 B |        1.00 |
| ReadStringMapUpstream     | map<string, string>, read  |  3,905.10 ns |  33.080 ns | 23.919 ns |  1.40 |    0.01 | 1.3809 | 0.0687 |   18136 B |        1.64 |
|                           |                            |              |            |           |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |  1,470.10 ns |  17.829 ns | 13.919 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  2,988.01 ns |  23.073 ns | 18.014 ns |  2.03 |    0.02 | 0.0038 |      - |      56 B |          NA |

## Apple M4 Pro

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M4 Pro, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a
  Job-DRYOBN : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a

IterationCount=12  LaunchCount=1  WarmupCount=6
```

| Method                    | Categories                 | Mean        | Error      | StdDev     | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|-------------------------- |--------------------------- |------------:|-----------:|-----------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         | 1,142.81 ns |  11.968 ns |   9.344 ns |  1.00 |    0.01 | 0.9651 | 0.0267 |    8080 B |        1.00 |
| ReadDoublesUpstream       | 1,000 double, read         | 3,907.89 ns |  34.736 ns |  25.117 ns |  3.42 |    0.03 | 0.9613 | 0.0229 |    8080 B |        1.00 |
|                           |                            |             |            |            |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |   945.15 ns |   4.984 ns |   3.891 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        | 2,901.35 ns | 282.021 ns | 220.184 ns |  3.07 |    0.22 |      - |      - |         - |          NA |
|                           |                            |             |            |            |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |   116.95 ns |   0.705 ns |   0.550 ns |  1.00 |    0.01 | 0.0572 |      - |     480 B |        1.00 |
| ReadIntsUpstream          | 100 int32, read            |   285.26 ns |   3.287 ns |   2.567 ns |  2.44 |    0.02 | 0.0572 |      - |     480 B |        1.00 |
|                           |                            |             |            |            |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |    90.48 ns |   0.484 ns |   0.320 ns |  1.00 |    0.00 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |   193.02 ns |   1.293 ns |   1.010 ns |  2.13 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |            |            |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          | 2,039.34 ns |  15.968 ns |  12.467 ns |  1.00 |    0.01 | 1.0605 | 0.0305 |    8880 B |        1.00 |
| ReadItemsUpstream         | 100 structs, read          | 2,567.12 ns |  20.059 ns |  15.661 ns |  1.26 |    0.01 | 1.0605 | 0.0305 |    8880 B |        1.00 |
|                           |                            |             |            |            |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         | 1,070.47 ns |  10.152 ns |   7.926 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         | 1,643.61 ns |  12.776 ns |   9.238 ns |  1.54 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |            |            |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           | 6,103.34 ns |  66.873 ns |  52.210 ns |  1.00 |    0.01 | 3.0060 | 0.2365 |   25192 B |        1.00 |
| ReadBigUpstream           | 4 KB order, read           | 9,896.98 ns | 112.872 ns |  88.123 ns |  1.62 |    0.02 | 2.9755 | 0.2289 |   24920 B |        0.99 |
|                           |                            |             |            |            |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          | 3,375.08 ns |  49.971 ns |  33.052 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          | 5,584.09 ns |  48.599 ns |  37.943 ns |  1.65 |    0.02 | 0.1831 |      - |    1560 B |          NA |
|                           |                            |             |            |            |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |   445.37 ns |   2.006 ns |   1.450 ns |  1.00 |    0.00 | 0.2904 | 0.0024 |    2432 B |        1.00 |
| ReadOrderUpstream         | Order, read                |   603.83 ns |   4.356 ns |   3.401 ns |  1.36 |    0.01 | 0.2880 | 0.0019 |    2416 B |        0.99 |
|                           |                            |             |            |            |       |         |        |        |           |             |
| WriteOrder                | Order, write               |   225.60 ns |   1.300 ns |   1.015 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |   455.65 ns |   4.213 ns |   3.290 ns |  2.02 |    0.02 | 0.0181 |      - |     152 B |          NA |
|                           |                            |             |            |            |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |   378.45 ns |   2.344 ns |   1.830 ns |  1.00 |    0.01 | 0.3767 | 0.0043 |    3152 B |        1.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   | 1,757.56 ns |  13.618 ns |  10.632 ns |  4.64 |    0.03 | 1.2207 | 0.0191 |   10216 B |        3.24 |
|                           |                            |             |            |            |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |   173.26 ns |   1.232 ns |   0.962 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |   792.33 ns |   5.310 ns |   4.146 ns |  4.57 |    0.03 | 0.0067 |      - |      56 B |          NA |
|                           |                            |             |            |            |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  | 2,504.68 ns |  18.930 ns |  14.779 ns |  1.00 |    0.01 | 1.3199 | 0.0496 |   11072 B |        1.00 |
| ReadStringMapUpstream     | map<string, string>, read  | 3,349.69 ns |  20.477 ns |  15.987 ns |  1.34 |    0.01 | 2.1667 | 0.1068 |   18136 B |        1.64 |
|                           |                            |             |            |            |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |   983.06 ns |   9.507 ns |   7.422 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write | 1,967.88 ns |  15.787 ns |  12.326 ns |  2.00 |    0.02 | 0.0038 |      - |      56 B |          NA |
