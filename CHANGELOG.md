# Changelog

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
