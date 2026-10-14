# Benchmarks

`dotnet run -c Release --project benchmarks` on .NET 10. Rows ending in `Upstream` are Bond.Runtime.CSharp 13.0.2 with Bond.IO.Unsafe buffers; ratios are against bond-tools. Rows ending in `Into` clear an instance and deserialize into it. The i7 run is pinned to performance cores (`taskset -c 4-11`).

## Intel i7-12700KF

```
BenchmarkDotNet v0.15.8, Linux Arch Linux
12th Gen Intel Core i7-12700KF 0.80GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.110
  [Host]     : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3
  Job-DYZKOT : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=3  WarmupCount=5
```

| Method                    | Categories                 | Mean         | Error      | StdDev     | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|-------------------------- |--------------------------- |-------------:|-----------:|-----------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,482.31 ns |  12.079 ns |  18.080 ns |  1.00 |    0.02 | 0.6180 | 0.0172 |    8080 B |        1.00 |
| ReadDoublesInto           | 1,000 double, read         |  1,175.69 ns |   4.580 ns |   6.713 ns |  0.79 |    0.01 |      - |      - |         - |        0.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  3,370.96 ns |  15.343 ns |  22.964 ns |  2.27 |    0.03 | 0.6180 | 0.0153 |    8080 B |        1.00 |
|                           |                            |              |            |            |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |  1,058.23 ns |   4.549 ns |   6.523 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  2,127.06 ns |   5.872 ns |   8.421 ns |  2.01 |    0.01 |      - |      - |         - |          NA |
|                           |                            |              |            |            |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    186.33 ns |   1.320 ns |   1.893 ns |  1.00 |    0.01 | 0.0367 |      - |     480 B |        1.00 |
| ReadIntsInto              | 100 int32, read            |    157.88 ns |   0.982 ns |   1.409 ns |  0.85 |    0.01 |      - |      - |         - |        0.00 |
| ReadIntsUpstream          | 100 int32, read            |    376.18 ns |   2.301 ns |   3.226 ns |  2.02 |    0.03 | 0.0367 |      - |     480 B |        1.00 |
|                           |                            |              |            |            |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |     99.62 ns |   0.623 ns |   0.894 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    216.00 ns |   0.777 ns |   1.162 ns |  2.17 |    0.02 |      - |      - |         - |          NA |
|                           |                            |              |            |            |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  2,021.03 ns |  27.237 ns |  39.923 ns |  1.00 |    0.03 | 0.6790 | 0.0191 |    8880 B |        1.00 |
| ReadItemsInto             | 100 structs, read          |  2,143.61 ns |  33.625 ns |  48.224 ns |  1.06 |    0.03 | 0.6104 | 0.0076 |    8000 B |        0.90 |
| ReadItemsUpstream         | 100 structs, read          |  3,464.24 ns |  37.333 ns |  53.542 ns |  1.71 |    0.04 | 0.6790 | 0.0191 |    8880 B |        1.00 |
|                           |                            |              |            |            |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |    969.67 ns |   4.546 ns |   6.805 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  1,993.91 ns |   9.494 ns |  14.210 ns |  2.06 |    0.02 |      - |      - |         - |          NA |
|                           |                            |              |            |            |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  7,717.09 ns |  48.324 ns |  72.329 ns |  1.00 |    0.01 | 1.9226 | 0.1373 |   25192 B |        1.00 |
| ReadBigInto               | 4 KB order, read           |  7,583.72 ns |  40.952 ns |  61.295 ns |  0.98 |    0.01 | 1.6937 | 0.0687 |   22144 B |        0.88 |
| ReadBigUpstream           | 4 KB order, read           | 11,799.21 ns |  72.071 ns | 107.872 ns |  1.53 |    0.02 | 1.8921 | 0.1373 |   24920 B |        0.99 |
|                           |                            |              |            |            |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  3,493.00 ns |  28.974 ns |  43.368 ns |  1.00 |    0.02 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  7,890.07 ns |  26.359 ns |  37.803 ns |  2.26 |    0.03 | 0.1068 |      - |    1560 B |          NA |
|                           |                            |              |            |            |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    528.32 ns |   3.650 ns |   5.235 ns |  1.00 |    0.01 | 0.1860 | 0.0010 |    2432 B |        1.00 |
| ReadOrderInto             | Order, read                |    473.10 ns |   2.926 ns |   4.196 ns |  0.90 |    0.01 | 0.1025 |      - |    1344 B |        0.55 |
| ReadOrderUpstream         | Order, read                |    828.30 ns |   6.939 ns |  10.171 ns |  1.57 |    0.02 | 0.1841 | 0.0010 |    2416 B |        0.99 |
|                           |                            |              |            |            |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    183.99 ns |   2.371 ns |   3.548 ns |  1.00 |    0.03 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    611.60 ns |   5.911 ns |   8.665 ns |  3.33 |    0.08 | 0.0114 |      - |     152 B |          NA |
|                           |                            |              |            |            |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    545.50 ns |   5.130 ns |   7.191 ns |  1.00 |    0.02 | 0.2413 | 0.0019 |    3152 B |        1.00 |
| ReadIntDoubleMapInto      | map<int32, double>, read   |    432.55 ns |   1.787 ns |   2.675 ns |  0.79 |    0.01 |      - |      - |         - |        0.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,776.28 ns |  11.127 ns |  16.310 ns |  3.26 |    0.05 | 0.7801 | 0.0172 |   10216 B |        3.24 |
|                           |                            |              |            |            |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    209.47 ns |   0.867 ns |   1.271 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |  1,156.70 ns |   3.251 ns |   4.765 ns |  5.52 |    0.04 | 0.0038 |      - |      56 B |          NA |
|                           |                            |              |            |            |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,432.44 ns |  21.802 ns |  32.632 ns |  1.00 |    0.02 | 0.8469 | 0.0305 |   11072 B |        1.00 |
| ReadStringMapInto         | map<string, string>, read  |  2,638.03 ns | 106.702 ns | 153.028 ns |  1.08 |    0.06 | 0.6027 | 0.0076 |    7920 B |        0.72 |
| ReadStringMapUpstream     | map<string, string>, read  |  4,142.67 ns |  66.767 ns |  95.755 ns |  1.70 |    0.04 | 1.3809 | 0.0687 |   18136 B |        1.64 |
|                           |                            |              |            |            |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |    655.84 ns |   5.786 ns |   8.660 ns |  1.00 |    0.02 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  2,934.35 ns |  20.564 ns |  29.493 ns |  4.47 |    0.07 | 0.0038 |      - |      56 B |          NA |

