using AutoDispatch.Testing;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AutoDispatch.Tests;

public interface IGreeter
{
    string Greet(string name);
}

public sealed class Greeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}!";
}

/// <summary>
/// Stand-in for a generated `[Behavior]` class — deliberately hand-written (not
/// source-generated) so these tests demonstrate that <see cref="PipelineTestHarness"/> and
/// <see cref="FakeServiceProvider"/> work purely against delegate shapes/interfaces, with no
/// dependency on AutoDispatch's own source generator.
/// </summary>
public sealed class UpperCaseBehavior
{
    public async Task<string> HandleAsync(string command, Func<Task<string>> next, CancellationToken ct = default)
    {
        var result = await next();
        return result.ToUpperInvariant();
    }
}

public sealed class PrefixBehavior
{
    private readonly string _prefix;

    public PrefixBehavior(string prefix) => _prefix = prefix;

    public async Task<string> HandleAsync(string command, Func<Task<string>> next, CancellationToken ct = default) =>
        _prefix + await next();
}

public class AutoDispatchTestingTests
{
    [Fact]
    public void FakeServiceProvider_ResolvesRegisteredService()
    {
        var sp = new FakeServiceProvider().Add<IGreeter>(new Greeter());

        var greeter = (IGreeter?)sp.GetService(typeof(IGreeter));

        Assert.NotNull(greeter);
        Assert.Equal("Hello, world!", greeter!.Greet("world"));
    }

    [Fact]
    public void FakeServiceProvider_ReturnsNull_ForUnregisteredService()
    {
        var sp = new FakeServiceProvider();

        Assert.Null(sp.GetService(typeof(IGreeter)));
    }

    [Fact]
    public void FakeServiceProvider_Add_ThrowsOnNullInstance()
    {
        var sp = new FakeServiceProvider();

        Assert.Throws<ArgumentNullException>(() => sp.Add<IGreeter>(null!));
    }

    [Fact]
    public async Task PipelineTestHarness_InvokesBehaviorInIsolation_ShortCircuitingNext()
    {
        var behavior = new UpperCaseBehavior();

        var result = await PipelineTestHarness.InvokeAsync<string, string>(
            behavior.HandleAsync,
            command: "ignored",
            nextResult: "pong:hello");

        Assert.Equal("PONG:HELLO", result);
    }

    [Fact]
    public async Task PipelineTestHarness_ComposesMultipleBehaviorsAndHandler_OutermostFirst()
    {
        var upperCase = new UpperCaseBehavior();
        var prefix = new PrefixBehavior("*** ");

        var result = await PipelineTestHarness.InvokeAsync<string, string>(
            command: "hi",
            handler: () => Task.FromResult("pong:hi"),
            ct: CancellationToken.None,
            prefix.HandleAsync,
            upperCase.HandleAsync);

        // prefix (outermost) wraps upperCase (innermost) wraps the handler result,
        // so upperCase uppercases the handler's raw output before prefix prepends "*** ".
        Assert.Equal("*** PONG:HI", result);
    }

    [Fact]
    public async Task PipelineTestHarness_NoBehaviors_InvokesHandlerDirectly()
    {
        var result = await PipelineTestHarness.InvokeAsync<string, string>(
            command: "hi",
            handler: () => Task.FromResult("pong:hi"),
            ct: CancellationToken.None);

        Assert.Equal("pong:hi", result);
    }
}
