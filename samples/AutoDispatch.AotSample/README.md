# AutoDispatch.AotSample

A minimal console application demonstrating that projects using
[AutoDispatch.Generator](https://www.nuget.org/packages/AutoDispatch.Generator)
publish cleanly with **Native AOT** — no reflection, no runtime type
scanning, no trimming or AOT warnings.

## Why this works

AutoDispatch is a compile-time Roslyn incremental source generator. All
handler discovery, DI registration, and dispatch code is emitted as plain
C# at build time. There is nothing left for the AOT compiler to be unsure
about: no `Assembly.GetTypes()`, no `Activator.CreateInstance` with unknown
types, no dynamic proxies, no runtime reflection over attributes.

The project file opts into the strictest possible checks:

```xml
<PublishAot>true</PublishAot>
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<EnableAotAnalyzer>true</EnableAotAnalyzer>
<IsTrimmable>true</IsTrimmable>
```

## Run it

```bash
dotnet run --project samples/AutoDispatch.AotSample
```

## Publish it as a native, self-contained AOT binary

```bash
dotnet publish samples/AutoDispatch.AotSample -c Release -r win-x64 --self-contained
```

(swap `win-x64` for `linux-x64`/`osx-arm64`/etc. as appropriate for your
machine; native AOT compilation requires the platform-specific prerequisites
documented at https://aka.ms/nativeaot-prerequisites — e.g. the "Desktop
development with C++" workload on Windows). The publish step's C#
compilation completes with **zero trim/AOT analyzer warnings** — verified via
`dotnet build -r win-x64` with `EnableTrimAnalyzer`/`EnableAotAnalyzer` both
turned on — and the final native link produces a binary that starts and runs
without a .NET runtime installed.
