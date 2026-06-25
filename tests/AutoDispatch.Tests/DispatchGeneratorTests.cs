using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AutoDispatch.Tests;

public class DispatchGeneratorTests
{
    private const string DependencyInjectionStub = @"
namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceCollection { }

    public static class ServiceCollectionServiceExtensions
    {
        public static IServiceCollection AddScoped<TService>(this IServiceCollection services) => services;
        public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
    }

    public static class ServiceProviderServiceExtensions
    {
        public static T GetRequiredService<T>(this System.IServiceProvider provider) => throw new System.NotImplementedException();
    }
}
";

    private static Dictionary<string, string> RunGenerator(string userSource, out ImmutableArray<Diagnostic> diagnostics)
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        try { refs.Add(MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location)); } catch { }
        try { refs.Add(MetadataReference.CreateFromFile(Assembly.Load("netstandard").Location)); } catch { }
        try { refs.Add(MetadataReference.CreateFromFile(Assembly.Load("System.Threading.Tasks").Location)); } catch { }

        var compilation = CSharpCompilation.Create("TestAssembly",
            new[]
            {
                CSharpSyntaxTree.ParseText(DependencyInjectionStub),
                CSharpSyntaxTree.ParseText(userSource)
            },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AutoDispatchGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out diagnostics);

        return driver.GetRunResult().GeneratedTrees
            .ToDictionary(
                t => System.IO.Path.GetFileName(t.FilePath),
                t => t.GetText().ToString());
    }

    [Fact]
    public void Attributes_FileIsGenerated()
    {
        var sources = RunGenerator(string.Empty, out _);
        Assert.True(sources.ContainsKey("AutoDispatch.Attributes.g.cs"));
    }

    [Fact]
    public void Attributes_ContainsHandlerAttribute()
    {
        var sources = RunGenerator(string.Empty, out _);
        var src = sources["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("HandlerAttribute", src);
    }

    [Fact]
    public void NoHandlers_NoDispatcherGenerated()
    {
        var sources = RunGenerator("public sealed class Nothing { }", out _);
        Assert.DoesNotContain("AutoDispatch.Dispatcher.g.cs", sources.Keys);
        Assert.DoesNotContain("AutoDispatch.Registration.g.cs", sources.Keys);
    }

    [Fact]
    public void SingleHandler_SyncReturn_GeneratesIDispatcher()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("public interface IDispatcher", src);
    }

    [Fact]
    public void SingleHandler_SyncReturn_GeneratesDispatcher()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("internal sealed class Dispatcher", src);
    }

    [Fact]
    public void SingleHandler_SyncReturn_CorrectSendMethod()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("Send(global::CreateOrderCommand command)", src);
        Assert.Contains("Handle(command);", src);
    }

    [Fact]
    public void SingleHandler_SyncVoid_GeneratesVoidSend()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class DeleteOrderCommand { }

[Handler]
public sealed class DeleteOrderHandler
{
    public void Handle(DeleteOrderCommand cmd) { }
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("void Send(global::DeleteOrderCommand command)", src);
    }

    [Fact]
    public void SingleHandler_AsyncTaskOfT_GeneratesSendAsync()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }
public sealed class OrderId { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.FromResult(new OrderId());
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("SendAsync(global::CreateOrderCommand command, global::System.Threading.CancellationToken ct = default)", src);
        Assert.Contains("Task<global::OrderId>", src);
    }

    [Fact]
    public void SingleHandler_AsyncTask_GeneratesTaskSendAsync()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class PingCommand { }

[Handler]
public sealed class PingHandler
{
    public Task HandleAsync(PingCommand cmd, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("global::System.Threading.Tasks.Task SendAsync(global::PingCommand command, global::System.Threading.CancellationToken ct = default)", src);
    }

    [Fact]
    public void MultipleHandlers_AllMethodsOnIDispatcher()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }
public sealed class DeleteOrderCommand { }
public sealed class OrderId { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.FromResult(new OrderId());
}

[Handler]
public sealed class DeleteOrderHandler
{
    public void Handle(DeleteOrderCommand cmd) { }
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("SendAsync(global::CreateOrderCommand command", src);
        Assert.Contains("Send(global::DeleteOrderCommand command)", src);
    }

    [Fact]
    public void DI_AddAutoDispatch_Generated()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("AddAutoDispatch", src);
    }

    [Fact]
    public void DI_RegistrationContainsHandler()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped<global::CreateOrderHandler>();", src);
    }

    [Fact]
    public void DI_DispatcherRegistered()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped<global::AutoDispatch.IDispatcher, global::AutoDispatch.Dispatcher>();", src);
    }

    [Fact]
    public void Diagnostic_AD001_NoHandleMethods()
    {
        RunGenerator(@"
using AutoDispatch;

[Handler]
public sealed class EmptyHandler
{
    public int NotAHandle() => 0;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD001" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Diagnostic_AD002_DuplicateCommand()
    {
        RunGenerator(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 1;
}

[Handler]
public sealed class CreateOrderHandlerTwo
{
    public int Handle(CreateOrderCommand cmd) => 2;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD002" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD003_AsyncWithoutCancellationToken()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }
public sealed class OrderId { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd) => Task.FromResult(new OrderId());
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD003" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Handler_InNamespace_UsesFullyQualifiedTypes()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

namespace MyApp
{
    public sealed class CreateOrderCommand { }
    public sealed class OrderId { }

    [Handler]
    public sealed class CreateOrderHandler
    {
        public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.FromResult(new OrderId());
    }
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("global::MyApp.CreateOrderCommand", src);
        Assert.Contains("global::MyApp.CreateOrderHandler", src);
        Assert.Contains("global::System.Threading.Tasks.Task<global::MyApp.OrderId>", src);
    }
}
