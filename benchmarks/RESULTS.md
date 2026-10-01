# Results

## Intel i7-12700KF

```
BenchmarkDotNet v0.15.8, Linux Arch Linux
12th Gen Intel Core i7-12700KF 0.80GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.110
  [Host]     : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3
  Job-DRYOBN : .NET 10.0.10 (10.0.10, 42.42.42.42424), X64 RyuJIT x86-64-v3

IterationCount=12  LaunchCount=1  WarmupCount=6
```

| Method                    | Categories                 |        Mean |     Error |   StdDev | Ratio | RatioSD |   Gen0 |   Gen1 | Allocated | Alloc Ratio |
|---------------------------|----------------------------|------------:|----------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,411.7 ns |  17.81 ns | 12.88 ns |  1.00 |    0.01 | 0.6180 | 0.0172 |    8080 B |        1.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  3,237.5 ns |  23.93 ns | 15.83 ns |  2.29 |    0.02 | 0.6180 | 0.0153 |    8080 B |        1.00 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |  1,067.8 ns |  12.28 ns |  9.58 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  3,042.1 ns |  27.93 ns | 21.81 ns |  2.85 |    0.03 |      - |      - |         - |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    299.1 ns |   3.82 ns |  2.98 ns |  1.00 |    0.01 | 0.0367 |      - |     480 B |        1.00 |
| ReadIntsUpstream          | 100 int32, read            |    362.5 ns |   4.93 ns |  3.57 ns |  1.21 |    0.02 | 0.0367 |      - |     480 B |        1.00 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |    165.1 ns |   1.43 ns |  1.11 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    212.1 ns |   1.51 ns |  1.00 ns |  1.28 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  2,442.0 ns |  28.55 ns | 20.64 ns |  1.00 |    0.01 | 0.6790 | 0.0191 |    8880 B |        1.00 |
| ReadItemsUpstream         | 100 structs, read          |  3,333.8 ns |  34.08 ns | 24.64 ns |  1.37 |    0.01 | 0.6790 | 0.0191 |    8880 B |        1.00 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |  1,399.3 ns |   4.99 ns |  3.30 ns |  1.00 |    0.00 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  1,955.6 ns |  16.84 ns | 13.15 ns |  1.40 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  7,734.3 ns |  78.31 ns | 61.14 ns |  1.00 |    0.01 | 1.9226 | 0.1373 |   25192 B |        1.00 |
| ReadBigUpstream           | 4 KB order, read           | 11,431.4 ns | 113.32 ns | 81.94 ns |  1.48 |    0.02 | 1.8921 | 0.1373 |   24920 B |        0.99 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  4,717.1 ns |  17.84 ns | 11.80 ns |  1.00 |    0.00 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  6,738.4 ns |  30.62 ns | 22.14 ns |  1.43 |    0.01 | 0.1144 |      - |    1560 B |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    622.7 ns |   6.31 ns |  4.92 ns |  1.00 |    0.01 | 0.1860 | 0.0010 |    2432 B |        1.00 |
| ReadOrderUpstream         | Order, read                |    799.2 ns |  10.20 ns |  7.96 ns |  1.28 |    0.02 | 0.1841 | 0.0010 |    2416 B |        0.99 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    341.7 ns |   2.60 ns |  2.03 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    586.4 ns |   4.29 ns |  3.35 ns |  1.72 |    0.01 | 0.0114 |      - |     152 B |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    615.0 ns |   5.60 ns |  4.37 ns |  1.00 |    0.01 | 0.2413 | 0.0019 |    3152 B |        1.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,739.2 ns |  15.13 ns | 11.81 ns |  2.83 |    0.03 | 0.7801 | 0.0172 |   10216 B |        3.24 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    256.7 ns |   2.19 ns |  1.71 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |  1,160.7 ns |   5.73 ns |  4.47 ns |  4.52 |    0.03 | 0.0038 |      - |      56 B |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,802.2 ns |  11.63 ns |  9.08 ns |  1.00 |    0.00 | 0.8469 | 0.0305 |   11072 B |        1.00 |
| ReadStringMapUpstream     | map<string, string>, read  |  3,905.3 ns |  83.94 ns | 55.52 ns |  1.39 |    0.02 | 1.3809 | 0.0687 |   18136 B |        1.64 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |  1,563.1 ns |  11.71 ns |  9.14 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  2,914.7 ns |  28.74 ns | 22.44 ns |  1.86 |    0.02 | 0.0038 |      - |      56 B |          NA |

## Apple M4 Pro

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M4 Pro, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.103
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a
  Job-DRYOBN : .NET 10.0.3 (10.0.3, 10.0.326.7603), Arm64 RyuJIT armv8.0-a

IterationCount=12  LaunchCount=1  WarmupCount=6

