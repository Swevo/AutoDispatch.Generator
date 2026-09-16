using System.Linq;
using Xunit;

namespace AutoDispatch.Tests;

public class PipelineDiagramTests
{
    private static string GetDiagramSource(string userSource) =>
        DispatchGeneratorTests.RunGenerator(userSource, out _)["AutoDispatch.PipelineDiagrams.g.cs"];

    [Fact]
    public void NoHandlers_DoesNotGeneratePipelineDiagrams()
    {
        var sources = DispatchGeneratorTests.RunGenerator(string.Empty, out _);
        Assert.DoesNotContain("AutoDispatch.PipelineDiagrams.g.cs", sources.Keys);
    }

    [Fact]
    public void SingleHandler_GeneratesDiagramEntryForCommand()
    {
        var src = GetDiagramSource(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}");

        Assert.Contains("public static class AutoDispatchPipelineDiagrams", src);
        Assert.Contains("[\"CreateOrderCommand\"]", src);
        Assert.Contains("CreateOrderHandler", src);
        Assert.Contains("graph LR", src);
    }

    [Fact]
    public void HandlerWithBehavior_IncludesBehaviorInDiagram()
    {
        var src = GetDiagramSource(@"
using AutoDispatch;
using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }

[Behavior]
public sealed class LoggingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public Task<TResult> HandleAsync(TCommand command, Func<Task<TResult>> next, CancellationToken ct = default) => next();
}

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}");

        Assert.Contains("LoggingBehavior", src);
    }

    [Fact]
    public void NotificationWithMultipleHandlers_FansOutInDiagram()
    {
        var src = GetDiagramSource(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class SendEmail
{
    public Task HandleAsync(OrderCreated n, CancellationToken ct) => Task.CompletedTask;
}

[NotificationHandler]
public sealed class UpdateInventory
{
    public Task HandleAsync(OrderCreated n, CancellationToken ct) => Task.CompletedTask;
}");

        Assert.Contains("[\"OrderCreated\"]", src);
        Assert.Contains("SendEmail", src);
        Assert.Contains("UpdateInventory", src);
        Assert.Contains("handlers run sequentially", src);
    }

    [Fact]
    public void AllProperty_CombinesEveryPipeline()
    {
        var src = GetDiagramSource(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}");

        Assert.Contains("public static string All { get; }", src);
    }
}
