# AutoDispatch.Templates

`dotnet new` item template that scaffolds a command + `[Handler]` class pair for
[AutoDispatch.Generator](https://www.nuget.org/packages/AutoDispatch.Generator).

## Install

```bash
dotnet new install AutoDispatch.Templates
```

## Use

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

## Uninstall

```bash
dotnet new uninstall AutoDispatch.Templates
```
