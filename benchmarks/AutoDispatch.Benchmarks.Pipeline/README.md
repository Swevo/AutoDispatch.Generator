# AutoDispatch Full-Pipeline Benchmarks

BenchmarkDotNet comparison of AutoDispatch vs MediatR when a command actually goes through a
**full pipeline**: two pipeline behaviors, a pre-processor, and a post-processor wrapping a
trivial handler. This isolates the overhead of the pieces added in v1.9.0-v1.12.0 (parallel
publish, exception middleware, pre/post-processors) rather than the bare no-op dispatch measured
in `benchmarks/AutoDispatch.Benchmarks`.

This lives in its own project/compilation because AutoDispatch's `[Behavior]`, `[PreProcessor]`,
and `[PostProcessor]` attributes apply to **every** command in the compilation they're declared
in (there's no per-command opt-in yet) — keeping this in a separate project avoids contaminating
the bare-dispatch baseline numbers published in the main benchmarks README.

Run it yourself:

```bash
cd benchmarks/AutoDispatch.Benchmarks.Pipeline
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

| Method                               | Mean     | Error   | StdDev  | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------------------- |---------:|--------:|--------:|------:|--------:|-------:|----------:|------------:|
| AutoDispatch_SendAsync_FullPipeline   | 173.9 ns | 3.29 ns | 3.79 ns |  1.00 |    0.03 | 0.0257 |     432 B |        1.00 |
| MediatR_Send_FullPipeline             | 313.9 ns | 6.16 ns | 8.01 ns |  1.81 |    0.06 | 0.0763 |    1280 B |        2.96 |

**Even with two behaviors plus a pre- and post-processor, AutoDispatch's generated `SendAsync` is
~1.8x faster than MediatR's equivalent runtime-built pipeline (`AddOpenBehavior` +
`RequestPreProcessorBehavior<,>`/`RequestPostProcessorBehavior<,>`) and allocates ~3x less** (432
B vs 1280 B). The gap versus the bare no-op benchmark (~4.3x for `SendAsync` alone) narrows
because the no-op behaviors/processors themselves cost the same on both sides — what AutoDispatch
removes is purely the per-call reflection and delegate-chain construction that MediatR's
`ServiceFactory`-based pipeline still pays for on every dispatch, even for statically-known
open-generic behaviors registered once at startup.