## Apple M4 Pro

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M4 Pro, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a
  Job-DYZKOT : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a

IterationCount=10  LaunchCount=3  WarmupCount=5
```

| Method                    | Categories                 | Mean         | Error      | StdDev     | Median       | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|-------------------------- |--------------------------- |-------------:|-----------:|-----------:|-------------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,212.47 ns |  20.893 ns |  29.965 ns |  1,202.93 ns |  1.00 |    0.03 | 0.9651 | 0.0267 |    8080 B |        1.00 |
| ReadDoublesInto           | 1,000 double, read         |    919.58 ns |  34.037 ns |  48.815 ns |    891.86 ns |  0.76 |    0.04 |      - |      - |         - |        0.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  4,240.16 ns |  91.215 ns | 130.818 ns |  4,247.36 ns |  3.50 |    0.14 | 0.9613 | 0.0229 |    8080 B |        1.00 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |  1,002.45 ns |  20.660 ns |  30.283 ns |  1,001.64 ns |  1.00 |    0.04 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  2,854.52 ns | 111.982 ns | 167.610 ns |  2,864.83 ns |  2.85 |    0.18 |      - |      - |         - |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    124.60 ns |   3.175 ns |   4.752 ns |    124.09 ns |  1.00 |    0.05 | 0.0572 |      - |     480 B |        1.00 |
| ReadIntsInto              | 100 int32, read            |    103.23 ns |   1.543 ns |   2.262 ns |    102.97 ns |  0.83 |    0.04 |      - |      - |         - |        0.00 |
| ReadIntsUpstream          | 100 int32, read            |    307.32 ns |   6.143 ns |   9.195 ns |    308.19 ns |  2.47 |    0.12 | 0.0572 |      - |     480 B |        1.00 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |     96.50 ns |   1.702 ns |   2.495 ns |     96.82 ns |  1.00 |    0.04 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    201.97 ns |   3.393 ns |   5.078 ns |    202.66 ns |  2.09 |    0.07 |      - |      - |         - |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  1,965.20 ns |  52.109 ns |  77.995 ns |  1,957.56 ns |  1.00 |    0.05 | 1.0605 | 0.0305 |    8880 B |        1.00 |
| ReadItemsInto             | 100 structs, read          |  2,044.76 ns |  57.780 ns |  86.482 ns |  2,056.36 ns |  1.04 |    0.06 | 0.9537 | 0.0114 |    8000 B |        0.90 |
| ReadItemsUpstream         | 100 structs, read          |  2,761.72 ns |  82.664 ns | 118.554 ns |  2,743.31 ns |  1.41 |    0.08 | 1.0605 | 0.0305 |    8880 B |        1.00 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |    872.51 ns |  14.122 ns |  20.699 ns |    867.03 ns |  1.00 |    0.03 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  1,758.85 ns |  67.876 ns |  99.491 ns |  1,707.13 ns |  2.02 |    0.12 |      - |      - |         - |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  6,779.36 ns | 225.494 ns | 337.509 ns |  6,805.95 ns |  1.00 |    0.07 | 3.0060 | 0.2365 |   25192 B |        1.00 |
| ReadBigInto               | 4 KB order, read           |  6,575.74 ns | 171.373 ns | 245.778 ns |  6,606.97 ns |  0.97 |    0.06 | 2.6474 | 0.1068 |   22144 B |        0.88 |
| ReadBigUpstream           | 4 KB order, read           | 10,584.45 ns | 263.819 ns | 394.872 ns | 10,664.28 ns |  1.57 |    0.10 | 2.9755 | 0.2289 |   24920 B |        0.99 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  3,263.49 ns |  64.730 ns |  96.885 ns |  3,272.58 ns |  1.00 |    0.04 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  5,936.51 ns | 142.951 ns | 213.963 ns |  5,942.98 ns |  1.82 |    0.08 | 0.1831 |      - |    1560 B |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    439.83 ns |  10.646 ns |  15.605 ns |    436.89 ns |  1.00 |    0.05 | 0.2904 | 0.0024 |    2432 B |        1.00 |
| ReadOrderInto             | Order, read                |    376.91 ns |  14.093 ns |  20.657 ns |    373.61 ns |  0.86 |    0.05 | 0.1602 |      - |    1344 B |        0.55 |
| ReadOrderUpstream         | Order, read                |    645.65 ns |  20.754 ns |  31.063 ns |    655.37 ns |  1.47 |    0.09 | 0.2880 | 0.0019 |    2416 B |        0.99 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    145.99 ns |   2.380 ns |   3.489 ns |    144.86 ns |  1.00 |    0.03 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    473.21 ns |   8.498 ns |  11.913 ns |    470.12 ns |  3.24 |    0.11 | 0.0181 |      - |     152 B |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    398.09 ns |   9.156 ns |  13.704 ns |    400.41 ns |  1.00 |    0.05 | 0.3767 | 0.0043 |    3152 B |        1.00 |
| ReadIntDoubleMapInto      | map<int32, double>, read   |    325.25 ns |   7.760 ns |  11.374 ns |    319.39 ns |  0.82 |    0.04 |      - |      - |         - |        0.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,748.33 ns |   8.342 ns |  11.419 ns |  1,748.67 ns |  4.40 |    0.15 | 1.2207 | 0.0191 |   10216 B |        3.24 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    174.78 ns |   0.773 ns |   1.132 ns |    175.00 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |    791.96 ns |   4.292 ns |   6.424 ns |    791.64 ns |  4.53 |    0.05 | 0.0067 |      - |      56 B |          NA |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,266.61 ns |  63.882 ns |  91.618 ns |  2,245.96 ns |  1.00 |    0.06 | 1.3199 | 0.0496 |   11072 B |        1.00 |
| ReadStringMapInto         | map<string, string>, read  |  2,190.58 ns |  58.969 ns |  84.572 ns |  2,156.16 ns |  0.97 |    0.05 | 0.9460 | 0.0114 |    7920 B |        0.72 |
| ReadStringMapUpstream     | map<string, string>, read  |  3,474.21 ns |  87.015 ns | 124.795 ns |  3,420.11 ns |  1.54 |    0.08 | 2.1667 | 0.1068 |   18136 B |        1.64 |
|                           |                            |              |            |            |              |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |    570.61 ns |  12.390 ns |  18.545 ns |    566.10 ns |  1.00 |    0.05 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  2,033.10 ns |  45.812 ns |  67.151 ns |  2,038.15 ns |  3.57 |    0.16 | 0.0038 |      - |      56 B |          NA |
