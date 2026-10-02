# Results

`dotnet run -c Release --project benchmarks` on .NET 10. Rows ending in `Upstream` are Bond.Runtime.CSharp 13.0.2 with Bond.IO.Unsafe buffers; ratios are against bond-tools. The i7 run is pinned to performance cores (`taskset -c 4-15`).

## Intel i7-12700KF

```
BenchmarkDotNet v0.15.8, Linux Arch Linux
12th Gen Intel Core i7-12700KF 0.80GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.110
  [Host]     : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3
  Job-DYZKOT : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=3  WarmupCount=5
```

| Method                    | Categories                 | Mean         | Error      | StdDev     | Median       | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|-------------------------- |--------------------------- |-------------:|-----------:|-----------:|-------------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,441.67 ns |   5.078 ns |   7.118 ns |  1,441.19 ns |  1.00 |    0.01 | 0.6180 | 0.0172 |    8080 B |        1.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  3,044.54 ns |  13.162 ns |  18.877 ns |  3,044.00 ns |  2.11 |    0.02 | 0.6180 | 0.0153 |    8080 B |        1.00 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |  1,050.29 ns |   5.292 ns |   7.921 ns |  1,050.99 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  1,904.66 ns |   7.099 ns |  10.406 ns |  1,903.75 ns |  1.81 |    0.02 |      - |      - |         - |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    179.54 ns |   1.413 ns |   2.026 ns |    179.11 ns |  1.00 |    0.02 | 0.0367 |      - |     480 B |        1.00 |
| ReadIntsUpstream          | 100 int32, read            |    342.79 ns |   1.633 ns |   2.444 ns |    343.62 ns |  1.91 |    0.02 | 0.0367 |      - |     480 B |        1.00 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |     98.77 ns |   0.512 ns |   0.751 ns |     98.80 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    230.72 ns |   0.783 ns |   1.123 ns |    230.53 ns |  2.34 |    0.02 |      - |      - |         - |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  1,962.09 ns |  16.958 ns |  24.857 ns |  1,965.10 ns |  1.00 |    0.02 | 0.6790 | 0.0191 |    8880 B |        1.00 |
| ReadItemsUpstream         | 100 structs, read          |  3,205.02 ns |  22.337 ns |  33.434 ns |  3,206.47 ns |  1.63 |    0.03 | 0.6790 | 0.0191 |    8880 B |        1.00 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |    992.57 ns |  27.922 ns |  41.793 ns |    969.76 ns |  1.00 |    0.06 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  1,985.76 ns |   9.450 ns |  14.145 ns |  1,983.90 ns |  2.00 |    0.08 |      - |      - |         - |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  7,527.80 ns |  54.761 ns |  80.268 ns |  7,532.36 ns |  1.00 |    0.01 | 1.9226 | 0.1450 |   25192 B |        1.00 |
| ReadBigUpstream           | 4 KB order, read           | 11,480.95 ns | 120.291 ns | 172.518 ns | 11,457.14 ns |  1.53 |    0.03 | 1.8921 | 0.1373 |   24920 B |        0.99 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  3,469.51 ns |  36.436 ns |  54.536 ns |  3,445.88 ns |  1.00 |    0.02 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  6,760.69 ns |  55.463 ns |  81.298 ns |  6,765.48 ns |  1.95 |    0.04 | 0.1144 |      - |    1560 B |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    518.38 ns |   4.305 ns |   6.311 ns |    518.14 ns |  1.00 |    0.02 | 0.1860 | 0.0010 |    2432 B |        1.00 |
| ReadOrderUpstream         | Order, read                |    787.29 ns |   5.869 ns |   8.602 ns |    787.70 ns |  1.52 |    0.02 | 0.1841 | 0.0010 |    2416 B |        0.99 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    180.70 ns |   3.550 ns |   5.204 ns |    180.47 ns |  1.00 |    0.04 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    597.60 ns |   2.156 ns |   3.092 ns |    597.74 ns |  3.31 |    0.10 | 0.0114 |      - |     152 B |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    518.09 ns |  10.421 ns |  15.274 ns |    522.63 ns |  1.00 |    0.04 | 0.2413 | 0.0019 |    3152 B |        1.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,714.70 ns |  12.154 ns |  18.192 ns |  1,717.68 ns |  3.31 |    0.10 | 0.7801 | 0.0172 |   10216 B |        3.24 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    208.77 ns |   1.145 ns |   1.679 ns |    208.37 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |  1,128.80 ns |   4.983 ns |   7.458 ns |  1,130.04 ns |  5.41 |    0.06 | 0.0038 |      - |      56 B |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,344.33 ns |  20.571 ns |  29.503 ns |  2,351.01 ns |  1.00 |    0.02 | 0.8469 | 0.0305 |   11072 B |        1.00 |
| ReadStringMapUpstream     | map<string, string>, read  |  3,892.37 ns |  18.612 ns |  26.693 ns |  3,886.70 ns |  1.66 |    0.02 | 1.3809 | 0.0687 |   18136 B |        1.64 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |    649.71 ns |   4.318 ns |   6.463 ns |    650.90 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  2,927.56 ns |  11.762 ns |  17.241 ns |  2,929.07 ns |  4.51 |    0.05 | 0.0038 |      - |      56 B |          NA |

