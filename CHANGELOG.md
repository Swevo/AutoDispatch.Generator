# Changelog

## [1.11.0] - 2026-09-16

### Added
- **Exception actions** — matching MediatR's `IRequestExceptionAction<TRequest, TException>`. Declare a public, open generic class with exactly one type parameter (`TCommand`) implementing `IExceptionAction<TCommand, TException>` for one fixed, concrete exception type, mark it `[ExceptionAction(Order = N)]`, and AutoDispatch calls `ExecuteAsync` as a side-effect-only observer whenever that exception type is thrown — it can never suppress the exception or supply a fallback response, unlike `[ExceptionHandler]`
- Actions and handlers for the same exception type share the same generated `catch` block; all matching actions run first (in `Order`), then matching handlers run, mirroring MediatR's pipeline ordering where exception actions always fire before exception handlers get a chance to short-circuit
- Actions run even when no `[ExceptionHandler]` is registered at all for the exception type — the exception is rethrown afterward via `throw;`, preserving the original stack trace
- Most-derived-exception-type-first catch ordering is now computed across both actions and handlers together, so mixing the two for overlapping exception hierarchies still produces valid, correctly-ordered C#
- AD018 (Error): `[ExceptionAction]` type is not a public, non-abstract open generic class with exactly one type parameter
- AD019 (Error): `[ExceptionAction]` type does not implement `IExceptionAction<TCommand, TException>` for one fixed exception type
- AD020 (Error): `[ExceptionAction]` type does not expose a valid public `ExecuteAsync` method
- `AddAutoDispatch()` now also registers `[ExceptionAction]` open-generic types
- README: new "Exception actions" subsection, updated diagnostics table (AD018-020), and feature bullets
- 8 new tests (codegen, DI registration, runtime always-runs/does-not-suppress behavior, and all three diagnostics); 104/104 passing solution-wide

## [1.10.0] - 2026-09-16

### Added
- **Exception handling middleware** — matching MediatR's `IRequestExceptionHandler<TRequest, TResponse, TException>`. Declare a public, open generic class with exactly two type parameters (`TCommand`, `TResult`) implementing `IExceptionHandler<TCommand, TResult, TException>` for one fixed, concrete exception type, mark it `[ExceptionHandler(Order = N)]`, and AutoDispatch wraps every async command/query dispatch method (with or without `[Behavior]`s) in a generated `try`/`catch` for that exception type, calling your handler to either supply a fallback response (`ExceptionHandlerResult<TResult>.Handled(response)`) or let the exception keep propagating (`.Unhandled()`)
- Catch clauses are always generated most-derived-exception-type first (computed from the real inheritance depth of the declared exception type), then by `Order`, then by declaration order — guarantees generated code compiles even when handlers target both a base and derived exception type, and mirrors how a human would order hand-written catch blocks
- With no `[ExceptionHandler]`s registered, dispatch codegen is completely unchanged (no `try`/`catch`, no added `async` overhead) — fully backward compatible
- AD015 (Error): `[ExceptionHandler]` type is not a public, non-abstract open generic class with exactly two type parameters
- AD016 (Error): `[ExceptionHandler]` type does not implement `IExceptionHandler<TCommand, TResult, TException>` for one fixed exception type
- AD017 (Error): `[ExceptionHandler]` type does not expose a valid public `HandleAsync` method
- `AddAutoDispatch()` now also registers `[ExceptionHandler]` open-generic types, same as `[Behavior]`
- README: new "Exception handling" section, updated diagnostics table (AD015-017), comparison table, and feature bullets
- 11 new tests (codegen for both the `Task<TResult>` and void-async shapes, most-derived-first catch ordering, DI registration, runtime handled/unhandled/void-async behavior, and all three diagnostics); 96/96 passing solution-wide

## [1.9.0] - 2026-09-16

