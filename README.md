# AutoDispatch.Generator

[![NuGet](https://img.shields.io/nuget/v/AutoDispatch.Generator.svg)](https://www.nuget.org/packages/AutoDispatch.Generator)
[![NuGet Downloads](https://img.shields.io/nuget/dt/AutoDispatch.Generator.svg)](https://www.nuget.org/packages/AutoDispatch.Generator)
[![CI](https://github.com/Swevo/AutoDispatch.Generator/actions/workflows/build.yml/badge.svg)](https://github.com/Swevo/AutoDispatch.Generator/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10 Ready](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](#)

AutoDispatch gives you the **MediatR-style handler pattern** without `IRequest<T>`, `IRequestHandler<,>`, reflection, or runtime dispatch overhead. Mark a handler with `[Handler]`, write `Handle` or `HandleAsync`, and the generator emits a strongly-typed dispatcher at build time.

## Why AutoDispatch?

- **Same mental model as MediatR** — command/query + handler + dispatcher
- **Zero reflection** — direct generated calls, no runtime dispatch overhead
- **Pipeline behaviors** — `[Behavior(Order = N)]` wraps all async handlers at compile time, and `[StreamBehavior(Order = N)]` wraps streaming queries the same way; no `IPipelineBehavior<,>` magic at runtime
- **Configurable notification fan-out** — `PublishAsync` runs handlers sequentially by default, or mark a notification `[ParallelPublish]` for `Task.WhenAll` concurrency
- **Exception handling middleware** — `[ExceptionHandler]` open generics intercept a typed exception thrown by a handler or pipeline behavior and can supply a fallback response, matching MediatR's `IRequestExceptionHandler<,,>`
- **Exception actions** — `[ExceptionAction]` open generics always run as side-effect-only observers on a typed exception (logging, metrics, alerting) without suppressing it, matching MediatR's `IRequestExceptionAction<,>`
- **Request pre/post-processors** — `[PreProcessor]`/`[PostProcessor]` open generics run unconditionally right before/after a handler executes, without writing a full `next()`-calling pipeline behavior, matching MediatR's `IRequestPreProcessor<>`/`IRequestPostProcessor<,>`
- **Constrained (scoped) behaviors** — add a generic constraint (e.g. `where TCommand : IAudited`) to any `[Behavior]`/`[PreProcessor]`/`[PostProcessor]`/`[StreamBehavior]` to apply it only to matching commands, instead of every command in the compilation
- **Built-in OpenTelemetry-compatible tracing** — opt in with `AddAutoDispatch(o => o.EnableTracing = true)` to wrap every `SendAsync`/`PublishAsync`/`StreamAsync` call in an `Activity`, with zero overhead when no listener is subscribed
- **No marker interfaces** — commands stay as plain POCOs
- **AOT-friendly** — everything is compile-time generated
- **DI-ready** — `AddAutoDispatch()` wires up handlers, behaviors, and `IDispatcher`

## Installation

```bash
dotnet add package AutoDispatch.Generator
```

Then register the generated dispatcher:

```csharp
builder.Services.AddAutoDispatch();
```

## Before vs After

### MediatR-style boilerplate

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

### AutoDispatch

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

## What gets generated

Given one or more `[Handler]` classes, AutoDispatch emits:

1. `AutoDispatch.HandlerAttribute`
2. `AutoDispatch.IDispatcher`
3. `AutoDispatch.Dispatcher`
4. `AddAutoDispatch()` for `IServiceCollection`

Example generated dispatcher:

```csharp
#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace AutoDispatch
{
    public interface IDispatcher
    {
        Task<OrderId> SendAsync(CreateOrderCommand command, CancellationToken ct = default);
        void Send(DeleteOrderCommand command);
    }

    internal sealed class Dispatcher : IDispatcher
    {
        private readonly IServiceProvider _sp;

        public Dispatcher(IServiceProvider sp) => _sp = sp;

        public Task<OrderId> SendAsync(CreateOrderCommand command, CancellationToken ct = default)
            => _sp.GetRequiredService<CreateOrderHandler>().HandleAsync(command, ct);

        public void Send(DeleteOrderCommand command)
            => _sp.GetRequiredService<DeleteOrderHandler>().Handle(command);
    }
}
```

## Conventions

AutoDispatch discovers **public instance non-static** methods on classes marked with `[Handler]`.

Supported signatures:

| Handler method | Generated dispatcher method |
|---|---|
| `T Handle(TCommand cmd)` | `T Send(TCommand command)` |
| `void Handle(TCommand cmd)` | `void Send(TCommand command)` |
| `Task HandleAsync(TCommand cmd, CancellationToken ct = default)` | `Task SendAsync(TCommand command, CancellationToken ct = default)` |
| `Task<T> HandleAsync(TCommand cmd, CancellationToken ct = default)` | `Task<T> SendAsync(TCommand command, CancellationToken ct = default)` |
| `Task HandleAsync(TCommand cmd)` | `Task SendAsync(TCommand command, CancellationToken ct = default)` |
| `Task<T> HandleAsync(TCommand cmd)` | `Task<T> SendAsync(TCommand command, CancellationToken ct = default)` |

Rules:

- Only methods named exactly `Handle` or `HandleAsync`
- `Handle` must have exactly one command parameter
- `HandleAsync` may have one command parameter, or a second `CancellationToken`
- Methods with zero parameters or more than two parameters are ignored
- `Dispatcher` is generated as `internal sealed`
- `AddAutoDispatch()` registers handlers with `AddScoped`

## Semantic aliases

`[CommandHandler]` and `[QueryHandler]` are aliases for `[Handler]` — use whichever reads best in your codebase.

```csharp
[CommandHandler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct = default)
        => Task.FromResult(new OrderId(Guid.NewGuid()));
}

[QueryHandler]
public sealed class GetOrderHandler
{
    public Task<Order?> HandleAsync(GetOrderQuery query, CancellationToken ct = default)
        => Task.FromResult<Order?>(null);
}
```

All three attributes are equivalent — the generated code is identical.

## Usage

```csharp
using AutoDispatch;

public sealed record CreateOrderCommand(string CustomerId);
public sealed record DeleteOrderCommand(Guid OrderId);
public sealed record OrderId(Guid Value);

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct = default)
        => Task.FromResult(new OrderId(Guid.NewGuid()));
}

[Handler]
public sealed class DeleteOrderHandler
{
    public void Handle(DeleteOrderCommand command)
    {
    }
}
```

Then consume the generated dispatcher:

```csharp
app.MapPost("/orders", async (CreateOrderCommand command, AutoDispatch.IDispatcher dispatcher, CancellationToken ct) =>
{
    var orderId = await dispatcher.SendAsync(command, ct);
    return Results.Ok(orderId);
});
```

## Generated DI registration

```csharp
builder.Services.AddAutoDispatch();
```

Produces code like:

```csharp
services.AddScoped<CreateOrderHandler>();
services.AddScoped<DeleteOrderHandler>();
services.AddScoped<AutoDispatch.IDispatcher, AutoDispatch.Dispatcher>();
```

## Tracing (OpenTelemetry-compatible)

Every command/notification/stream dispatch can be wrapped in a `System.Diagnostics.Activity` from a generated `"AutoDispatch"` `ActivitySource`, without adding any dependency on OpenTelemetry itself:

```csharp
builder.Services.AddAutoDispatch(o => o.EnableTracing = true);
```

Then subscribe from your OpenTelemetry SDK setup as you would any other `ActivitySource`:

```csharp
builder.Services.AddOpenTelemetry().WithTracing(tracing =>
    tracing.AddSource(AutoDispatch.AutoDispatchTelemetry.ActivitySourceName));
```

With tracing enabled, `IDispatcher` resolves to a generated `TracingDispatcher` decorator that:

- Starts one `Activity` per `SendAsync`/`PublishAsync`/`StreamAsync` call (named `AutoDispatch.SendAsync`, `AutoDispatch.PublishAsync`, `AutoDispatch.StreamAsync`), tagged with the short command/notification/query type name
- Sets `ActivityStatusCode.Error` and an `error.type` tag if the call throws, then rethrows unchanged — tracing never changes behavior or swallows exceptions
- For streams, keeps the `Activity` open for the whole enumeration and records an error status if any `MoveNextAsync()` call throws

Tracing is **opt-in and pay-for-play**: `EnableTracing` defaults to `false`, so the plain `Dispatcher` is registered and there is no decorator, no extra virtual call, and no `Activity` allocation unless you turn it on. Even when enabled, if nothing is listening to the `"AutoDispatch"` source, `ActivitySource.StartActivity(...)` returns `null` and every `activity?.` call below is a no-op — the cost is one extra method call on the hot path, not a full tracing pipeline.

## Pipeline behaviors

`[Behavior(Order = N)]` wraps all async handlers in a compile-time pipeline. Identical mental model to MediatR's `IPipelineBehavior<,>` — but the chain is emitted as generated code, not resolved via reflection at runtime.

Behavior requirements:

- The behavior class must be **public**, **non-abstract**, and open-generic with exactly two type parameters
- It must implement `IPipelineBehavior<TCommand, TResult>`
- It must expose `public Task<TResult> HandleAsync(TCommand command, Func<Task<TResult>> next, CancellationToken ct = default)`

### Define a behavior

```csharp
using AutoDispatch;

[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCommand, TResult>
    : IPipelineBehavior<TCommand, TResult>
{
    private readonly ILogger<LoggingBehavior<TCommand, TResult>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TCommand, TResult>> logger)
        => _logger = logger;

    public async Task<TResult> HandleAsync(
        TCommand command,
        Func<Task<TResult>> next,
        CancellationToken ct = default)
    {
        _logger.LogInformation("→ {Command}", typeof(TCommand).Name);
        var result = await next();
        _logger.LogInformation("← {Command}", typeof(TCommand).Name);
        return result;
    }
}
```

That's all. `AddAutoDispatch()` registers it automatically.

### Multiple behaviors

```csharp
[Behavior(Order = 0)]  // runs first (outermost)
public sealed class LoggingBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult> { ... }

[Behavior(Order = 1)]  // runs second
public sealed class ValidationBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult> { ... }

[Behavior(Order = 2)]  // runs last (innermost, just before the handler)
public sealed class TimingBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult> { ... }
```

Execution order: Logging → Validation → Timing → Handler → Timing → Validation → Logging.

When multiple behaviors have the same `Order`, AutoDispatch preserves declaration order.

### What gets generated

For `Task<OrderId> SendAsync(CreateOrderCommand)` with two behaviors:

```csharp
// Generated dispatcher method:
public Task<OrderId> SendAsync(CreateOrderCommand command, CancellationToken ct = default)
{
    Func<Task<OrderId>> pipeline =
        () => _sp.GetRequiredService<CreateOrderHandler>().HandleAsync(command, ct);
    var _b1 = _sp.GetRequiredService<TimingBehavior<CreateOrderCommand, OrderId>>();
    var _p1 = pipeline;
    pipeline = () => _b1.HandleAsync(command, _p1, ct);
    var _b0 = _sp.GetRequiredService<LoggingBehavior<CreateOrderCommand, OrderId>>();
    var _p0 = pipeline;
    pipeline = () => _b0.HandleAsync(command, _p0, ct);
    return pipeline();
}
```

### Behaviors and void-async handlers

For `Task` (no result) handlers, the generator wraps the call in `Task<Unit>` internally. `Unit` is emitted by the generator — you never reference it directly; the method signature stays `Task SendAsync(...)`.

Behaviors can also short-circuit by returning a result without calling `next()`.

### Behaviors only apply to async handlers

Sync `T Send(...)` and `void Send(...)` methods are not wrapped. Add a pipeline when you migrate a sync handler to async, or keep it sync for zero overhead.

### Constrained (scoped) behaviors

By default a `[Behavior]` (and `[PreProcessor]`/`[PostProcessor]`/`[StreamBehavior]`) applies to
**every** command in the compilation. Add a generic constraint to the `TCommand` type parameter
to scope it to only the commands that satisfy it — matching how MediatR users constrain a
registered `IPipelineBehavior<,>` to a subset of requests:

```csharp
public interface IAudited { }

public sealed record CreateOrderCommand : IAudited { ... }   // audited
public sealed record PingCommand { ... }                     // not audited

[Behavior(Order = 0)]
public sealed class AuditBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
    where TCommand : IAudited
{
    public Task<TResult> HandleAsync(TCommand command, Func<Task<TResult>> next, CancellationToken ct = default)
    {
        // log an audit entry for `command` ...
        return next();
    }
}
```

`AuditBehavior` is only woven into `CreateOrderCommand`'s generated `SendAsync` — `PingCommand`
falls back to its normal dispatch (simple expression-bodied if no other pipeline steps apply)
with no `AuditBehavior` reference at all, so it never pays for a pipeline it doesn't use.
Constraint checking supports named interface/base-class constraints (the common case — marker
interfaces like `IAudited`); a generic constraint type (e.g. `IMarker<T>`) isn't resolved yet and
is treated as always-satisfied rather than silently dropping the behavior. If a constraint never
matches any registered command/query at all — typically a typo — AutoDispatch reports `AD027` so
the mistake doesn't fail silently.

## Notifications (publish/subscribe)

`[Handler]` gives you MediatR's `Send` (exactly one handler per command). `[NotificationHandler]` gives you the other half — MediatR's `Publish`: **any number of handlers** may subscribe to the same notification type, and every one of them runs when you publish it.

```csharp
using AutoDispatch;

public sealed record OrderCreated(Guid OrderId);

[NotificationHandler]
public sealed class SendConfirmationEmail
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        // ...
        return Task.CompletedTask;
    }
}

[NotificationHandler]
public sealed class UpdateAnalytics
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        // ...
        return Task.CompletedTask;
    }
}
```

`AddAutoDispatch()` registers both handlers automatically, and `IDispatcher` gains a matching `PublishAsync` overload:

```csharp
await dispatcher.PublishAsync(new OrderCreated(orderId), ct);
// runs SendConfirmationEmail.HandleAsync, then UpdateAnalytics.HandleAsync
```

### Conventions

- Only `HandleAsync(TNotification notification, CancellationToken ct = default)` is supported — notification handlers publish, they don't return a result, so plain `Handle` and `Task<T>`-returning methods are ignored
- Unlike `[Handler]`, **multiple** `[NotificationHandler]` classes may handle the same notification type — there is no AD002-style "duplicate handler" error
- By default, handlers run **sequentially**, in deterministic order (by handler type name), awaiting each one before starting the next — matching MediatR's default `ForeachAwaitPublisher` behavior. If a handler throws, remaining handlers for that publish call do not run
- Mark the **notification type itself** `[ParallelPublish]` to switch that notification to **concurrent** fan-out via `Task.WhenAll` instead — matching MediatR's opt-in `TaskWhenAllPublisher`. All handlers start immediately and are awaited together; every handler runs even if another one throws (a synchronous throw is safely converted to a faulted task so it doesn't skip the rest), and failures surface once every handler has finished
- `[NotificationHandler(Lifetime = HandlerLifetime.Singleton)]` (or `Transient`) works the same way as it does on `[Handler]`
- Pipeline `[Behavior]`s apply only to command/query dispatch (`Send`/`SendAsync`). To wrap `PublishAsync` itself, use [notification pipeline behaviors](#notification-pipeline-behaviors) instead

### Parallel publish

Only opt in when handlers for a notification are independent of one another and safe to run
concurrently (no shared mutable state, no ordering assumptions between handlers):

```csharp
using AutoDispatch;

[ParallelPublish]
public sealed record OrderCreated(Guid OrderId);

[NotificationHandler]
public sealed class SendConfirmationEmail
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => /* ... */ Task.CompletedTask;
}

[NotificationHandler]
public sealed class UpdateAnalytics
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => /* ... */ Task.CompletedTask;
}
```

```csharp
// SendConfirmationEmail and UpdateAnalytics now both start immediately and run concurrently
await dispatcher.PublishAsync(new OrderCreated(orderId), ct);
```

## Notification pipeline behaviors

`[Behavior]` wraps a single command's handler call. `[NotificationBehavior]` is its `Publish`-side
counterpart — it wraps the **entire fan-out** for a notification type (every subscribed handler,
whether sequential or `[ParallelPublish]`) in one `Func<Task>`-based pipeline. There is no MediatR
equivalent for this: MediatR's `IPipelineBehavior<,>` only wraps `Send`, never `Publish`.

```csharp
using AutoDispatch;

public sealed record OrderCreated(Guid OrderId);

[NotificationBehavior(Order = 0)]
public sealed class LoggingNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
{
    private readonly ILogger<LoggingNotificationBehavior<TNotification>> _logger;

    public LoggingNotificationBehavior(ILogger<LoggingNotificationBehavior<TNotification>> logger) => _logger = logger;

    public async Task HandleAsync(TNotification notification, Func<Task> next, CancellationToken ct = default)
    {
        _logger.LogInformation("Publishing {Notification}", typeof(TNotification).Name);
        await next();
        _logger.LogInformation("Published {Notification}", typeof(TNotification).Name);
    }
}
```

Every `PublishAsync(OrderCreated, ...)` call — sequential or `[ParallelPublish]` — now runs inside
this behavior. Register any number of them; like `[Behavior]`, they compose by `Order` (ties broken
by declaration order), and a behavior can call `next()` zero, one, or multiple times, or not at all
to short-circuit the entire publish.

### Conventions

- Must be a `public`, non-`abstract` open generic class with exactly **one** type parameter (`TNotification`)
- Must implement `INotificationPipelineBehavior<TNotification>` using its own type parameter (`AD029` otherwise)
- Must declare `public Task HandleAsync(TNotification notification, Func<Task> next, CancellationToken ct = default)` (`AD030` otherwise)
- Applies to **every** notification type in the compilation — there is currently no constrained/scoped variant (unlike `[Behavior]`); this may be added in a future release
- `AddAutoDispatch()` registers each `[NotificationBehavior]` type as an open generic, the same way `[Behavior]` is registered
- No codegen change to `PublishAsync` at all when no `[NotificationBehavior]` is registered

## Streaming queries

MediatR's `IStreamRequest<TResponse>` has no zero-reflection equivalent in most alternatives —
`[StreamHandler]` closes that gap. Mark a class `[StreamHandler]` with a public
`IAsyncEnumerable<TResult> HandleAsync(TQuery query, CancellationToken ct = default)` method, and
AutoDispatch generates a matching `StreamAsync` method on `IDispatcher` that returns the handler's
async stream directly — no buffering, no intermediate list, items are produced lazily as your
handler yields them.

```csharp
using AutoDispatch;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public sealed record GetOrdersQuery(string CustomerId);
public sealed record OrderSummary(string OrderId, decimal Total);

[StreamHandler]
public sealed class GetOrdersHandler
{
    public async IAsyncEnumerable<OrderSummary> HandleAsync(
        GetOrdersQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var order in _repository.StreamOrdersAsync(query.CustomerId, ct))
        {
            yield return new OrderSummary(order.Id, order.Total);
        }
    }
}
```

```csharp
await foreach (var summary in dispatcher.StreamAsync(new GetOrdersQuery(customerId), ct))
{
    // process each item as it arrives — no need to wait for the full result set
}
```

### Conventions

- Like `[Handler]`, streaming is request/response — **exactly one** `[StreamHandler]` is allowed per query type; a second handler for the same query type reports AD010, same spirit as AD002 for commands
- Only `HandleAsync(TQuery query, CancellationToken ct = default)` returning `IAsyncEnumerable<TResult>` is recognized; methods returning `Task`/`Task<T>` belong on a `[Handler]`, not a `[StreamHandler]`
- `AddAutoDispatch()` registers stream handlers the same way as command/notification handlers, honoring `[StreamHandler(Lifetime = ...)]`
- `[Behavior]` (the command pipeline) does not apply to `StreamAsync` — use `[StreamBehavior]` instead (below) to wrap streaming queries

## Stream pipeline behaviors

Streams get their own pipeline, matching MediatR's `IStreamPipelineBehavior<TRequest, TResponse>`.
Declare a public, open generic class with exactly two type parameters implementing
`IStreamPipelineBehavior<TQuery, TResult>`, and AutoDispatch wraps every generated `StreamAsync`
call with it — in `Order` order (ascending, outermost first), same ordering rules as `[Behavior]`.
Unlike command behaviors (which wrap a `Task<TResult>`), `next()` here returns
`IAsyncEnumerable<TResult>` directly, so a stream behavior is typically itself an async iterator
that forwards (or filters/transforms) items as they arrive:

```csharp
using AutoDispatch;
using System.Collections.Generic;

[StreamBehavior(Order = 0)]
public sealed class LoggingStreamBehavior<TQuery, TResult> : IStreamPipelineBehavior<TQuery, TResult>
{
    public async IAsyncEnumerable<TResult> HandleAsync(
        TQuery query,
        Func<IAsyncEnumerable<TResult>> next,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Streaming {Query}", query);
        await foreach (var item in next().WithCancellation(ct))
        {
            yield return item;
        }
    }
}
```

With no `[StreamBehavior]`s registered, `StreamAsync` delegates directly to the handler exactly as
before (no wrapping overhead). Once one or more are registered, AutoDispatch builds a lazy chain of
`Func<IAsyncEnumerable<TResult>>` calls — nothing runs until the caller actually enumerates the
result, matching the handler's own laziness.

## Exception handling

Matching MediatR's `IRequestExceptionHandler<TRequest, TResponse, TException>`, you can register
typed handlers that intercept an exception thrown by a command/query handler (or by any
`[Behavior]` in its pipeline) and either supply a fallback response or let it keep propagating.
Declare a public, open generic class with exactly two type parameters (`TCommand`, `TResult`)
implementing `IExceptionHandler<TCommand, TResult, TException>` for one **fixed, concrete**
exception type:

```csharp
using AutoDispatch;

public sealed class ValidationException : Exception { }

[ExceptionHandler(Order = 0)]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
    {
        _logger.LogWarning(exception, "Validation failed for {Command}", command);

        // Return a fallback response instead of letting the exception propagate:
        return Task.FromResult(ExceptionHandlerResult<TResult>.Handled(default!));

        // Or let it keep propagating (e.g. to the next applicable handler, or to the caller):
        // return Task.FromResult(ExceptionHandlerResult<TResult>.Unhandled());
    }
}
```

Conventions:
- With no `[ExceptionHandler]`s registered, dispatch codegen is byte-for-byte unchanged from
  earlier versions — no `try`/`catch`, no `async` overhead added.
- Once one or more are registered, every async command/query dispatch method (with or without
  `[Behavior]`s) is wrapped in a `try`/`catch` per distinct exception type.
- Multiple handlers may target unrelated or related exception types; catch clauses are always
  generated **most-derived exception type first** (so a handler for `Exception` never shadows one
  for `ValidationException`), then by `Order` (ascending), then by declaration order — this also
  matches how ordinary C# `catch` blocks must be ordered to compile.
- If a handler returns `Unhandled()`, the exception is rethrown so the next applicable handler (or
  the caller) sees it, exactly like MediatR's behavior when no handler sets `state.Handled`.

### Exception actions

Matching MediatR's `IRequestExceptionAction<TRequest, TException>`, you can also register
side-effect-only observers that **always** run when a matching exception is thrown — they cannot
suppress the exception or supply a fallback response, unlike `[ExceptionHandler]`. This is the
right tool for logging, metrics, or alerting that must fire regardless of whether some other
handler ultimately recovers. Declare a public, open generic class with exactly **one** type
parameter (`TCommand`) implementing `IExceptionAction<TCommand, TException>` for one fixed,
concrete exception type:

```csharp
using AutoDispatch;

[ExceptionAction(Order = 0)]
public sealed class LoggingExceptionAction<TCommand> : IExceptionAction<TCommand, ValidationException>
{
    public Task ExecuteAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
    {
        _logger.LogWarning(exception, "Validation failed for {Command}", command);
        return Task.CompletedTask;
    }
}
```

Conventions:
- Actions and handlers for the same exception type share the same generated `catch` block; within
  it, **all matching actions run first** (in `Order`), then the matching handlers run — mirroring
  MediatR's pipeline where `IRequestExceptionAction` always executes before
  `IRequestExceptionHandler` gets a chance to short-circuit.
- Actions run even when no `[ExceptionHandler]` is registered for the exception type at all — the
  exception is rethrown afterward via `throw;`, preserving the original stack trace.
- Catch-block ordering (most-derived exception type first) is computed across **both** actions and
  handlers together, so mixing the two for overlapping exception hierarchies still produces valid,
  correctly-ordered C#.

### Request pre/post-processors

Matching MediatR's `IRequestPreProcessor<TRequest>` and `IRequestPostProcessor<TRequest, TResponse>`,
you can register processors that always run immediately before or after a command/query handler
executes — without writing a full `[Behavior]` (which requires calling a `next()` delegate
yourself). Pre/post-processors sit as the **innermost** step of the pipeline, running directly
around the handler call, inside any custom `[Behavior]`s:

```csharp
using AutoDispatch;

[PreProcessor(Order = 0)]
public sealed class LoggingPreProcessor<TCommand> : IPreProcessor<TCommand>
{
    public Task ProcessAsync(TCommand command, CancellationToken ct = default)
    {
        _logger.LogInformation("Handling {Command}", command);
        return Task.CompletedTask;
    }
}

[PostProcessor(Order = 0)]
public sealed class LoggingPostProcessor<TCommand, TResult> : IPostProcessor<TCommand, TResult>
{
    public Task ProcessAsync(TCommand command, TResult response, CancellationToken ct = default)
    {
        _logger.LogInformation("Handled {Command} -> {Response}", command, response);
        return Task.CompletedTask;
    }
}
```

Conventions:
- `[PreProcessor]` is a public, open generic class with exactly one type parameter (`TCommand`)
  implementing `IPreProcessor<TCommand>`; `[PostProcessor]` has exactly two (`TCommand`, `TResult`)
  implementing `IPostProcessor<TCommand, TResult>` — both are fully open (unlike `[ExceptionHandler]`/
  `[ExceptionAction]`, there's no fixed exception type to validate).
- All matching pre-processors run first (in `Order`), then the handler, then all matching
  post-processors (in `Order`, receiving the handler's response) — for void-async handlers the
  response is `Unit.Value`.
- With no `[PreProcessor]`/`[PostProcessor]`s registered, dispatch codegen is unchanged.

## Diagnostics

| Code | Severity | Description |
|---|---|---|
| AD001 | Warning | `[Handler]` on a class with no valid `Handle`/`HandleAsync` methods |
| AD002 | Error | Duplicate handlers discovered for the same command type |
| AD003 | Warning | `HandleAsync` does not accept `CancellationToken` |
| AD004 | Error | `[Behavior]` type is not a public, non-abstract open generic class with exactly two type parameters |
| AD005 | Error | `[Behavior]` type does not implement `IPipelineBehavior<TCommand, TResult>` |
| AD006 | Error | `[Behavior]` type does not expose a valid public `HandleAsync` method |
| AD007 | Warning | `[NotificationHandler]` on a class with no valid `HandleAsync(TNotification, CancellationToken)` method |
| AD008 | Warning | Notification `HandleAsync` does not accept `CancellationToken` |
| AD009 | Warning | `[StreamHandler]` on a class with no valid `HandleAsync(TQuery, CancellationToken)` method returning `IAsyncEnumerable<TResult>` |
| AD010 | Error | Duplicate stream handlers discovered for the same query type |
| AD011 | Warning | Stream `HandleAsync` does not accept `CancellationToken` |
| AD012 | Error | `[StreamBehavior]` type is not a public, non-abstract open generic class with exactly two type parameters |
| AD013 | Error | `[StreamBehavior]` type does not implement `IStreamPipelineBehavior<TQuery, TResult>` |
| AD014 | Error | `[StreamBehavior]` type does not expose a valid public `HandleAsync` method |
| AD015 | Error | `[ExceptionHandler]` type is not a public, non-abstract open generic class with exactly two type parameters |
| AD016 | Error | `[ExceptionHandler]` type does not implement `IExceptionHandler<TCommand, TResult, TException>` for a fixed exception type |
| AD017 | Error | `[ExceptionHandler]` type does not expose a valid public `HandleAsync` method |
| AD018 | Error | `[ExceptionAction]` type is not a public, non-abstract open generic class with exactly one type parameter |
| AD019 | Error | `[ExceptionAction]` type does not implement `IExceptionAction<TCommand, TException>` for a fixed exception type |
| AD020 | Error | `[ExceptionAction]` type does not expose a valid public `ExecuteAsync` method |
| AD021 | Error | `[PreProcessor]` type is not a public, non-abstract open generic class with exactly one type parameter |
| AD022 | Error | `[PreProcessor]` type does not implement `IPreProcessor<TCommand>` |
| AD023 | Error | `[PreProcessor]` type does not expose a valid public `ProcessAsync` method |
| AD024 | Error | `[PostProcessor]` type is not a public, non-abstract open generic class with exactly two type parameters |
| AD025 | Error | `[PostProcessor]` type does not implement `IPostProcessor<TCommand, TResult>` |
| AD026 | Error | `[PostProcessor]` type does not expose a valid public `ProcessAsync` method |
| AD027 | Warning | A constrained `[Behavior]`/`[PreProcessor]`/`[PostProcessor]`/`[StreamBehavior]`'s constraint doesn't match any registered command/query — it will never run |
| AD028 | Error | `[NotificationBehavior]` type is not a public, non-abstract open generic class with exactly one type parameter |
| AD029 | Error | `[NotificationBehavior]` type does not implement `INotificationPipelineBehavior<TNotification>` |
| AD030 | Error | `[NotificationBehavior]` type does not expose a valid public `HandleAsync` method |

### AD001

> `[Handler]` on '{Type}' has no `Handle` or `HandleAsync` methods. No dispatch methods will be generated.

Add a valid `Handle` or `HandleAsync` method to the handler class.

### AD002

> `Duplicate handler for command '{Command}': both '{HandlerA}' and '{HandlerB}' define a Handle/HandleAsync method for this command type. Remove one handler or rename the method.`

Each command/query type must map to exactly one handler method.

### AD003

> `HandleAsync` on '{Handler}' for command '{Command}' is missing a `CancellationToken` parameter. Consider adding `CancellationToken ct = default` as the second parameter.`

The method still works; the warning helps you preserve cancellation flow.

### AD004

> `[Behavior]` on '{Type}' must be a public, non-abstract class with exactly two type parameters so AutoDispatch can close it as `<TCommand, TResult>`.`

Pipeline behaviors are resolved as closed generics at dispatch time, so `[Behavior]` types must be declared as open generic classes such as `LoggingBehavior<TCommand, TResult>`.

### AD005

> `[Behavior]` on '{Type}' must implement `AutoDispatch.IPipelineBehavior<TCommand, TResult>` using its declared type parameters.`

Implement the generated `IPipelineBehavior<TCommand, TResult>` interface directly on the behavior type.

### AD006

> `[Behavior]` on '{Type}' must declare `public Task<TResult> HandleAsync(TCommand command, Func<Task<TResult>> next, CancellationToken ct = default)`.`

Explicit interface implementations are not enough — the generated dispatcher calls the behavior's public `HandleAsync` method directly.

### AD007

> `[NotificationHandler]` on '{Type}' has no `HandleAsync(TNotification, CancellationToken)` method. No publish dispatch will be generated for this handler.`

Add a valid `HandleAsync(TNotification notification, CancellationToken ct = default)` method that returns `Task`.

### AD008

> `HandleAsync` on '{Handler}' for notification '{Notification}' is missing a `CancellationToken` parameter. Consider adding `CancellationToken ct = default` as the second parameter.`

The method still works; the warning helps you preserve cancellation flow through `PublishAsync`.

### AD009

> `[StreamHandler]` on '{Type}' has no `HandleAsync(TQuery, CancellationToken)` method returning `IAsyncEnumerable<TResult>`. No streaming dispatch will be generated.`

Add a valid `HandleAsync(TQuery query, CancellationToken ct = default)` method that returns `IAsyncEnumerable<TResult>`.

### AD010

> `Duplicate stream handler for query '{Query}': both '{HandlerA}' and '{HandlerB}' define a `HandleAsync` stream method for this query type. Remove one handler or rename the method.`

Each query type must map to exactly one stream handler, just like commands.

### AD011

> `HandleAsync` on '{Handler}' for query '{Query}' is missing a `CancellationToken` parameter. Consider adding `CancellationToken ct = default` as the second parameter.`

The method still works; the warning helps you preserve cancellation flow through `StreamAsync`.

### AD012

> `[StreamBehavior]` on '{Type}' must be a public, non-abstract class with exactly two type parameters so AutoDispatch can close it as `<TQuery, TResult>`.`

Stream pipeline behaviors are resolved as closed generics at dispatch time, so `[StreamBehavior]` types must be declared as open generic classes such as `LoggingStreamBehavior<TQuery, TResult>`.

### AD013

> `[StreamBehavior]` on '{Type}' must implement `AutoDispatch.IStreamPipelineBehavior<TQuery, TResult>` using its declared type parameters.`

Implement the generated `IStreamPipelineBehavior<TQuery, TResult>` interface directly on the behavior type.

### AD014

> `[StreamBehavior]` on '{Type}' must declare `public IAsyncEnumerable<TResult> HandleAsync(TQuery query, Func<IAsyncEnumerable<TResult>> next, CancellationToken ct = default)`.`

Explicit interface implementations are not enough — the generated dispatcher calls the behavior's public `HandleAsync` method directly.

## XML doc comments and pipeline readability

Doc comments on `Handle`/`HandleAsync` methods are forwarded to the generated `IDispatcher` member automatically:

```csharp
[Handler]
public sealed class CreateOrderHandler
{
    /// <summary>Creates an order for the given customer.</summary>
    public Task<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct = default)
        => Task.FromResult(new OrderId(Guid.NewGuid()));
}
```

generates:

```csharp
public interface IDispatcher
{
    /// <summary>Creates an order for the given customer.</summary>
    Task<OrderId> SendAsync(CreateOrderCommand command, CancellationToken ct = default);
}
```

Generated async dispatch methods that go through a behavior pipeline are also annotated with a
comment showing the execution order, so you never have to guess:

```csharp
// Pipeline: LoggingBehavior -> ValidationBehavior -> CreateOrderHandler.HandleAsync -> LoggingBehavior -> ValidationBehavior
public Task<OrderId> SendAsync(CreateOrderCommand command, CancellationToken ct = default)
{
    ...
}
```

## IDE code fixes

`AutoDispatch.CodeFixes` ships inside the `AutoDispatch.Generator` package and adds one-click fixes:

| Diagnostic | Quick fix |
|---|---|
| AD001 | Adds a `HandleAsync` stub method to a `[Handler]` class with none |
| AD003 | Adds the missing `CancellationToken ct = default` parameter |
| AD007 | Adds a `HandleAsync` stub method to a `[NotificationHandler]` class with none |
| AD008 | Adds the missing `CancellationToken ct = default` parameter to a notification `HandleAsync` |
| AD009 | Adds a `HandleAsync` stub method to a `[StreamHandler]` class with none |
| AD011 | Adds the missing `CancellationToken ct = default` parameter to a stream `HandleAsync` |

## Testing handlers and behaviors

The [`AutoDispatch.Testing`](https://www.nuget.org/packages/AutoDispatch.Testing) package makes it
easy to unit test handlers and `[Behavior]` chains without a DI container:

```bash
dotnet add package AutoDispatch.Testing
```

```csharp
// FakeServiceProvider — a minimal IServiceProvider for constructing the generated Dispatcher
var sp = new FakeServiceProvider().Add(new CreateOrderHandler());
IDispatcher dispatcher = new Dispatcher(sp);
var orderId = await dispatcher.SendAsync(new CreateOrderCommand("cust-1"));

// PipelineTestHarness — test a behavior in isolation, short-circuiting next()
var result = await PipelineTestHarness.InvokeAsync<CreateOrderCommand, OrderId>(
    loggingBehavior.HandleAsync,
    command,
    nextResult: expectedOrderId);
```

See the [AutoDispatch.Testing README](src/AutoDispatch.Testing/README.md) for more.

## Scaffolding with dotnet new

```bash
dotnet new install AutoDispatch.Templates
dotnet new autodispatch-handler -n CreateOrder --namespace MyApp.Orders
dotnet new autodispatch-notification -n OrderCreated --namespace MyApp.Orders
dotnet new autodispatch-stream -n GetOrders --namespace MyApp.Orders
```

Generates a ready-to-fill `CreateOrderCommand.cs` with the command record and `[Handler]` class,
`OrderCreatedNotification.cs` with the notification record and `[NotificationHandler]` class, or
`GetOrdersQuery.cs` with the query record and `[StreamHandler]` class.

## AutoDispatch vs alternatives

| Approach | Boilerplate | Runtime dispatch | Pipeline behaviors | Notifications (publish) | Streaming queries | Compile-time safety | AOT |
|---|---|---|---|---|---|---|---|
| **AutoDispatch** | Low | None | Compile-time generated, with typed `[ExceptionHandler]`s | ✅ (fan-out, sequential or `[ParallelPublish]`) | ✅ (`IAsyncEnumerable<T>` + pipeline) | High | ✅ |
| **MediatR** | Medium | Yes | Runtime reflection, with `IRequestExceptionHandler<,,>` | ✅ | ✅ | High | ⚠️ |
| **Raw service calls** | Low | None | Manual | Manual | Manual | High | ✅ |

### Benchmarks

[BenchmarkDotNet results](benchmarks/AutoDispatch.Benchmarks/README.md) comparing the generated
`IDispatcher` against MediatR's `IMediator`, for a single no-op command handler, a notification
fanned out to two no-op handlers, and a 10-item stream fully enumerated:

| Method                    | Mean      | Ratio | Allocated | Alloc Ratio |
|-------------------------- |----------:|------:|----------:|------------:|
| AutoDispatch_SendAsync    |  16.36 ns |  1.00 |      96 B |        1.00 |
| MediatR_Send              |  69.66 ns |  4.26 |     288 B |        3.00 |
| AutoDispatch_PublishAsync |  30.13 ns |  1.84 |      24 B |        0.25 |
| MediatR_Publish           | 106.47 ns |  6.51 |     464 B |        4.83 |
| AutoDispatch_StreamAsync  | 172.46 ns | 10.54 |     144 B |        1.50 |
| MediatR_CreateStream      | 435.03 ns | 26.60 |     536 B |        5.58 |

**`SendAsync` is ~4x faster, 3x fewer allocations. `PublishAsync` fanning out to two handlers is
~3.5x faster and allocates ~19x less. `StreamAsync` fully enumerating a 10-item stream is ~2.5x
faster and allocates ~3.7x less** — no reflection-based handler lookup, no runtime-built
pipeline, publisher, or stream wrapper. Run it yourself with `dotnet run -c Release` in
`benchmarks/AutoDispatch.Benchmarks`.

[A separate benchmark](benchmarks/AutoDispatch.Benchmarks.Pipeline/README.md) measures a
**full pipeline** — two behaviors plus a pre-processor and a post-processor wrapping the
handler — instead of a bare no-op dispatch:

| Method                              | Mean     | Ratio | Allocated | Alloc Ratio |
|------------------------------------- |---------:|------:|----------:|------------:|
| AutoDispatch_SendAsync_FullPipeline  | 173.9 ns |  1.00 |     432 B |        1.00 |
| MediatR_Send_FullPipeline            | 313.9 ns |  1.81 |    1280 B |        2.96 |

**Even fully wired up with behaviors and pre/post-processors on both sides, AutoDispatch is
still ~1.8x faster and allocates ~3x less than MediatR's equivalent runtime pipeline.**

## Migrating from MediatR

AutoDispatch follows the same CQRS mental model as MediatR, so migration is mechanical:

### 1. Install AutoDispatch and remove MediatR

```bash
dotnet add package AutoDispatch.Generator
dotnet remove package MediatR
dotnet remove package MediatR.Extensions.Microsoft.DependencyInjection
```

### 2. Remove marker interfaces from commands

```csharp
// Before
public sealed record CreateOrderCommand(string CustomerId) : IRequest<OrderId>;

// After
public sealed record CreateOrderCommand(string CustomerId);
```

### 3. Convert handler classes

```csharp
// Before
public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, OrderId>
{
    public Task<OrderId> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
        => Task.FromResult(new OrderId(Guid.NewGuid()));
}

// After
[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct = default)
        => Task.FromResult(new OrderId(Guid.NewGuid()));
}
```

### 4. Convert pipeline behaviors

```csharp
// Before
public sealed class LoggingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        _logger.LogInformation("→ {Request}", typeof(TRequest).Name);
        var result = await next();
        _logger.LogInformation("← {Request}", typeof(TRequest).Name);
        return result;
    }
}

// After
[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCommand, TResult>
    : IPipelineBehavior<TCommand, TResult>
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

### 5. Update DI registration

```csharp
// Before
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

// After
builder.Services.AddAutoDispatch();
```

### 6. Update dispatch call sites

```csharp
// Before (IMediator)
var orderId = await mediator.Send(new CreateOrderCommand(customerId), ct);

// After (IDispatcher)
var orderId = await dispatcher.SendAsync(new CreateOrderCommand(customerId), ct);
```

> **Tip:** Use the [AutoDispatch Migrator](https://github.com/Swevo/AutoDispatch.Generator) Copilot agent to automate the migration across your entire codebase.

## Best fit

Use AutoDispatch when you want:

- CQRS-style organization without MediatR ceremony
- Build-time generated dispatch code
- Fast startup and predictable runtime behavior
- Plain C# command/query types with no framework coupling

## Also by the same author

> 🌐 Full suite overview: **[swevo.github.io](https://swevo.github.io/)**


| Package | Description |
|---|---|
| [**AutoWire**](https://github.com/Swevo/AutoWire) | Compile-time DI auto-registration for `Microsoft.Extensions.DependencyInjection`. |
| [**AutoMap.Generator**](https://github.com/Swevo/AutoMap.Generator) | Compile-time object mapping with generated extension methods. |
| [**AutoValidate.Generator**](https://github.com/Swevo/AutoValidate.Generator) | Compile-time validator discovery and registration. |
| [**AutoResult.Generator**](https://github.com/Swevo/AutoResult.Generator) | Compile-time result helpers and `Try*()` wrappers. |
| [**AutoQuery.Generator**](https://github.com/Swevo/AutoQuery.Generator) | Compile-time query specifications for LINQ-based filtering. |
| [**AutoLog.Generator**](https://github.com/Swevo/AutoLog.Generator) | Compile-time high-performance logging — `[Log(Level, Message)]` on a partial method generates `LoggerMessage.Define`. AOT-safe. |
| [**AutoHttpClient.Generator**](https://github.com/Swevo/AutoHttpClient.Generator) | Compile-time typed HTTP client — `[HttpClient]` on an interface generates a strongly-typed client. AOT-safe Refit alternative. |

## Related Packages

| Package | Downloads | Description |
|---|---|---|
| [AutoWire](https://www.nuget.org/packages/AutoWire) | [![Downloads](https://img.shields.io/nuget/dt/AutoWire.svg)](https://www.nuget.org/packages/AutoWire) | Compile-time dependency injection auto-registration for  |
| [AutoMap.Generator](https://www.nuget.org/packages/AutoMap.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoMap.Generator.svg)](https://www.nuget.org/packages/AutoMap.Generator) | Compile-time object mapping for  |
| [AutoQuery.Generator](https://www.nuget.org/packages/AutoQuery.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoQuery.Generator.svg)](https://www.nuget.org/packages/AutoQuery.Generator) | Compile-time query composition for IQueryable using Roslyn incremental source generators |
| [AutoArchitecture](https://www.nuget.org/packages/AutoArchitecture) | [![Downloads](https://img.shields.io/nuget/dt/AutoArchitecture.svg)](https://www.nuget.org/packages/AutoArchitecture) | Compile-time architecture/dependency-rule enforcement for  |
| [AutoHttpClient.Generator](https://www.nuget.org/packages/AutoHttpClient.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoHttpClient.Generator.svg)](https://www.nuget.org/packages/AutoHttpClient.Generator) | Compile-time typed HTTP client generation for  |
| [AutoLog.Generator](https://www.nuget.org/packages/AutoLog.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoLog.Generator.svg)](https://www.nuget.org/packages/AutoLog.Generator) | Compile-time high-performance logging for  |
| [AutoValidate.Generator](https://www.nuget.org/packages/AutoValidate.Generator) | [![Downloads](https://img.shields.io/nuget/dt/AutoValidate.Generator.svg)](https://www.nuget.org/packages/AutoValidate.Generator) | Compile-time FluentValidation wiring for  |

---

## License

MIT