## Apple M4 Pro

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M4 Pro, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a
  Job-DYZKOT : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a

IterationCount=10  LaunchCount=3  WarmupCount=5
```

| Method                    | Categories                 | Mean        | Error     | StdDev     | Median      | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|-------------------------- |--------------------------- |------------:|----------:|-----------:|------------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         | 1,132.50 ns |  9.234 ns |  12.944 ns | 1,137.22 ns |  1.00 |    0.02 | 0.9651 | 0.0267 |    8080 B |        1.00 |
| ReadDoublesUpstream       | 1,000 double, read         | 3,968.92 ns | 18.600 ns |  27.840 ns | 3,969.66 ns |  3.51 |    0.05 | 0.9613 | 0.0229 |    8080 B |        1.00 |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |   948.57 ns |  3.318 ns |   4.966 ns |   947.39 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        | 2,815.56 ns | 91.288 ns | 127.973 ns | 2,776.58 ns |  2.97 |    0.13 |      - |      - |         - |          NA |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |   116.88 ns |  0.412 ns |   0.617 ns |   117.06 ns |  1.00 |    0.01 | 0.0573 |      - |     480 B |        1.00 |
| ReadIntsUpstream          | 100 int32, read            |   287.48 ns |  2.200 ns |   3.293 ns |   286.73 ns |  2.46 |    0.03 | 0.0572 |      - |     480 B |        1.00 |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |    93.98 ns |  0.841 ns |   1.258 ns |    94.14 ns |  1.00 |    0.02 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |   191.20 ns |  0.594 ns |   0.871 ns |   190.90 ns |  2.03 |    0.03 |      - |      - |         - |          NA |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          | 1,885.27 ns | 12.637 ns |  18.915 ns | 1,883.79 ns |  1.00 |    0.01 | 1.0605 | 0.0324 |    8880 B |        1.00 |
| ReadItemsUpstream         | 100 structs, read          | 2,577.09 ns | 13.548 ns |  20.278 ns | 2,578.30 ns |  1.37 |    0.02 | 1.0605 | 0.0305 |    8880 B |        1.00 |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |   838.08 ns |  7.304 ns |  10.706 ns |   836.80 ns |  1.00 |    0.02 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         | 1,614.65 ns | 12.419 ns |  17.810 ns | 1,609.31 ns |  1.93 |    0.03 |      - |      - |         - |          NA |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           | 6,068.55 ns | 32.430 ns |  46.510 ns | 6,068.76 ns |  1.00 |    0.01 | 3.0060 | 0.2365 |   25192 B |        1.00 |
| ReadBigUpstream           | 4 KB order, read           | 9,860.83 ns | 84.786 ns | 126.903 ns | 9,821.58 ns |  1.62 |    0.02 | 2.9755 | 0.2289 |   24920 B |        0.99 |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          | 2,937.87 ns | 33.570 ns |  49.206 ns | 2,932.67 ns |  1.00 |    0.02 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          | 5,606.50 ns | 42.249 ns |  63.236 ns | 5,616.70 ns |  1.91 |    0.04 | 0.1831 |      - |    1560 B |          NA |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |   415.80 ns |  8.652 ns |  12.949 ns |   423.71 ns |  1.00 |    0.04 | 0.2904 | 0.0024 |    2432 B |        1.00 |
| ReadOrderUpstream         | Order, read                |   602.62 ns |  3.316 ns |   4.964 ns |   601.33 ns |  1.45 |    0.05 | 0.2880 | 0.0019 |    2416 B |        0.99 |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| WriteOrder                | Order, write               |   143.38 ns |  0.614 ns |   0.919 ns |   143.51 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |   438.93 ns |  2.156 ns |   3.227 ns |   438.98 ns |  3.06 |    0.03 | 0.0181 |      - |     152 B |          NA |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |   378.38 ns |  1.221 ns |   1.790 ns |   378.41 ns |  1.00 |    0.01 | 0.3767 | 0.0043 |    3152 B |        1.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   | 1,743.88 ns |  7.671 ns |  11.481 ns | 1,741.92 ns |  4.61 |    0.04 | 1.2207 | 0.0191 |   10216 B |        3.24 |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |   174.01 ns |  0.750 ns |   1.122 ns |   174.26 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |   789.83 ns |  1.611 ns |   2.205 ns |   790.04 ns |  4.54 |    0.03 | 0.0067 |      - |      56 B |          NA |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  | 2,171.90 ns | 10.869 ns |  16.269 ns | 2,172.01 ns |  1.00 |    0.01 | 1.3199 | 0.0496 |   11072 B |        1.00 |
| ReadStringMapUpstream     | map<string, string>, read  | 3,346.18 ns | 13.529 ns |  19.403 ns | 3,344.49 ns |  1.54 |    0.01 | 2.1667 | 0.1068 |   18136 B |        1.64 |
|                           |                            |             |           |            |             |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |   553.39 ns |  1.804 ns |   2.700 ns |   553.02 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write | 1,960.26 ns |  8.383 ns |  12.287 ns | 1,960.77 ns |  3.54 |    0.03 | 0.0038 |      - |      56 B |          NA |
