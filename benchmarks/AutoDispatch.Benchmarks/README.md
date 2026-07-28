# AutoDispatch Benchmarks

BenchmarkDotNet comparison of per-call dispatch overhead between AutoDispatch's generated
`IDispatcher` and MediatR's `IMediator`, both resolving a trivial handler that adds one to an int.

Run it yourself:

```bash
cd benchmarks/AutoDispatch.Benchmarks
dotnet run -c Release
```

## Results

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8875)
AMD Ryzen 9 5900X, 1 CPU, 24 logical and 12 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 9.0.18 (9.0.1826.31522), X64 RyuJIT AVX2
  DefaultJob : .NET 9.0.18 (9.0.1826.31522), X64 RyuJIT AVX2
```

| Method                 | Mean     | Error    | StdDev    | Ratio | Gen0   | Allocated | Alloc Ratio |
|----------------------- |---------:|---------:|----------:|------:|-------:|----------:|------------:|
| AutoDispatch_SendAsync | 23.42 ns | 0.843 ns |  2.485 ns |  1.00 | 0.0057 |      96 B |        1.00 |
| MediatR_Send           | 89.13 ns | 3.548 ns | 10.461 ns |  3.85 | 0.0172 |     288 B |        3.00 |

**AutoDispatch is ~3.8x faster and allocates 3x less per dispatch** than MediatR for the same
command/handler shape, because there's no reflection-based handler lookup or runtime-built
pipeline — the entire call is a direct, compile-time-generated method call.

These numbers are for a single no-op handler with no pipeline behaviors — the relative gap will
vary with handler complexity, DI container size, and behavior chain depth, but the fixed
per-dispatch overhead that AutoDispatch removes (reflection + runtime behavior chain
construction) stays constant regardless of what your handler does.
