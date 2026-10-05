# Results

`dotnet run -c Release --project benchmarks` on .NET 10. Rows ending in `Upstream` are Bond.Runtime.CSharp 13.0.2 with Bond.IO.Unsafe buffers; ratios are against bond-tools. Rows ending in `Into` clear an instance and deserialize into it. The i7 run is pinned to performance cores (`taskset -c 4-15`).

## Intel i7-12700KF

```
BenchmarkDotNet v0.15.8, Linux Arch Linux
12th Gen Intel Core i7-12700KF 0.80GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.110
  [Host]     : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3
  Job-DYZKOT : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=3  WarmupCount=5
```

| Method                    | Categories                 | Mean         | Error     | StdDev     | Median      | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|-------------------------- |--------------------------- |-------------:|----------:|-----------:|------------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,510.65 ns | 34.173 ns |  46.776 ns |  1,496.4 ns |  1.00 |    0.04 | 0.6180 | 0.0172 |    8080 B |        1.00 |
| ReadDoublesInto           | 1,000 double, read         |  1,175.24 ns |  4.917 ns |   7.360 ns |  1,176.2 ns |  0.78 |    0.02 |      - |      - |         - |        0.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  3,415.81 ns | 52.240 ns |  78.191 ns |  3,390.1 ns |  2.26 |    0.08 | 0.6180 | 0.0153 |    8080 B |        1.00 |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |  1,063.57 ns |  3.990 ns |   5.972 ns |  1,065.0 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  2,145.64 ns | 11.009 ns |  16.478 ns |  2,141.5 ns |  2.02 |    0.02 |      - |      - |         - |          NA |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    189.82 ns |  1.485 ns |   2.177 ns |    189.9 ns |  1.00 |    0.02 | 0.0367 |      - |     480 B |        1.00 |
| ReadIntsInto              | 100 int32, read            |    156.63 ns |  3.216 ns |   4.814 ns |    158.6 ns |  0.83 |    0.03 |      - |      - |         - |        0.00 |
| ReadIntsUpstream          | 100 int32, read            |    376.81 ns |  1.885 ns |   2.643 ns |    377.2 ns |  1.99 |    0.03 | 0.0367 |      - |     480 B |        1.00 |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |     99.69 ns |  0.824 ns |   1.234 ns |    100.1 ns |  1.00 |    0.02 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    217.03 ns |  1.542 ns |   2.260 ns |    217.4 ns |  2.18 |    0.03 |      - |      - |         - |          NA |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  2,079.52 ns | 21.298 ns |  31.219 ns |  2,079.2 ns |  1.00 |    0.02 | 0.6790 | 0.0191 |    8880 B |        1.00 |
| ReadItemsInto             | 100 structs, read          |  2,158.30 ns | 43.847 ns |  64.270 ns |  2,131.7 ns |  1.04 |    0.03 | 0.6104 | 0.0076 |    8000 B |        0.90 |
| ReadItemsUpstream         | 100 structs, read          |  3,523.77 ns | 13.104 ns |  18.370 ns |  3,523.4 ns |  1.69 |    0.03 | 0.6790 | 0.0191 |    8880 B |        1.00 |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |    982.66 ns |  5.722 ns |   8.387 ns |    981.7 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  2,017.28 ns | 10.407 ns |  15.577 ns |  2,018.9 ns |  2.05 |    0.02 |      - |      - |         - |          NA |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  7,922.17 ns | 84.330 ns | 120.943 ns |  7,894.4 ns |  1.00 |    0.02 | 1.9226 | 0.1373 |   25192 B |        1.00 |
| ReadBigInto               | 4 KB order, read           |  7,677.05 ns | 58.600 ns |  85.895 ns |  7,673.9 ns |  0.97 |    0.02 | 1.6937 | 0.0610 |   22144 B |        0.88 |
| ReadBigUpstream           | 4 KB order, read           | 12,069.35 ns | 59.841 ns |  89.567 ns | 12,081.2 ns |  1.52 |    0.03 | 1.8921 | 0.1373 |   24920 B |        0.99 |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  3,524.98 ns | 20.759 ns |  30.428 ns |  3,518.1 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  7,992.00 ns | 30.172 ns |  45.160 ns |  7,992.7 ns |  2.27 |    0.02 | 0.1068 |      - |    1560 B |          NA |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    547.75 ns |  7.827 ns |  11.225 ns |    544.6 ns |  1.00 |    0.03 | 0.1860 | 0.0010 |    2432 B |        1.00 |
| ReadOrderInto             | Order, read                |    475.80 ns |  3.265 ns |   4.785 ns |    476.1 ns |  0.87 |    0.02 | 0.1020 |      - |    1344 B |        0.55 |
| ReadOrderUpstream         | Order, read                |    848.77 ns |  7.572 ns |  11.099 ns |    852.4 ns |  1.55 |    0.04 | 0.1841 | 0.0010 |    2416 B |        0.99 |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    184.00 ns |  2.342 ns |   3.506 ns |    183.8 ns |  1.00 |    0.03 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    632.62 ns |  8.244 ns |  12.084 ns |    628.3 ns |  3.44 |    0.09 | 0.0114 |      - |     152 B |          NA |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    541.18 ns | 10.875 ns |  16.278 ns |    533.4 ns |  1.00 |    0.04 | 0.2413 | 0.0019 |    3152 B |        1.00 |
| ReadIntDoubleMapInto      | map<int32, double>, read   |    427.75 ns |  9.999 ns |  14.966 ns |    436.7 ns |  0.79 |    0.04 |      - |      - |         - |        0.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,804.46 ns | 10.948 ns |  15.701 ns |  1,804.0 ns |  3.34 |    0.10 | 0.7801 | 0.0172 |   10216 B |        3.24 |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    211.69 ns |  1.250 ns |   1.872 ns |    212.0 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |  1,164.80 ns |  4.423 ns |   6.484 ns |  1,165.3 ns |  5.50 |    0.06 | 0.0038 |      - |      56 B |          NA |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,472.27 ns | 20.742 ns |  30.404 ns |  2,477.1 ns |  1.00 |    0.02 | 0.8469 | 0.0305 |   11072 B |        1.00 |
| ReadStringMapInto         | map<string, string>, read  |  2,605.86 ns | 94.230 ns | 138.120 ns |  2,536.9 ns |  1.05 |    0.06 | 0.6027 | 0.0076 |    7920 B |        0.72 |
| ReadStringMapUpstream     | map<string, string>, read  |  4,136.15 ns | 25.657 ns |  35.968 ns |  4,140.7 ns |  1.67 |    0.02 | 1.3809 | 0.0687 |   18136 B |        1.64 |
|                           |                            |              |           |            |             |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |    662.66 ns |  4.480 ns |   6.425 ns |    660.4 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  2,959.59 ns | 20.063 ns |  30.029 ns |  2,960.8 ns |  4.47 |    0.06 | 0.0038 |      - |      56 B |          NA |

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