### Added
- **Configurable notification publish strategy** — matching MediatR's `INotificationPublisher` options. Mark a notification type `[ParallelPublish]` to fan `PublishAsync` out to all of its handlers concurrently via `Task.WhenAll` instead of the sequential default (matching MediatR's `TaskWhenAllPublisher`); every handler runs even if another one throws, including handlers that throw synchronously (converted to a faulted task via the new internal `PublishTaskHelpers.SafeInvoke` so a sync throw can't skip the remaining handlers)
- Notifications without `[ParallelPublish]` are unaffected — codegen is unchanged, still the deterministic sequential `await`-one-at-a-time order from 1.6.0
- README: new "Parallel publish" subsection under Notifications, updated feature bullets and comparison table
- 6 new tests (codegen for both strategies, DI-free attribute discovery, runtime concurrency, and the synchronous-throw safety guarantee); 85/85 passing solution-wide

## [1.8.0] - 2026-09-16

### Added
- **Stream pipeline behaviors** — closes the "no pipeline-behavior support for streams" gap noted in 1.7.0, matching MediatR's `IStreamPipelineBehavior<TRequest, TResponse>`. Declare a public, open generic class with exactly two type parameters implementing `IStreamPipelineBehavior<TQuery, TResult>` and mark it `[StreamBehavior(Order = N)]` — AutoDispatch wraps every generated `StreamAsync` call with it, in `Order` order, using a lazy chain of `Func<IAsyncEnumerable<TResult>>` (no Task-wrapping, matching the handler's own laziness)
- AD012 (Error): `[StreamBehavior]` type is not a public, non-abstract open generic class with exactly two type parameters
- AD013 (Error): `[StreamBehavior]` type does not implement `IStreamPipelineBehavior<TQuery, TResult>`
- AD014 (Error): `[StreamBehavior]` type does not expose a valid public `HandleAsync` method
- `AddAutoDispatch()` now also registers `[StreamBehavior]` open-generic types, same as `[Behavior]`
- README: new "Stream pipeline behaviors" section, updated diagnostics table (AD012-014), updated comparison table and feature bullets
- 10 new tests (codegen, DI registration, runtime ordering/forwarding, diagnostics); 80/80 passing solution-wide

## [1.7.0] - 2026-09-16

### Added
- **Streaming queries** — the second-biggest gap versus MediatR is closed. Mark a class `[StreamHandler]` with an `IAsyncEnumerable<TResult> HandleAsync(TQuery query, CancellationToken ct = default)` method, and AutoDispatch generates a `StreamAsync(TQuery, CancellationToken) : IAsyncEnumerable<TResult>` method on `IDispatcher` that delegates directly to the handler — no buffering, no wrapping enumerator
- Like `[Handler]` commands (and unlike notifications), exactly one `[StreamHandler]` may exist per query type
- `AddAutoDispatch()` now also registers `[StreamHandler]` classes with their configured `HandlerLifetime` (defaults to `Scoped`, same as commands and notifications)
- AD009 (Warning): `[StreamHandler]` on a class with no valid `HandleAsync(TQuery, CancellationToken) : IAsyncEnumerable<TResult>` method
- AD010 (Error): more than one `[StreamHandler]` registered for the same query type
- AD011 (Warning): stream `HandleAsync` is missing a `CancellationToken` parameter
- **IDE code fixes for streams** — `AddCancellationTokenCodeFixProvider` now also fixes AD011, and a new `AddStreamHandleAsyncStubCodeFixProvider` fixes AD009 by adding an `IAsyncEnumerable<object>`-returning `HandleAsync` stub
- **`autodispatch-stream` item template** — `AutoDispatch.Templates` (bumped to 1.2.0) now scaffolds a query record + `[StreamHandler]` class via `dotnet new autodispatch-stream -n GetOrders`, matching the existing `autodispatch-handler`/`autodispatch-notification` templates
- **Streaming benchmark** — `AutoDispatch.Benchmarks` now also compares `StreamAsync`/`CreateStream` fully enumerating a 10-item stream: AutoDispatch is ~2.5x faster than MediatR and allocates ~3.7x less (144 B vs 536 B). README and benchmark results updated with real numbers

## [1.6.1] - 2026-09-15

### Added
- **Publish benchmark** — `AutoDispatch.Benchmarks` now also compares `PublishAsync`/`Publish` fanning a notification out to two no-op handlers: AutoDispatch is ~3.5x faster than MediatR and allocates ~19x less (24 B vs 464 B) for the same two-handler fan-out. README and benchmark results updated with real numbers.
- **`autodispatch-notification` item template** — `AutoDispatch.Templates` (bumped to 1.1.0) now scaffolds a notification record + `[NotificationHandler]` class via `dotnet new autodispatch-notification -n OrderCreated`, matching the existing `autodispatch-handler` template for commands
- **IDE code fixes for notifications** — `AutoDispatch.CodeFixes` now also fixes AD007 (adds a `HandleAsync` stub to a `[NotificationHandler]` class with none) and AD008 (adds the missing `CancellationToken ct = default` parameter), matching the existing AD001/AD003 quick fixes for commands

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
