# AutoDispatch.Generator

[![NuGet](https://img.shields.io/nuget/v/AutoDispatch.Generator.svg)](https://www.nuget.org/packages/AutoDispatch.Generator)
[![CI](https://github.com/Swevo/AutoDispatch.Generator/actions/workflows/build.yml/badge.svg)](https://github.com/Swevo/AutoDispatch.Generator/actions/workflows/build.yml)

AutoDispatch gives you the **MediatR-style handler pattern** without `IRequest<T>`, `IRequestHandler<,>`, reflection, or runtime dispatch overhead. Mark a handler with `[Handler]`, write `Handle` or `HandleAsync`, and the generator emits a strongly-typed dispatcher at build time.

## Why AutoDispatch?

- **Same mental model as MediatR** — command/query + handler + dispatcher
- **Zero reflection** — direct generated calls, no runtime dispatch overhead
- **Pipeline behaviors** — `[Behavior(Order = N)]` wraps all async handlers at compile time; no `IPipelineBehavior<,>` magic at runtime
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

## Pipeline behaviors

`[Behavior(Order = N)]` wraps all async handlers in a compile-time pipeline. Identical mental model to MediatR's `IPipelineBehavior<,>` — but the chain is emitted as generated code, not resolved via reflection at runtime.

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

### Behaviors only apply to async handlers

Sync `T Send(...)` and `void Send(...)` methods are not wrapped. Add a pipeline when you migrate a sync handler to async, or keep it sync for zero overhead.

## Diagnostics

| Code | Severity | Description |
|---|---|---|
| AD001 | Warning | `[Handler]` on a class with no valid `Handle`/`HandleAsync` methods |
| AD002 | Error | Duplicate handlers discovered for the same command type |
| AD003 | Warning | `HandleAsync` does not accept `CancellationToken` |

### AD001

> `[Handler]` on '{Type}' has no `Handle` or `HandleAsync` methods. No dispatch methods will be generated.

Add a valid `Handle` or `HandleAsync` method to the handler class.

### AD002

> `Duplicate handler for command '{Command}': both '{HandlerA}' and '{HandlerB}' define a Handle/HandleAsync method for this command type. Remove one handler or rename the method.`

Each command/query type must map to exactly one handler method.

### AD003

> `HandleAsync` on '{Handler}' for command '{Command}' is missing a `CancellationToken` parameter. Consider adding `CancellationToken ct = default` as the second parameter.`

The method still works; the warning helps you preserve cancellation flow.

## AutoDispatch vs alternatives

| Approach | Boilerplate | Runtime dispatch | Pipeline behaviors | Compile-time safety | AOT |
|---|---|---|---|---|---|
| **AutoDispatch** | Low | None | Compile-time generated | High | ✅ |
| **MediatR** | Medium | Yes | Runtime reflection | High | ⚠️ |
| **Raw service calls** | Low | None | Manual | High | ✅ |

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

## License

MIT
