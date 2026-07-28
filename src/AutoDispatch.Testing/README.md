# AutoDispatch.Testing

Lightweight test helpers for [AutoDispatch.Generator](https://www.nuget.org/packages/AutoDispatch.Generator) — no DI container, no ASP.NET Core host required.

## Installation

```bash
dotnet add package AutoDispatch.Testing
```

## FakeServiceProvider

A minimal `IServiceProvider` test double, useful for constructing the generated `Dispatcher` or
testing `AddAutoDispatch()` consumers directly:

```csharp
var sp = new FakeServiceProvider()
    .Add(new CreateOrderHandler())
    .Add(new LoggingBehavior<CreateOrderCommand, OrderId>());

IDispatcher dispatcher = new Dispatcher(sp);
var orderId = await dispatcher.SendAsync(new CreateOrderCommand("cust-1"));
```

## PipelineTestHarness

Test a `[Behavior]` in isolation — short-circuit `next()` to a known result and assert the
behavior's own logic:

```csharp
var behavior = new LoggingBehavior<CreateOrderCommand, OrderId>(logger);

var result = await PipelineTestHarness.InvokeAsync<CreateOrderCommand, OrderId>(
    behavior.HandleAsync,
    new CreateOrderCommand("cust-1"),
    nextResult: new OrderId(Guid.NewGuid()));
```

Or compose multiple behaviors + a handler exactly like the generated dispatcher does, without any
DI setup:

```csharp
var result = await PipelineTestHarness.InvokeAsync<CreateOrderCommand, OrderId>(
    command,
    handler: () => handler.HandleAsync(command),
    ct: CancellationToken.None,
    loggingBehavior.HandleAsync,
    validationBehavior.HandleAsync);
```

## Why not reference `IDispatcher`/`IPipelineBehavior<,>` directly?

AutoDispatch generates its attributes and interfaces locally into *each* consuming project — there
is no shared runtime assembly (that's what makes it AOT/reflection-free). Because of that,
`PipelineTestHarness` works against plain delegates (via method-group conversion, e.g.
`behavior.HandleAsync`) rather than the generated interface type, so it works identically no
matter which project generated the type.
