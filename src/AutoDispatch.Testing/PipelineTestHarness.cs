using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoDispatch.Testing;

/// <summary>
/// A delegate matching the shape of a generated <c>[Behavior]</c>'s public
/// <c>HandleAsync(TCommand, Func&lt;Task&lt;TResult&gt;&gt;, CancellationToken)</c> method.
/// Because each AutoDispatch-enabled project generates its own local
/// <c>AutoDispatch.IPipelineBehavior&lt;TCommand,TResult&gt;</c> type, this harness intentionally
/// works against plain delegates (via method-group conversion, e.g. <c>behavior.HandleAsync</c>)
/// instead of the generated interface, so it works from any project regardless of which assembly
/// the interface was generated into.
/// </summary>
public delegate Task<TResult> PipelineBehaviorHandler<TCommand, TResult>(
    TCommand command,
    Func<Task<TResult>> next,
    CancellationToken ct = default);

/// <summary>
/// Test helper for exercising one or more <c>[Behavior]</c> implementations together with (or
/// instead of) a handler, without needing DI, the generated <c>Dispatcher</c>, or ASP.NET Core.
/// </summary>
public static class PipelineTestHarness
{
    /// <summary>
    /// Invokes a single behavior in isolation, short-circuiting <c>next()</c> to
    /// <paramref name="nextResult"/> so the behavior's own logic can be asserted independently
    /// of any handler.
    /// </summary>
    public static Task<TResult> InvokeAsync<TCommand, TResult>(
        PipelineBehaviorHandler<TCommand, TResult> behavior,
        TCommand command,
        TResult nextResult,
        CancellationToken ct = default) =>
        behavior(command, () => Task.FromResult(nextResult), ct);

    /// <summary>
    /// Builds and executes the same outermost-first pipeline chain that AutoDispatch generates
    /// for <c>[Behavior(Order = N)]</c> classes, ending in <paramref name="handler"/>. Pass
    /// behaviors in the order you expect them to run (outermost/lowest <c>Order</c> first).
    /// </summary>
    public static Task<TResult> InvokeAsync<TCommand, TResult>(
        TCommand command,
        Func<Task<TResult>> handler,
        CancellationToken ct,
        params PipelineBehaviorHandler<TCommand, TResult>[] behaviorsOutermostFirst)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var pipeline = handler;
        for (var i = behaviorsOutermostFirst.Length - 1; i >= 0; i--)
        {
            var next = pipeline;
            var behavior = behaviorsOutermostFirst[i];
            pipeline = () => behavior(command, next, ct);
        }

        return pipeline();
    }
}
