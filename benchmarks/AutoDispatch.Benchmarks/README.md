# AutoDispatch Benchmarks

BenchmarkDotNet comparison of per-call dispatch overhead between AutoDispatch's generated
`IDispatcher` and MediatR's `IMediator`: `Send`/`SendAsync` against a trivial handler that adds
one to an int, and `Publish`/`PublishAsync` fanning a notification out to two no-op handlers.

Run it yourself:

```bash
cd benchmarks/AutoDispatch.Benchmarks
dotnet run -c Release
```

## Results

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9445)
Unknown processor
.NET SDK 10.0.400
  [Host]     : .NET 9.0.20 (9.0.2026.41315), X64 RyuJIT AVX2
  DefaultJob : .NET 9.0.20 (9.0.2026.41315), X64 RyuJIT AVX2
```

| Method                    | Mean      | Error    | StdDev   | Ratio | Gen0   | Allocated | Alloc Ratio |
|-------------------------- |----------:|---------:|---------:|------:|-------:|----------:|------------:|
| AutoDispatch_SendAsync    |  17.31 ns | 0.364 ns | 0.341 ns |  1.00 | 0.0057 |      96 B |        1.00 |
| MediatR_Send              |  68.59 ns | 1.396 ns | 1.371 ns |  3.96 | 0.0172 |     288 B |        3.00 |
| AutoDispatch_PublishAsync |  29.94 ns | 0.591 ns | 0.553 ns |  1.73 | 0.0014 |      24 B |        0.25 |
| MediatR_Publish           | 115.25 ns | 2.325 ns | 6.325 ns |  6.66 | 0.0277 |     464 B |        4.83 |

**`SendAsync` is ~4x faster and allocates 3x less than MediatR's `Send`.** **`PublishAsync` fanning
out to two handlers is ~3.9x faster than MediatR's `Publish` for the same two handlers, and
allocates ~19x less** (24 B vs 464 B) — MediatR's `Publish` allocates a `Task[]`/enumerator per
call even for a fixed, known set of handlers, while AutoDispatch's generated `PublishAsync` is a
straight-line sequence of `await` calls with no allocation beyond the notification object itself.

These numbers are for a single no-op handler (and two no-op notification handlers) with no
pipeline behaviors — the relative gap will vary with handler complexity, DI container size, and
behavior chain depth, but the fixed per-dispatch overhead that AutoDispatch removes (reflection,
runtime handler lookup, and runtime-built behavior/publisher chains) stays constant regardless of
what your handlers do.
