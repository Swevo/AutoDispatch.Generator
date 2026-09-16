using AutoDispatch;
using Microsoft.Extensions.DependencyInjection;

// AutoDispatch generates all handler/behavior/DI registrations at compile
// time via a Roslyn source generator — there is no reflection, no runtime
// Assembly.GetTypes() scanning, and no dynamic proxy/codegen anywhere in the
// generated output. That makes it fully compatible with Native AOT
// publishing (dotnet publish -c Release -r <rid> /p:PublishAot=true) with
// zero extra configuration: no [DynamicallyAccessedMembers], no runtime
// directives, no reflection-based DI container required.

var services = new ServiceCollection();
services.AddAutoDispatch();
await using var provider = services.BuildServiceProvider();

var dispatcher = provider.GetRequiredService<IDispatcher>();

var orderId = await dispatcher.SendAsync(new CreateOrderCommand("customer-42"));
Console.WriteLine($"Created order {orderId.Value}");

var order = await dispatcher.SendAsync(new GetOrderQuery(orderId));
Console.WriteLine(order is null ? "Order not found" : $"Order for {order.CustomerId}");

dispatcher.Send(new DeleteOrderCommand(orderId));
Console.WriteLine("Order deleted");

public sealed record CreateOrderCommand(string CustomerId);
public sealed record GetOrderQuery(OrderId OrderId);
public sealed record DeleteOrderCommand(OrderId OrderId);
public sealed record OrderId(Guid Value);
public sealed record Order(OrderId Id, string CustomerId);

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
        => Task.FromResult<Order?>(new Order(query.OrderId, "customer-42"));
}

[CommandHandler]
public sealed class DeleteOrderHandler
{
    public void Handle(DeleteOrderCommand command)
    {
        // no-op for the sample
    }
}
