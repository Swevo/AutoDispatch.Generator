using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
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
        public static IServiceCollection AddScoped(this IServiceCollection services, System.Type serviceType) => services;
        public static IServiceCollection AddSingleton<TService>(this IServiceCollection services) => services;
        public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
        public static IServiceCollection AddTransient<TService>(this IServiceCollection services) => services;
        public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
    }

    public static class ServiceProviderServiceExtensions
    {
        public static T GetRequiredService<T>(this System.IServiceProvider provider)
        {
            var service = provider.GetService(typeof(T));
            if (service is T typed)
            {
                return typed;
            }

            throw new System.InvalidOperationException(""Unable to resolve service of type "" + typeof(T).FullName);
        }
    }
}
";

    private static Dictionary<string, string> RunGenerator(string userSource, out ImmutableArray<Diagnostic> diagnostics)
    {
        var result = RunGeneratorResult(userSource);
        diagnostics = result.Diagnostics;
        return result.Sources;
    }

    private static GenerationResult RunGeneratorResult(string userSource)
    {
        var refs = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: $"TestAssembly_{Guid.NewGuid():N}",
            new[]
            {
                CSharpSyntaxTree.ParseText(DependencyInjectionStub),
                CSharpSyntaxTree.ParseText(userSource)
            },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AutoDispatchGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        return new GenerationResult(
            driver.GetRunResult().GeneratedTrees.ToDictionary(
                t => System.IO.Path.GetFileName(t.FilePath),
                t => t.GetText().ToString()),
            diagnostics,
            (CSharpCompilation)outputCompilation);
    }

    private static async Task<object?> InvokeSendAsync(Assembly assembly, string commandTypeName)
    {
        var commandType = assembly.GetType(commandTypeName, throwOnError: true)!;
        var dispatcherType = assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var sendAsync = dispatcherType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public)!;
        var task = (Task)sendAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(commandType)!, CancellationToken.None })!;
        await task;

        var taskType = task.GetType();
        if (taskType.IsGenericType)
        {
            return taskType.GetProperty("Result")!.GetValue(task);
        }

        return null;
    }

    private static IReadOnlyList<string> GetRecorderEntries(Assembly assembly)
    {
        var recorderType = assembly.GetType("Recorder", throwOnError: true)!;
        return (IReadOnlyList<string>)recorderType.GetProperty("Entries", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    }

    private static CompiledAssembly CompileAssembly(string userSource)
    {
        var result = RunGeneratorResult(userSource);
        var errors = result.OutputCompilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.True(errors.Length == 0,
            "Compilation errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(static d => d.ToString())));

        using var peStream = new MemoryStream();
        var emitResult = result.OutputCompilation.Emit(peStream);

        Assert.True(emitResult.Success,
            "Emit errors:" + Environment.NewLine + string.Join(Environment.NewLine, emitResult.Diagnostics.Select(static d => d.ToString())));

        peStream.Position = 0;
        var loadContext = new AssemblyLoadContext($"AutoDispatchTests_{Guid.NewGuid():N}", isCollectible: true);
        var assembly = loadContext.LoadFromStream(peStream);
        return new CompiledAssembly(loadContext, assembly);
    }

    private sealed class GenerationResult
    {
        public GenerationResult(
            Dictionary<string, string> sources,
            ImmutableArray<Diagnostic> diagnostics,
            CSharpCompilation outputCompilation)
        {
            Sources = sources;
            Diagnostics = diagnostics;
            OutputCompilation = outputCompilation;
        }

        public Dictionary<string, string> Sources { get; }

        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public CSharpCompilation OutputCompilation { get; }
    }

    private sealed class CompiledAssembly : IDisposable
    {
        public CompiledAssembly(AssemblyLoadContext loadContext, Assembly assembly)
        {
            LoadContext = loadContext;
            Assembly = assembly;
        }

        public AssemblyLoadContext LoadContext { get; }

        public Assembly Assembly { get; }

        public void Dispose()
        {
            LoadContext.Unload();
        }
    }

    private sealed class ReflectionServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> instances = new();

        public object? GetService(Type serviceType)
        {
            if (instances.TryGetValue(serviceType, out var instance))
            {
                return instance;
            }

            if (serviceType == typeof(IServiceProvider))
            {
                return this;
            }

            var constructors = serviceType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(static ctor => ctor.GetParameters().Length)
                .ToArray();

            if (constructors.Length == 0)
            {
                return null;
            }

            var constructor = constructors[0];
            var parameters = constructor.GetParameters();
            var arguments = new object?[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                arguments[i] = GetService(parameters[i].ParameterType)
                    ?? throw new InvalidOperationException($"Unable to resolve service '{parameters[i].ParameterType}'.");
            }

            instance = constructor.Invoke(arguments);
            instances[serviceType] = instance;
            return instance;
        }
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

    [Fact]
    public void HandlerLifetime_Singleton_EmitsAddSingleton()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class PingCommand { }

[Handler(Lifetime = HandlerLifetime.Singleton)]
public sealed class PingHandler
{
    public void Handle(PingCommand cmd) { }
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddSingleton<global::PingHandler>();", src);
    }

    [Fact]
    public void HandlerLifetime_Transient_EmitsAddTransient()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class PingCommand { }

[Handler(Lifetime = HandlerLifetime.Transient)]
public sealed class PingHandler
{
    public void Handle(PingCommand cmd) { }
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddTransient<global::PingHandler>();", src);
    }

    [Fact]
    public void HandlerLifetime_Default_EmitsAddScoped()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class PingCommand { }

[Handler]
public sealed class PingHandler
{
    public void Handle(PingCommand cmd) { }
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped<global::PingHandler>();", src);
    }

    [Fact]
    public void Attributes_ContainsHandlerLifetimeEnum()
    {
        var sources = RunGenerator(string.Empty, out _);
        var src = sources["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("HandlerLifetime", src);
        Assert.Contains("Singleton", src);
        Assert.Contains("Transient", src);
    }

    [Fact]
    public void CommandHandlerAlias_GeneratesDispatcher()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class CreateOrderCommand { }

[CommandHandler]
public sealed class CreateOrderHandler
{
    public void Handle(CreateOrderCommand cmd) { }
}", out _);

        Assert.True(sources.ContainsKey("AutoDispatch.Dispatcher.g.cs"));
        Assert.Contains("CreateOrderCommand", sources["AutoDispatch.Dispatcher.g.cs"]);
    }

    [Fact]
    public void QueryHandlerAlias_GeneratesDispatcher()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class GetOrdersQuery { }

[QueryHandler]
public sealed class GetOrdersHandler
{
    public void Handle(GetOrdersQuery query) { }
}", out _);

        Assert.True(sources.ContainsKey("AutoDispatch.Dispatcher.g.cs"));
        Assert.Contains("GetOrdersQuery", sources["AutoDispatch.Dispatcher.g.cs"]);
    }

    [Fact]
    public void QueryHandlerAlias_WithLifetime_EmitsCorrectRegistration()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class GetOrdersQuery { }

[QueryHandler(Lifetime = HandlerLifetime.Singleton)]
public sealed class GetOrdersHandler
{
    public void Handle(GetOrdersQuery query) { }
}", out _);

        Assert.Contains("services.AddSingleton<global::GetOrdersHandler>();",
            sources["AutoDispatch.Registration.g.cs"]);
    }

    [Fact]
    public void Attributes_ContainsAllThreeAliases()
    {
        var src = RunGenerator(string.Empty, out _)["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("CommandHandlerAttribute", src);
        Assert.Contains("QueryHandlerAttribute", src);
        Assert.Contains("HandlerAttribute", src);
    }

    [Fact]
    public void BehaviorAttribute_GeneratedInAttributesFile()
    {
        var src = RunGenerator(string.Empty, out _)["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("BehaviorAttribute", src);
    }

    [Fact]
    public void IPipelineBehavior_GeneratedInAttributesFile()
    {
        var src = RunGenerator(string.Empty, out _)["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("IPipelineBehavior", src);
    }

    [Fact]
    public void Unit_GeneratedInAttributesFile()
    {
        var src = RunGenerator(string.Empty, out _)["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("Unit", src);
    }

    [Fact]
    public void OpenGenericBehavior_TaskOfT_WrapsDispatchInPipeline()
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
}

[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult>
{
    public Task<TResult> HandleAsync(TCmd command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("LoggingBehavior<", src);
        Assert.Contains(".HandleAsync(command,", src);
    }

    [Fact]
    public void OpenGenericBehavior_BareTask_WrapsDispatchUsingUnit()
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
}

[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult>
{
    public Task<TResult> HandleAsync(TCmd command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("Unit.Value", src);
        Assert.Contains("LoggingBehavior<", src);
    }

    [Fact]
    public void OpenGenericBehavior_SyncHandler_NotWrapped()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed class DeleteOrderCommand { }

[Handler]
public sealed class DeleteOrderHandler
{
    public void Handle(DeleteOrderCommand cmd) { }
}

[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult>
{
    public System.Threading.Tasks.Task<TResult> HandleAsync(TCmd command, System.Func<System.Threading.Tasks.Task<TResult>> next, System.Threading.CancellationToken ct = default) => next();
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.DoesNotContain("pipeline", src);
    }

    [Fact]
    public void OpenGenericBehavior_RegisteredInDI()
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
}

[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult>
{
    public Task<TResult> HandleAsync(TCmd command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped(typeof(global::LoggingBehavior<,>));", src);
    }

    [Fact]
    public void OpenGenericBehavior_MultipleBehaviors_OrderApplied()
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
}

[Behavior(Order = 0)]
public sealed class FirstBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult>
{
    public Task<TResult> HandleAsync(TCmd command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}

[Behavior(Order = 1)]
public sealed class SecondBehavior<TCmd, TResult> : IPipelineBehavior<TCmd, TResult>
{
    public Task<TResult> HandleAsync(TCmd command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("FirstBehavior<", src);
        Assert.Contains("SecondBehavior<", src);
        // SecondBehavior (Order=1) is inner → written first in source; FirstBehavior (Order=0) is outer → written last
        var secondIdx = src.IndexOf("SecondBehavior<", System.StringComparison.Ordinal);
        var firstIdx = src.LastIndexOf("FirstBehavior<", System.StringComparison.Ordinal);
        Assert.True(secondIdx < firstIdx, "SecondBehavior (inner, Order=1) should appear before FirstBehavior (outer, Order=0) in generated source");
    }

    [Fact]
    public void OpenGenericBehavior_NoBehaviors_SimpleDispatchPreserved()
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
        Assert.Contains("=>", src);
        Assert.DoesNotContain("pipeline", src);
    }

    [Fact]
    public async Task Behavior_Runtime_SingleBehaviorWrapsHandler()
    {
        const string source = @"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class Recorder
{
    public static List<string> Entries { get; } = new List<string>();
}

public sealed class CreateOrderCommand { }
public sealed class OrderId { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler"");
        return Task.FromResult(new OrderId());
    }
}

[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""before"");
        var result = await next();
        Recorder.Entries.Add(""after"");
        return result;
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokeSendAsync(compiled.Assembly, "CreateOrderCommand");

        Assert.Equal(new[] { "before", "handler", "after" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public async Task Behavior_Runtime_MultipleBehaviorsFollowDeclarationOrderWhenOrderMatches()
    {
        const string source = @"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class Recorder
{
    public static List<string> Entries { get; } = new List<string>();
}

public sealed class CreateOrderCommand { }
public sealed class OrderId { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler"");
        return Task.FromResult(new OrderId());
    }
}

[Behavior]
public sealed class FirstBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""first:before"");
        var result = await next();
        Recorder.Entries.Add(""first:after"");
        return result;
    }
}

[Behavior]
public sealed class SecondBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""second:before"");
        var result = await next();
        Recorder.Entries.Add(""second:after"");
        return result;
    }
}";

        using var compiled = CompileAssembly(source);
        var dispatcherType = compiled.Assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var commandType = compiled.Assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var sendAsync = dispatcherType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public)!;
        var task = (Task)sendAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(commandType)!, CancellationToken.None })!;
        await task;

        Assert.Equal(
            new[] { "first:before", "second:before", "handler", "second:after", "first:after" },
            GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public async Task Behavior_Runtime_CanShortCircuitHandler()
    {
        const string source = @"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class Recorder
{
    public static List<string> Entries { get; } = new List<string>();
}

public sealed class CreateOrderCommand { }
public sealed class OrderId
{
    public string Value { get; }
    public OrderId(string value) => Value = value;
}

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler"");
        return Task.FromResult(new OrderId(""handler""));
    }
}

