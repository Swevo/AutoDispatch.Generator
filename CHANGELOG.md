# Changelog

## [1.6.0] - 2026-09-15

### Added
- **Notifications / publish-subscribe** — the biggest gap versus MediatR is closed. Mark a class `[NotificationHandler]` with a `Task HandleAsync(TNotification notification, CancellationToken ct = default)` method, and AutoDispatch generates a `PublishAsync(TNotification, CancellationToken)` method on `IDispatcher` that fans a single publish call out to **every** registered handler for that notification type
- Unlike `[Handler]` commands (which require exactly one handler per command type), any number of `[NotificationHandler]` classes may subscribe to the same notification type — all of them run, in deterministic (handler-type-name) order, matching MediatR's default `ForeachAwaitPublisher` semantics
- `AddAutoDispatch()` now also registers `[NotificationHandler]` classes with their configured `HandlerLifetime` (defaults to `Scoped`, same as commands)
- AD007 (Warning): `[NotificationHandler]` on a class with no valid `HandleAsync(TNotification, CancellationToken)` method
- AD008 (Warning): notification `HandleAsync` is missing a `CancellationToken` parameter

## [1.5.0] - 2026-07-28

### Added
- **XML doc-comment forwarding** — `///` doc comments on `Handle`/`HandleAsync` methods are now emitted above the corresponding generated `IDispatcher` member, so callers get real IntelliSense instead of undocumented generated code
- **Pipeline order comments** — generated async dispatch methods with behaviors now include a `// Pipeline: A -> B -> Handler -> B -> A` comment showing execution order for readability
- **IDE code fixes** — `AutoDispatch.CodeFixes` (shipped inside the same NuGet package) adds quick fixes for AD001 (adds a `HandleAsync` stub method) and AD003 (adds the missing `CancellationToken ct = default` parameter)
- **AutoDispatch.Testing** — new companion package with `FakeServiceProvider` (a minimal `IServiceProvider` test double) and `PipelineTestHarness` (compose/short-circuit `[Behavior]` chains in tests) for unit testing handlers and behaviors without a full DI container
- **AutoDispatch.Templates** — new `dotnet new` item template package; `dotnet new install AutoDispatch.Templates` then `dotnet new autodispatch-handler -n CreateOrder` scaffolds a command + `[Handler]` pair

## [1.4.0] - 2026-07-10

### Added
- **Validated pipeline behaviors** — `[Behavior]` classes are now checked at build time before the dispatcher is emitted
- AD004 (Error): `[Behavior]` type must be a public, non-abstract open generic class with exactly two type parameters
- AD005 (Error): `[Behavior]` type must implement `IPipelineBehavior<TCommand, TResult>`
- AD006 (Error): `[Behavior]` type must expose a public `HandleAsync(TCommand, Func<Task<TResult>>, CancellationToken)` method

### Changed
- Behaviors with the same `Order` now execute in declaration order
- Invalid behaviors are skipped from code generation so they do not break otherwise valid dispatch pipelines

## [1.3.0] - 2026-06-26

### Added
- **Pipeline behaviors** — mark a class `[Behavior(Order = N)]` to wrap all async dispatch calls in a compile-time-generated pipeline chain
- `IPipelineBehavior<TCommand, TResult>` interface emitted into the generated attributes file
- `Unit` struct for void-async (`Task`) handlers; behaviors receive `Func<Task<Unit>>` as `next`
- Open-generic behaviors (`class MyBehavior<TCmd, TResult>`) registered via `AddScoped(typeof(MyBehavior<,>))` inside `AddAutoDispatch()`
- Multiple behaviors ordered ascending by `Order`; lower `Order` = outermost (runs first)
- Sync handlers (`T Send(...)`, `void Send(...)`) are not wrapped — pipeline applies to async only

## [1.2.0] - 2026-06-25

### Added
- `[CommandHandler]` — semantic alias for `[Handler]`, use on command handlers to express intent
- `[QueryHandler]` — semantic alias for `[Handler]`, use on query handlers to express intent
- Both aliases support the `Lifetime` property (`Scoped`/`Singleton`/`Transient`)
- All three attributes (`[Handler]`, `[CommandHandler]`, `[QueryHandler]`) are interchangeable

## [1.1.0] - 2026-06-25

### Added
- `HandlerLifetime` enum: `Scoped` (default), `Singleton`, `Transient`
- `[Handler(Lifetime = HandlerLifetime.Singleton)]` controls the DI registration lifetime per handler
- `AddAutoDispatch()` now emits `AddSingleton<T>()` or `AddTransient<T>()` accordingly

## [1.0.0] - 2026-06-25

### Added
- `[Handler]` attribute — marks a class as a dispatch handler
- `Handle(TCommand)` → generates `IDispatcher.Send(TCommand)` (sync)
- `HandleAsync(TCommand, CancellationToken)` → generates `IDispatcher.SendAsync(TCommand, CancellationToken)` (async)
- `void Handle` supported → `IDispatcher.Send` returns void
- `Task HandleAsync` supported → `IDispatcher.SendAsync` returns Task
- `AddAutoDispatch()` extension method registers all handlers + `IDispatcher`
- AD001 (Warning): `[Handler]` class has no Handle/HandleAsync methods
- AD002 (Error): Two handlers registered for the same command type
- AD003 (Warning): `HandleAsync` missing CancellationToken parameter
