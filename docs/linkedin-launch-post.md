I got tired of paying a reflection tax for a pattern that's really just "route this object to that method." So I built a source generator that removes it.

**AutoDispatch.Generator** gives you the same CQRS mental model as MediatR — command, handler, pipeline behavior — but the entire dispatcher is generated at compile time by a Roslyn incremental source generator. No IRequest<T>, no reflection, no runtime service scan.

Before:

    public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, OrderId>
    {
        public Task<OrderId> Handle(CreateOrderCommand request, CancellationToken ct) { ... }
    }

After:

    [Handler]
    public sealed class CreateOrderHandler
    {
        public Task<OrderId> HandleAsync(CreateOrderCommand command, CancellationToken ct = default) { ... }
    }

One attribute. No marker interface on the command. Everything else — IDispatcher, the DI registration, the pipeline chain — is generated the moment you compile.

I benchmarked it against MediatR for a trivial handler call:

→ AutoDispatch: 23.4 ns, 96 B allocated
→ MediatR: 89.1 ns, 288 B allocated

~3.8x faster, 3x fewer allocations, and it's Native AOT / trimming compatible out of the box — no reflection means no extra annotations needed.

It also catches mistakes at compile time that MediatR only surfaces at runtime (or never): duplicate handlers for the same command, missing CancellationToken parameters, misconfigured pipeline behaviors — each with its own diagnostic and, for a couple of them, a one-click IDE fix.

Shipped this week as three packages:
• AutoDispatch.Generator — the core generator
• AutoDispatch.Testing — test handlers/behaviors without a DI container
• AutoDispatch.Templates — dotnet new autodispatch-handler scaffolds a command + handler pair

If you're running MediatR today, the migration is mostly mechanical — swap the marker interface for an attribute, rename Handle to HandleAsync, done.

Repo: https://github.com/Swevo/AutoDispatch.Generator

#dotnet #csharp #cqrs #sourcegenerators #softwareengineering
