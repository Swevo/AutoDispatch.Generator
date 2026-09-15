namespace MyApp.Namespace;

/// <summary>The streaming query dispatched to <see cref="FooHandler"/> via <c>IDispatcher.StreamAsync</c>.</summary>
public sealed record FooQuery(/* TODO: add query properties */ string Value);

/// <summary>Handles <see cref="FooQuery"/> by streaming results as they become available.</summary>
[AutoDispatch.StreamHandler]
public sealed class FooHandler
{
    /// <summary>Streams the results for <see cref="FooQuery"/>.</summary>
    public System.Collections.Generic.IAsyncEnumerable<object> HandleAsync(FooQuery query, System.Threading.CancellationToken ct = default)
    {
        // TODO: replace object with your result type, then reimplement this as an async iterator:
        // public async IAsyncEnumerable<TResult> HandleAsync(FooQuery query, [EnumeratorCancellation] CancellationToken ct = default)
        // {
        //     await foreach (var item in ...)
        //     {
        //         yield return item;
        //     }
        // }
        throw new System.NotImplementedException();
    }
}
