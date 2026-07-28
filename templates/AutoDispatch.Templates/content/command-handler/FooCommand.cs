namespace MyApp.Namespace;

/// <summary>The command dispatched to <see cref="FooHandler"/>.</summary>
public sealed record FooCommand(/* TODO: add command properties */ string Value);

/// <summary>Handles <see cref="FooCommand"/>.</summary>
[AutoDispatch.Handler]
public sealed class FooHandler
{
    /// <summary>Handles the <see cref="FooCommand"/>.</summary>
    public System.Threading.Tasks.Task HandleAsync(FooCommand command, System.Threading.CancellationToken ct = default)
    {
        // TODO: implement handler logic.
        throw new System.NotImplementedException();
    }
}
