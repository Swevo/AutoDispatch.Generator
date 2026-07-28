---
title: "MediatR without the Reflection Tax: Introducing AutoDispatch.Generator"
published: false
tags: dotnet, csharp, sourcegenerators, cqrs
---

If you've built more than a couple of .NET APIs in the last few years, chances are you've reached
for [MediatR](https://github.com/jbogard/MediatR). It's a great pattern — commands, queries,
handlers, pipeline behaviors — but it comes with a tax you rarely think about until you profile
your app: reflection-based handler resolution, `IPipelineBehavior<,>` chains built at runtime, and
a startup scan of your entire assembly.

**AutoDispatch.Generator** gives you the exact same mental model — command, handler, pipeline
behavior — but everything is generated at *compile time* by a Roslyn incremental source generator.
No `IRequest<T>`, no reflection, no runtime dispatch table. Just a strongly-typed `IDispatcher`
that's built the moment you compile.

## Before

```csharp
using MediatR;

public sealed record CreateOrderCommand(string CustomerId) : IRequest<OrderId>;

public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, OrderId>
{
    public Task<OrderId> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        // ...
    }
}
```

## After

```csharp
using AutoDispatch;

public sealed record CreateOrderCommand(string CustomerId);

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct = default)
    {
        // ...
    }
}
```

That's it. No marker interface on your command. `[Handler]` is all AutoDispatch needs to find your
handler and wire it into a generated `IDispatcher`:

```csharp
builder.Services.AddAutoDispatch();

app.MapPost("/orders", async (CreateOrderCommand command, IDispatcher dispatcher, CancellationToken ct) =>
{
    var orderId = await dispatcher.SendAsync(command, ct);
    return Results.Ok(orderId);
});
```

## Pipeline behaviors, without the runtime chain-building

MediatR's `IPipelineBehavior<,>` is resolved and chained together at runtime, for every request.
AutoDispatch generates the entire chain at compile time:

```csharp
[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, Func<Task<TResult>> next, CancellationToken ct = default)
    {
        _logger.LogInformation("→ {Command}", typeof(TCommand).Name);
        var result = await next();
        _logger.LogInformation("← {Command}", typeof(TCommand).Name);
        return result;
    }
}
```

The generated dispatcher even documents the resulting execution order right above the method:

```csharp
// Pipeline: LoggingBehavior -> ValidationBehavior -> CreateOrderHandler.HandleAsync -> LoggingBehavior -> ValidationBehavior
public Task<OrderId> SendAsync(CreateOrderCommand command, CancellationToken ct = default)
{
    ...
}
```

## Compile-time safety you don't get from MediatR

Because everything is resolved at compile time, AutoDispatch can catch mistakes MediatR only
surfaces at runtime — or not at all:

- **AD001** — a `[Handler]` class with no `Handle`/`HandleAsync` method (dead code, warning)
- **AD002** — two handlers registered for the same command (error, not a silent last-registration-wins)
- **AD003** — an async handler missing `CancellationToken` (warning, with a one-click IDE fix)
- **AD004–AD006** — malformed `[Behavior]` classes, caught before your app ever runs

## AOT-friendly by construction

Because there's no reflection and no runtime service scanning, AutoDispatch works out of the box
with Native AOT and trimming — no extra configuration, no `[DynamicallyAccessedMembers]` annotations.

## Migrating from MediatR

The [README](https://github.com/Swevo/AutoDispatch.Generator#migrating-from-mediatr) has a full
step-by-step migration guide, and it's mechanical enough that most of it can be automated with a
Copilot agent. Removing the marker interfaces and swapping `IRequestHandler<,>` for `[Handler]` is
usually a find-and-replace away.

## Try it

```bash
dotnet add package AutoDispatch.Generator
```

Companion packages:

- `AutoDispatch.Testing` — a `FakeServiceProvider` and `PipelineTestHarness` for unit testing
  handlers and behaviors without spinning up a DI container.
- `AutoDispatch.Templates` — `dotnet new install AutoDispatch.Templates` then
  `dotnet new autodispatch-handler -n CreateOrder` scaffolds a command + handler pair.

Repo: <https://github.com/Swevo/AutoDispatch.Generator>

If you're tired of paying a reflection tax for a pattern that's fundamentally just "route this
object to that method," give it a try and let me know what breaks.
