namespace MyApp.Namespace;

/// <summary>The notification published to all handlers subscribed to it, including <see cref="FooHandler"/>.</summary>
public sealed record FooNotification(/* TODO: add notification properties */ string Value);

/// <summary>Handles <see cref="FooNotification"/>. Any number of handlers may subscribe to the same notification type.</summary>
[AutoDispatch.NotificationHandler]
public sealed class FooHandler
{
    /// <summary>Handles the published <see cref="FooNotification"/>.</summary>
    public System.Threading.Tasks.Task HandleAsync(FooNotification notification, System.Threading.CancellationToken ct = default)
    {
        // TODO: implement handler logic.
        throw new System.NotImplementedException();
    }
}
