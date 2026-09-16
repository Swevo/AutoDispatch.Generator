using AutoDispatch;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAutoDispatch();

var app = builder.Build();

// One line wires every [Endpoint]-attributed command/query below to a minimal API route,
// generated at compile time by AutoDispatch -- no hand-written MapPost/MapGet lambdas needed.
app.MapAutoDispatchEndpoints();

app.Run();

[Endpoint("POST", "/orders")]
public sealed record CreateOrderCommand(string CustomerId);

[Endpoint("GET", "/orders/{id}")]
public sealed record GetOrderQuery(Guid Id);

[Endpoint("DELETE", "/orders/{id}")]
public sealed record DeleteOrderCommand(Guid Id);

public sealed record Order(Guid Id, string CustomerId);

[CommandHandler]
public sealed class CreateOrderHandler
{
    public Task<Order> HandleAsync(CreateOrderCommand command, CancellationToken ct = default)
        => Task.FromResult(new Order(Guid.NewGuid(), command.CustomerId));
}

[QueryHandler]
public sealed class GetOrderHandler
{
    public Task<Order?> HandleAsync(GetOrderQuery query, CancellationToken ct = default)
        => Task.FromResult<Order?>(new Order(query.Id, "customer-42"));
}

[CommandHandler]
public sealed class DeleteOrderHandler
{
    public void Handle(DeleteOrderCommand command)
    {
        // no-op for the sample
    }
}

// Needed so WebApplicationFactory<T>-style integration tests (and this file-scoped Program.cs)
// can reference the entry point type.
public partial class Program;