```
| Method                    | Categories                 |        Mean |     Error |   StdDev | Ratio | RatioSD |   Gen0 |   Gen1 | Allocated | Alloc Ratio |
|---------------------------|----------------------------|------------:|----------:|---------:|------:|--------:|-------:|-------:|----------:|------------:|
| ReadDoubles               | 1,000 double, read         |  1,112.6 ns |  10.21 ns |  7.97 ns |  1.00 |    0.01 | 0.9651 | 0.0267 |    8080 B |        1.00 |
| ReadDoublesUpstream       | 1,000 double, read         |  3,608.6 ns |  44.91 ns | 35.07 ns |  3.24 |    0.04 | 0.9651 | 0.0267 |    8080 B |        1.00 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteDoubles              | 1,000 double, write        |    947.9 ns |  11.04 ns |  8.62 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteDoublesUpstream      | 1,000 double, write        |  3,878.7 ns |  47.88 ns | 37.38 ns |  4.09 |    0.05 |      - |      - |         - |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadInts                  | 100 int32, read            |    328.5 ns |   4.34 ns |  3.39 ns |  1.00 |    0.01 | 0.0572 |      - |     480 B |        1.00 |
| ReadIntsUpstream          | 100 int32, read            |    274.3 ns |   2.07 ns |  1.50 ns |  0.84 |    0.01 | 0.0572 |      - |     480 B |        1.00 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteInts                 | 100 int32, write           |    137.6 ns |   0.89 ns |  0.64 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntsUpstream         | 100 int32, write           |    191.4 ns |   1.62 ns |  1.26 ns |  1.39 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadItems                 | 100 structs, read          |  2,010.9 ns |  20.13 ns | 15.71 ns |  1.00 |    0.01 | 1.0605 | 0.0305 |    8880 B |        1.00 |
| ReadItemsUpstream         | 100 structs, read          |  2,549.7 ns |  27.71 ns | 21.64 ns |  1.27 |    0.01 | 1.0605 | 0.0305 |    8880 B |        1.00 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteItems                | 100 structs, write         |  1,016.6 ns |  10.55 ns |  7.63 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteItemsUpstream        | 100 structs, write         |  1,596.2 ns |   4.32 ns |  2.85 ns |  1.57 |    0.01 |      - |      - |         - |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadBig                   | 4 KB order, read           |  6,053.3 ns |  71.92 ns | 56.15 ns |  1.00 |    0.01 | 3.0060 | 0.2365 |   25192 B |        1.00 |
| ReadBigUpstream           | 4 KB order, read           | 10,034.6 ns | 108.06 ns | 78.14 ns |  1.66 |    0.02 | 2.9755 | 0.2289 |   24920 B |        0.99 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteBig                  | 4 KB order, write          |  3,765.5 ns |  53.53 ns | 41.79 ns |  1.00 |    0.02 |      - |      - |         - |          NA |
| WriteBigUpstream          | 4 KB order, write          |  5,583.2 ns |  47.24 ns | 36.88 ns |  1.48 |    0.02 | 0.1831 |      - |    1560 B |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadOrder                 | Order, read                |    447.2 ns |   1.95 ns |  1.52 ns |  1.00 |    0.00 | 0.2904 | 0.0024 |    2432 B |        1.00 |
| ReadOrderUpstream         | Order, read                |    600.8 ns |   5.29 ns |  4.13 ns |  1.34 |    0.01 | 0.2880 | 0.0019 |    2416 B |        0.99 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteOrder                | Order, write               |    267.4 ns |   2.44 ns |  1.90 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteOrderUpstream        | Order, write               |    500.0 ns |   4.02 ns |  2.91 ns |  1.87 |    0.02 | 0.0181 |      - |     152 B |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadIntDoubleMap          | map<int32, double>, read   |    439.8 ns |   2.82 ns |  2.20 ns |  1.00 |    0.01 | 0.3767 | 0.0043 |    3152 B |        1.00 |
| ReadIntDoubleMapUpstream  | map<int32, double>, read   |  1,767.1 ns |  17.65 ns | 13.78 ns |  4.02 |    0.04 | 1.2207 | 0.0191 |   10216 B |        3.24 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteIntDoubleMap         | map<int32, double>, write  |    226.8 ns |   1.76 ns |  1.38 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteIntDoubleMapUpstream | map<int32, double>, write  |    900.8 ns |   4.84 ns |  3.50 ns |  3.97 |    0.03 | 0.0067 |      - |      56 B |          NA |
|                           |                            |             |           |          |       |         |        |        |           |             |
| ReadStringMap             | map<string, string>, read  |  2,596.4 ns |  22.36 ns | 17.46 ns |  1.00 |    0.01 | 1.3199 | 0.0496 |   11072 B |        1.00 |
| ReadStringMapUpstream     | map<string, string>, read  |  3,309.9 ns |  25.68 ns | 20.05 ns |  1.27 |    0.01 | 2.1667 | 0.1068 |   18136 B |        1.64 |
|                           |                            |             |           |          |       |         |        |        |           |             |
| WriteStringMap            | map<string, string>, write |  1,094.9 ns |   7.67 ns |  5.99 ns |  1.00 |    0.01 |      - |      - |         - |          NA |
| WriteStringMapUpstream    | map<string, string>, write |  1,964.9 ns |  15.72 ns | 12.27 ns |  1.79 |    0.01 | 0.0038 |      - |      56 B |          NA |