[Behavior(Order = 0)]
public sealed class ShortCircuitBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""short-circuit"");
        return Task.FromResult((TResult)(object)new OrderId(""behavior""));
    }
}";

        using var compiled = CompileAssembly(source);
        var dispatcherType = compiled.Assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var commandType = compiled.Assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var sendAsync = dispatcherType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public)!;
        var task = (Task)sendAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(commandType)!, CancellationToken.None })!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var value = (string)result.GetType().GetProperty("Value")!.GetValue(result)!;

        Assert.Equal("behavior", value);
        Assert.Equal(new[] { "short-circuit" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public async Task Behavior_Runtime_NoBehaviorsStillDispatches()
    {
        const string source = @"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class Recorder
{
    public static List<string> Entries { get; } = new List<string>();
}

public sealed class CreateOrderCommand { }
public sealed class OrderId { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler"");
        return Task.FromResult(new OrderId());
    }
}";

        using var compiled = CompileAssembly(source);
        var dispatcherType = compiled.Assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var commandType = compiled.Assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var sendAsync = dispatcherType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public)!;
        var task = (Task)sendAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(commandType)!, CancellationToken.None })!;
        await task;

        Assert.Equal(new[] { "handler" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public void Diagnostic_AD004_BehaviorMustBeOpenGeneric()
    {
        RunGenerator(@"
using AutoDispatch;

[Behavior]
public sealed class LoggingBehavior
{
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD004" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD005_BehaviorMustImplementPipelineInterface()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[Behavior]
public sealed class LoggingBehavior<TCommand, TResult>
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD005" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD006_BehaviorMustExposePublicHandleAsync()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[Behavior]
public sealed class LoggingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    Task<TResult> IPipelineBehavior<TCommand, TResult>.HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct) => next();
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD006" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void DocComment_ForwardedToInterfaceMethod_SyncHandler()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

public sealed record CreateOrderCommand(string CustomerId);

[Handler]
public sealed class CreateOrderHandler
{
    /// <summary>Creates an order for the given customer.</summary>
    public void Handle(CreateOrderCommand cmd) { }
}", out _);

        var dispatcher = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("/// <summary>Creates an order for the given customer.</summary>", dispatcher);
        Assert.Contains("void Send(global::CreateOrderCommand command);", dispatcher);
    }

    [Fact]
    public void DocComment_ForwardedToInterfaceMethod_AsyncHandlerWithBehavior()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed record CreateOrderCommand(string CustomerId);

[Behavior]
public sealed class LoggingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}

[Handler]
public sealed class CreateOrderHandler
{
    /// <summary>Creates an order asynchronously.</summary>
    /// <param name=""cmd"">The order to create.</param>
    public Task HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var dispatcher = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("/// <summary>Creates an order asynchronously.</summary>", dispatcher);
    }

    [Fact]
    public void PipelineComment_EmittedAboveDispatchMethod_ShowsBehaviorOrder()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed record CreateOrderCommand(string CustomerId);

[Behavior(Order = 0)]
public sealed class LoggingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}

[Behavior(Order = 1)]
public sealed class ValidationBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}

[Handler]
public sealed class CreateOrderHandler
{
    public Task HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var dispatcher = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("// Pipeline: LoggingBehavior -> ValidationBehavior -> CreateOrderHandler.HandleAsync -> LoggingBehavior -> ValidationBehavior", dispatcher);
    }
}
