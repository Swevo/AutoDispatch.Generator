MediatR is a great pattern — commands, handlers, pipeline behaviors give you a clean, testable way to organize application logic. I've used it happily on plenty of projects. But on a couple of latency-sensitive and Native AOT services, the reflection-based handler resolution and runtime-built pipeline chain started showing up in profiles, and AOT support needed extra care.

So I built **AutoDispatch.Generator** — same CQRS mental model, but the whole dispatcher is generated at compile time by a Roslyn incremental source generator instead of resolved at runtime.

With MediatR:

    public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, OrderId>
    {
        public Task<OrderId> Handle(CreateOrderCommand request, CancellationToken ct) { ... }
    }

With AutoDispatch:

    [Handler]
    public sealed class CreateOrderHandler
    {
        public Task<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct = default) { ... }
    }

One attribute instead of a marker interface. `IDispatcher`, the DI registration, and the pipeline chain are all generated the moment you compile — nothing to resolve at runtime.

For teams where that trade-off matters, the numbers are worth sharing. Benchmarking a trivial handler call:

→ AutoDispatch: 23.4 ns, 96 B allocated
→ MediatR: 89.1 ns, 288 B allocated

That gap comes from removing reflection-based lookup and runtime pipeline construction — it also means Native AOT and trimming work out of the box, with no extra annotations.

A nice side effect of moving resolution to compile time: a few classes of mistake become compiler diagnostics instead of runtime surprises — duplicate handlers for the same command, a missing CancellationToken, a misconfigured pipeline behavior — a couple even come with a one-click IDE fix.

Shipped this week as three packages:
• AutoDispatch.Generator — the core generator
• AutoDispatch.Testing — test handlers/behaviors without a DI container
• AutoDispatch.Templates — dotnet new autodispatch-handler scaffolds a command + handler pair

If you already use MediatR and just want to try it, migration is mostly mechanical — swap the marker interface for an attribute, rename Handle to HandleAsync. MediatR remains a solid default for most apps; this is aimed at teams who've hit the specific perf/AOT wall I did.

Repo: https://github.com/Swevo/AutoDispatch.Generator

#dotnet #csharp #cqrs #sourcegenerators #softwareengineering
