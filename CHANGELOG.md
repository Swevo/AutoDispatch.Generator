# Changelog

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
