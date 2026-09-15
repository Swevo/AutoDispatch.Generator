# AutoDispatch.Templates

`dotnet new` item templates that scaffold code for
[AutoDispatch.Generator](https://www.nuget.org/packages/AutoDispatch.Generator):
a command + `[Handler]` pair, a notification + `[NotificationHandler]` pair, or a streaming
query + `[StreamHandler]` pair.

## Install

```bash
dotnet new install AutoDispatch.Templates
```

## Use

### Command + Handler

```bash
dotnet new autodispatch-handler -n CreateOrder --namespace MyApp.Orders
```

Generates `CreateOrderCommand.cs`:

```csharp
namespace MyApp.Orders;

public sealed record CreateOrderCommand(/* TODO: add command properties */ string Value);

[AutoDispatch.Handler]
public sealed class CreateOrderHandler
{
    public System.Threading.Tasks.Task HandleAsync(CreateOrderCommand command, System.Threading.CancellationToken ct = default)
    {
        // TODO: implement handler logic.
        throw new System.NotImplementedException();
    }
}
```

### Notification + NotificationHandler

```bash
dotnet new autodispatch-notification -n OrderCreated --namespace MyApp.Orders
```

Generates `OrderCreatedNotification.cs`:

```csharp
namespace MyApp.Orders;

public sealed record OrderCreatedNotification(/* TODO: add notification properties */ string Value);

[AutoDispatch.NotificationHandler]
public sealed class OrderCreatedHandler
{
    public System.Threading.Tasks.Task HandleAsync(OrderCreatedNotification notification, System.Threading.CancellationToken ct = default)
    {
        // TODO: implement handler logic.
        throw new System.NotImplementedException();
    }
}
```

Run the command again with a different `-n` to add another handler for the same
notification type — any number of `[NotificationHandler]` classes may subscribe to it.

### Streaming query + StreamHandler

```bash
dotnet new autodispatch-stream -n GetOrders --namespace MyApp.Orders
```

Generates `GetOrdersQuery.cs`:

```csharp
namespace MyApp.Orders;

public sealed record GetOrdersQuery(/* TODO: add query properties */ string Value);

[AutoDispatch.StreamHandler]
public sealed class GetOrdersHandler
{
    public System.Collections.Generic.IAsyncEnumerable<object> HandleAsync(GetOrdersQuery query, System.Threading.CancellationToken ct = default)
    {
        // TODO: replace object with your result type, then reimplement this as an async iterator
        // that yields items as they become available.
        throw new System.NotImplementedException();
    }
}
```

## Uninstall

```bash
dotnet new uninstall AutoDispatch.Templates
```
