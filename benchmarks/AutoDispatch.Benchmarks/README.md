# AutoDispatch Benchmarks

BenchmarkDotNet comparison of per-call dispatch overhead between AutoDispatch's generated
`IDispatcher` and MediatR's `IMediator`: `Send`/`SendAsync` against a trivial handler that adds
one to an int, `Publish`/`PublishAsync` fanning a notification out to two no-op handlers, and
`CreateStream`/`StreamAsync` fully enumerating a 10-item stream.

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
| AutoDispatch_SendAsync    |  16.36 ns | 0.345 ns | 0.369 ns |  1.00 | 0.0057 |      96 B |        1.00 |
| MediatR_Send              |  69.66 ns | 1.420 ns | 2.667 ns |  4.26 | 0.0172 |     288 B |        3.00 |
| AutoDispatch_PublishAsync |  30.13 ns | 0.432 ns | 0.404 ns |  1.84 | 0.0014 |      24 B |        0.25 |
| MediatR_Publish           | 106.47 ns | 2.116 ns | 2.825 ns |  6.51 | 0.0277 |     464 B |        4.83 |
| AutoDispatch_StreamAsync  | 172.46 ns | 1.878 ns | 1.757 ns | 10.54 | 0.0086 |     144 B |        1.50 |
| MediatR_CreateStream      | 435.03 ns | 8.723 ns | 8.159 ns | 26.60 | 0.0319 |     536 B |        5.58 |

**`SendAsync` is ~4x faster and allocates 3x less than MediatR's `Send`.** **`PublishAsync` fanning
out to two handlers is ~3.5x faster than MediatR's `Publish` for the same two handlers, and
allocates ~19x less** (24 B vs 464 B) — MediatR's `Publish` allocates a `Task[]`/enumerator per
call even for a fixed, known set of handlers, while AutoDispatch's generated `PublishAsync` is a
straight-line sequence of `await` calls with no allocation beyond the notification object itself.
**`StreamAsync` fully enumerating a 10-item stream is ~2.5x faster and allocates ~3.7x less than
MediatR's `CreateStream`** — AutoDispatch's generated `StreamAsync` delegates directly to the
handler's `IAsyncEnumerable<T>` with no wrapping enumerator, while MediatR resolves the handler
via its runtime pipeline before invoking it.

These numbers are for a single no-op handler (and two no-op notification handlers, and a 10-item
no-op stream) with no pipeline behaviors — the relative gap will vary with handler complexity, DI
container size, and behavior chain depth, but the fixed per-dispatch overhead that AutoDispatch
removes (reflection, runtime handler lookup, and runtime-built behavior/publisher/stream chains)
stays constant regardless of what your handlers do.
