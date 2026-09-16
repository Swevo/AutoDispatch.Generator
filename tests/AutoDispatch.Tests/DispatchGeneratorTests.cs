using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
        public static IServiceCollection AddScoped<TService>(this IServiceCollection services, System.Func<System.IServiceProvider, TService> factory) => services;
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

    internal static Dictionary<string, string> RunGenerator(string userSource, out ImmutableArray<Diagnostic> diagnostics)
    {
        var result = RunGeneratorResult(userSource);
        diagnostics = result.Diagnostics;
        return result.Sources;
    }

    internal static GenerationResult RunGeneratorForDebug(string userSource) => RunGeneratorResult(userSource);

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

    private static async Task InvokePublishAsync(Assembly assembly, string notificationTypeName)
    {
        var notificationType = assembly.GetType(notificationTypeName, throwOnError: true)!;
        var dispatcherType = assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var publishAsync = dispatcherType.GetMethod("PublishAsync", BindingFlags.Instance | BindingFlags.Public)!;
        var task = (Task)publishAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(notificationType)!, CancellationToken.None })!;
        await task;
    }

    private static async Task<List<int>> InvokeStreamAsync(Assembly assembly, string queryTypeName)
    {
        var queryType = assembly.GetType(queryTypeName, throwOnError: true)!;
        var dispatcherType = assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var streamAsync = dispatcherType.GetMethod("StreamAsync", BindingFlags.Instance | BindingFlags.Public)!;
        var stream = (IAsyncEnumerable<int>)streamAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(queryType)!, CancellationToken.None })!;

        var results = new List<int>();
        await foreach (var item in stream)
        {
            results.Add(item);
        }

        return results;
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

    internal sealed class GenerationResult
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
    public void OpenGenericBehavior_WithMarkerConstraint_OnlyAppliesToMatchingCommand()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public interface IAudited { }

public sealed class AuditedCommand : IAudited { }
public sealed class PlainCommand { }

[Handler]
public sealed class AuditedHandler
{
    public Task<int> HandleAsync(AuditedCommand cmd, CancellationToken ct = default) => Task.FromResult(1);
}

[Handler]
public sealed class PlainHandler
{
    public Task<int> HandleAsync(PlainCommand cmd, CancellationToken ct = default) => Task.FromResult(2);
}

[Behavior(Order = 0)]
public sealed class AuditBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
    where TCommand : IAudited
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];

        // The constrained behavior must be woven into AuditedCommand's SendAsync ...
        Assert.Contains("global::AuditBehavior<global::AuditedCommand", src);

        // ... but PlainCommand doesn't satisfy `where TCommand : IAudited`, so it must fall back
        // to the simple expression-bodied dispatch with no behavior wrapping at all.
        Assert.DoesNotContain("global::AuditBehavior<global::PlainCommand", src);
        Assert.Contains("=> this._sp.GetRequiredService<global::PlainHandler>().HandleAsync(command, ct);", src);
    }

    [Fact]
    public async Task Behavior_Runtime_WithMarkerConstraint_OnlyRunsForMatchingCommand()
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

public interface IAudited { }

public sealed class AuditedCommand : IAudited { }
public sealed class PlainCommand { }

[Handler]
public sealed class AuditedHandler
{
    public Task<int> HandleAsync(AuditedCommand cmd, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""audited-handler"");
        return Task.FromResult(1);
    }
}

[Handler]
public sealed class PlainHandler
{
    public Task<int> HandleAsync(PlainCommand cmd, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""plain-handler"");
        return Task.FromResult(2);
    }
}

[Behavior(Order = 0)]
public sealed class AuditBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
    where TCommand : IAudited
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""audit-behavior"");
        return next();
    }
}";

        using var compiled = CompileAssembly(source);
        var dispatcherType = compiled.Assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;

        var auditedCommandType = compiled.Assembly.GetType("AuditedCommand", throwOnError: true)!;
        var auditedSendAsync = dispatcherType.GetMethod("SendAsync", new[] { auditedCommandType, typeof(CancellationToken) })!;
        var auditedTask = (Task)auditedSendAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(auditedCommandType)!, CancellationToken.None })!;
        await auditedTask;

        var plainCommandType = compiled.Assembly.GetType("PlainCommand", throwOnError: true)!;
        var plainSendAsync = dispatcherType.GetMethod("SendAsync", new[] { plainCommandType, typeof(CancellationToken) })!;
        var plainTask = (Task)plainSendAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(plainCommandType)!, CancellationToken.None })!;
        await plainTask;

        // The behavior only runs ahead of the audited command's handler, never the plain one.
        Assert.Equal(new[] { "audit-behavior", "audited-handler", "plain-handler" }, GetRecorderEntries(compiled.Assembly));
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

    // ---- Notifications / Publish ----

    [Fact]
    public void Attributes_ContainsNotificationHandlerAttribute()
    {
        var sources = RunGenerator(string.Empty, out _);
        var src = sources["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("NotificationHandlerAttribute", src);
    }

    [Fact]
    public void NotificationHandler_GeneratesPublishAsyncOnIDispatcher()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class SendEmailOnOrderCreated
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("PublishAsync(global::OrderCreated notification, global::System.Threading.CancellationToken ct = default)", src);
    }

    [Fact]
    public void NotificationHandler_RegistersHandlerInDI()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class SendEmailOnOrderCreated
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped<global::SendEmailOnOrderCreated>();", src);
    }

    [Fact]
    public void NotificationHandler_MultipleHandlersForSameNotification_DoesNotReportDuplicateDiagnostic()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class SendEmailOnOrderCreated
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}

[NotificationHandler]
public sealed class UpdateAnalyticsOnOrderCreated
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD002");
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("global::SendEmailOnOrderCreated", src);
        Assert.Contains("global::UpdateAnalyticsOnOrderCreated", src);
    }

    [Fact]
    public void NotificationHandler_NoValidHandleAsync_ReportsAD007()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

[NotificationHandler]
public sealed class BrokenHandler
{
    public void HandleAsync(object notification) { }
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD007");
        Assert.DoesNotContain(sources.Keys, k => k == "AutoDispatch.Dispatcher.g.cs" && sources[k].Contains("PublishAsync"));
    }

    [Fact]
    public void NotificationHandler_MissingCancellationToken_ReportsAD008()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading.Tasks;

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class SendEmailOnOrderCreated
{
    public Task HandleAsync(OrderCreated notification) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD008");

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("PublishAsync(global::OrderCreated notification, global::System.Threading.CancellationToken ct = default)", src);
        Assert.Contains("HandleAsync(notification).ConfigureAwait(false);", src);
    }

    [Fact]
    public async Task NotificationHandler_Runtime_FansOutToAllHandlers()
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

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class EmailHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""email"");
        return Task.CompletedTask;
    }
}

[NotificationHandler]
public sealed class AnalyticsHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""analytics"");
        return Task.CompletedTask;
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokePublishAsync(compiled.Assembly, "OrderCreated");

        Assert.Equal(new[] { "analytics", "email" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public async Task NotificationHandler_Runtime_CoexistsWithCommandHandlers()
    {
        const string source = @"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }
public sealed class OrderId { }
public sealed class OrderCreated { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.FromResult(new OrderId());
}

[NotificationHandler]
public sealed class EmailHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}";

        using var compiled = CompileAssembly(source);
        var result = await InvokeSendAsync(compiled.Assembly, "CreateOrderCommand");
        await InvokePublishAsync(compiled.Assembly, "OrderCreated");

        Assert.NotNull(result);
    }

    [Fact]
    public void ParallelPublishAttribute_GeneratedInAttributesFile()
    {
        var sources = RunGenerator("using AutoDispatch;", out _);
        var src = sources["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("ParallelPublishAttribute", src);
    }

    [Fact]
    public void ParallelPublish_NotificationType_GeneratesTaskWhenAll()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[ParallelPublish]
public sealed class OrderCreated { }

[NotificationHandler]
public sealed class EmailHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}

[NotificationHandler]
public sealed class AnalyticsHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("Task.WhenAll(", src);
        Assert.Contains("Publish fan-out (parallel via Task.WhenAll)", src);
    }

    [Fact]
    public void NoParallelPublish_NotificationType_GeneratesSequentialAwaits()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class EmailHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.DoesNotContain("Task.WhenAll(", src);
        Assert.Contains("Publish fan-out (sequential)", src);
    }

    [Fact]
    public async Task ParallelPublish_Runtime_AllHandlersRunAndComplete()
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

[ParallelPublish]
public sealed class OrderCreated { }

[NotificationHandler]
public sealed class EmailHandler
{
    public async Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        await Task.Delay(10, ct);
        lock (Recorder.Entries) { Recorder.Entries.Add(""email""); }
    }
}

[NotificationHandler]
public sealed class AnalyticsHandler
{
    public async Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        lock (Recorder.Entries) { Recorder.Entries.Add(""analytics""); }
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokePublishAsync(compiled.Assembly, "OrderCreated");

        var entries = GetRecorderEntries(compiled.Assembly);
        Assert.Equal(2, entries.Count);
        Assert.Contains("email", entries);
        Assert.Contains("analytics", entries);
    }

    [Fact]
    public async Task ParallelPublish_Runtime_AllHandlersRunEvenIfOneThrows()
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

[ParallelPublish]
public sealed class OrderCreated { }

[NotificationHandler]
public sealed class FailingHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        lock (Recorder.Entries) { Recorder.Entries.Add(""failing""); }
        throw new System.InvalidOperationException(""boom"");
    }
}

[NotificationHandler]
public sealed class AnalyticsHandler
{
    public async Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        lock (Recorder.Entries) { Recorder.Entries.Add(""analytics""); }
    }
}";

        using var compiled = CompileAssembly(source);
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => InvokePublishAsync(compiled.Assembly, "OrderCreated"));

        var entries = GetRecorderEntries(compiled.Assembly);
        Assert.Contains("failing", entries);
        Assert.Contains("analytics", entries);
    }

    // ---- Streaming queries ----

    [Fact]
    public void StreamHandler_GeneratesStreamAsyncOnDispatcher()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default)
    {
        yield return 1;
        yield return 2;
    }
}", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("global::System.Collections.Generic.IAsyncEnumerable<global::System.Int32> StreamAsync(global::GetNumbersQuery query, global::System.Threading.CancellationToken ct = default)", src);
        Assert.Contains("global::GetNumbersHandler", src);

        var registrationSrc = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped<global::GetNumbersHandler>();", registrationSrc);
    }

    [Fact]
    public void StreamHandler_NoValidHandleAsync_ReportsAD009()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

[StreamHandler]
public sealed class BrokenStreamHandler
{
    public void HandleAsync(object query) { }
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD009");
        Assert.DoesNotContain(sources.Keys, k => k == "AutoDispatch.Dispatcher.g.cs" && sources[k].Contains("StreamAsync"));
    }

    [Fact]
    public void StreamHandler_DuplicateHandlersForSameQuery_ReportsAD010()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandlerA
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default) { yield return 1; }
}

[StreamHandler]
public sealed class GetNumbersHandlerB
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default) { yield return 2; }
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD010");
    }

    [Fact]
    public void StreamHandler_MissingCancellationToken_ReportsAD011()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Collections.Generic;

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query)
    {
        yield return 1;
    }
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD011");

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("StreamAsync(global::GetNumbersQuery query, global::System.Threading.CancellationToken ct = default)", src);
        Assert.Contains("HandleAsync(query);", src);
    }

    [Fact]
    public async Task StreamHandler_Runtime_YieldsAllItemsInOrder()
    {
        const string source = @"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default)
    {
        yield return 1;
        yield return 2;
        yield return 3;
    }
}";

        using var compiled = CompileAssembly(source);
        var results = await InvokeStreamAsync(compiled.Assembly, "GetNumbersQuery");

        Assert.Equal(new[] { 1, 2, 3 }, results);
    }

    [Fact]
    public async Task StreamHandler_Runtime_CoexistsWithCommandAndNotificationHandlers()
    {
        const string source = @"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }
public sealed class OrderId { }
public sealed class OrderCreated { }
public sealed class GetNumbersQuery { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.FromResult(new OrderId());
}

[NotificationHandler]
public sealed class EmailHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default)
    {
        yield return 42;
    }
}";

        using var compiled = CompileAssembly(source);
        var sendResult = await InvokeSendAsync(compiled.Assembly, "CreateOrderCommand");
        await InvokePublishAsync(compiled.Assembly, "OrderCreated");
        var streamResults = await InvokeStreamAsync(compiled.Assembly, "GetNumbersQuery");

        Assert.NotNull(sendResult);
        Assert.Equal(new[] { 42 }, streamResults);
    }

    [Fact]
    public void StreamBehaviorAttribute_GeneratedInAttributesFile()
    {
        var sources = RunGenerator("using AutoDispatch;", out _);
        var src = sources["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("StreamBehaviorAttribute", src);
    }

    [Fact]
    public void IStreamPipelineBehavior_GeneratedInAttributesFile()
    {
        var sources = RunGenerator("using AutoDispatch;", out _);
        var src = sources["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("IStreamPipelineBehavior", src);
    }

    [Fact]
    public void OpenGenericStreamBehavior_WrapsStreamInPipeline()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default)
    {
        yield return 1;
    }
}

[StreamBehavior(Order = 0)]
public sealed class LoggingStreamBehavior<TQuery, TResult> : IStreamPipelineBehavior<TQuery, TResult>
{
    public IAsyncEnumerable<TResult> HandleAsync(TQuery query, System.Func<IAsyncEnumerable<TResult>> next, CancellationToken ct = default) => next();
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("LoggingStreamBehavior<", src);
        Assert.Contains("Stream pipeline:", src);
    }

    [Fact]
    public void OpenGenericStreamBehavior_RegisteredInDI()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default)
    {
        yield return 1;
    }
}

[StreamBehavior(Order = 0)]
public sealed class LoggingStreamBehavior<TQuery, TResult> : IStreamPipelineBehavior<TQuery, TResult>
{
    public IAsyncEnumerable<TResult> HandleAsync(TQuery query, System.Func<IAsyncEnumerable<TResult>> next, CancellationToken ct = default) => next();
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped(typeof(global::LoggingStreamBehavior<,>));", src);
    }

    [Fact]
    public void OpenGenericStreamBehavior_NoStreamBehaviors_SimpleDispatchPreserved()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default)
    {
        yield return 1;
    }
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.DoesNotContain("Stream pipeline:", src);
        Assert.DoesNotContain("_sb0", src);
    }

    [Fact]
    public async Task StreamBehavior_Runtime_SingleBehaviorWrapsHandlerAndForwardsAllItems()
    {
        const string source = @"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

public static class Recorder
{
    public static List<string> Entries { get; } = new List<string>();
}

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler:start"");
        yield return 1;
        yield return 2;
        Recorder.Entries.Add(""handler:end"");
    }
}

[StreamBehavior(Order = 0)]
public sealed class LoggingStreamBehavior<TQuery, TResult> : IStreamPipelineBehavior<TQuery, TResult>
{
    public async IAsyncEnumerable<TResult> HandleAsync(TQuery query, System.Func<IAsyncEnumerable<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""before"");
        await foreach (var item in next())
        {
            yield return item;
        }
        Recorder.Entries.Add(""after"");
    }
}";

        using var compiled = CompileAssembly(source);
        var results = await InvokeStreamAsync(compiled.Assembly, "GetNumbersQuery");

        Assert.Equal(new[] { 1, 2 }, results);
        Assert.Equal(new[] { "before", "handler:start", "handler:end", "after" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public async Task StreamBehavior_Runtime_MultipleBehaviorsFollowOrder()
    {
        const string source = @"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

public static class Recorder
{
    public static List<string> Entries { get; } = new List<string>();
}

public sealed class GetNumbersQuery { }

[StreamHandler]
public sealed class GetNumbersHandler
{
    public async IAsyncEnumerable<int> HandleAsync(GetNumbersQuery query, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler"");
        yield return 1;
    }
}

[StreamBehavior(Order = 0)]
public sealed class FirstStreamBehavior<TQuery, TResult> : IStreamPipelineBehavior<TQuery, TResult>
{
    public async IAsyncEnumerable<TResult> HandleAsync(TQuery query, System.Func<IAsyncEnumerable<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""first:before"");
        await foreach (var item in next())
        {
            yield return item;
        }
        Recorder.Entries.Add(""first:after"");
    }
}

[StreamBehavior(Order = 1)]
public sealed class SecondStreamBehavior<TQuery, TResult> : IStreamPipelineBehavior<TQuery, TResult>
{
    public async IAsyncEnumerable<TResult> HandleAsync(TQuery query, System.Func<IAsyncEnumerable<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""second:before"");
        await foreach (var item in next())
        {
            yield return item;
        }
        Recorder.Entries.Add(""second:after"");
    }
}";

        using var compiled = CompileAssembly(source);
        var results = await InvokeStreamAsync(compiled.Assembly, "GetNumbersQuery");

        Assert.Equal(new[] { 1 }, results);
        Assert.Equal(
            new[] { "first:before", "second:before", "handler", "second:after", "first:after" },
            GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public void Diagnostic_AD012_StreamBehaviorMustBeOpenGeneric()
    {
        RunGenerator(@"
using AutoDispatch;

[StreamBehavior]
public sealed class LoggingStreamBehavior
{
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD012" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD013_StreamBehaviorMustImplementStreamPipelineInterface()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

[StreamBehavior]
public sealed class LoggingStreamBehavior<TQuery, TResult>
{
    public IAsyncEnumerable<TResult> HandleAsync(TQuery query, System.Func<IAsyncEnumerable<TResult>> next, CancellationToken ct = default) => next();
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD013" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD014_StreamBehaviorMustExposePublicHandleAsync()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Collections.Generic;
using System.Threading;

[StreamBehavior]
public sealed class LoggingStreamBehavior<TQuery, TResult> : IStreamPipelineBehavior<TQuery, TResult>
{
    IAsyncEnumerable<TResult> IStreamPipelineBehavior<TQuery, TResult>.HandleAsync(TQuery query, System.Func<IAsyncEnumerable<TResult>> next, CancellationToken ct) => next();
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD014" && d.Severity == DiagnosticSeverity.Error);
    }

    // ---- Exception handling middleware ----

    [Fact]
    public void ExceptionHandlerAttribute_GeneratedInAttributesFile()
    {
        var src = RunGenerator(string.Empty, out _)["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("ExceptionHandlerAttribute", src);
        Assert.Contains("IExceptionHandler", src);
        Assert.Contains("ExceptionHandlerResult", src);
    }

    [Fact]
    public void ExceptionHandler_TaskOfT_GeneratesTryCatch()
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

public sealed class ValidationException : System.Exception { }

[ExceptionHandler]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
        => Task.FromResult(ExceptionHandlerResult<TResult>.Unhandled());
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("public async ", src);
        Assert.Contains("catch (global::ValidationException ex)", src);
        Assert.Contains("ValidationExceptionHandler<", src);
        Assert.Contains(".IsHandled", src);
    }

    [Fact]
    public void ExceptionHandler_NoHandlers_SimpleDispatchPreserved()
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

        // TracingDispatcher always wraps calls in try/catch (to record Activity error status), so
        // scope this assertion to the plain Dispatcher class, which should stay try/catch-free when
        // there are no exception handlers.
        var dispatcherClassStart = src.IndexOf("internal sealed class Dispatcher", StringComparison.Ordinal);
        var tracingClassStart = src.IndexOf("internal sealed class TracingDispatcher", StringComparison.Ordinal);
        Assert.True(dispatcherClassStart >= 0 && tracingClassStart > dispatcherClassStart);
        var dispatcherClassSrc = src.Substring(dispatcherClassStart, tracingClassStart - dispatcherClassStart);

        Assert.DoesNotContain("            try", dispatcherClassSrc);
        Assert.DoesNotContain("catch (", dispatcherClassSrc);
    }

    [Fact]
    public void ExceptionHandler_RegisteredInDI()
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

public sealed class ValidationException : System.Exception { }

[ExceptionHandler]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
        => Task.FromResult(ExceptionHandlerResult<TResult>.Unhandled());
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped(typeof(global::ValidationExceptionHandler<,>));", src);
    }

    [Fact]
    public void ExceptionHandler_MostDerivedExceptionCaughtFirst()
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

public sealed class ValidationException : System.Exception { }

[ExceptionHandler]
public sealed class GeneralExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, System.Exception>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, System.Exception exception, CancellationToken ct = default)
        => Task.FromResult(ExceptionHandlerResult<TResult>.Unhandled());
}

[ExceptionHandler]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
        => Task.FromResult(ExceptionHandlerResult<TResult>.Unhandled());
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        var validationIdx = src.IndexOf("catch (global::ValidationException ex)", System.StringComparison.Ordinal);
        var generalIdx = src.IndexOf("catch (global::System.Exception ex)", System.StringComparison.Ordinal);
        Assert.True(validationIdx >= 0 && generalIdx >= 0);
        Assert.True(validationIdx < generalIdx, "The more-derived ValidationException catch must be emitted before the base System.Exception catch, or the compiler would reject the base catch as unreachable-shadowing.");
    }

    [Fact]
    public async Task ExceptionHandler_Runtime_HandlesExceptionAndReturnsFallback()
    {
        const string source = @"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

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
        => throw new ValidationException();
}

public sealed class ValidationException : System.Exception { }

[ExceptionHandler]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
        => Task.FromResult(ExceptionHandlerResult<TResult>.Handled((TResult)(object)new OrderId(""fallback"")));
}";

        using var compiled = CompileAssembly(source);
        var result = await InvokeSendAsync(compiled.Assembly, "CreateOrderCommand");

        var valueProperty = result!.GetType().GetProperty("Value")!;
        Assert.Equal("fallback", valueProperty.GetValue(result));
    }

    [Fact]
    public async Task ExceptionHandler_Runtime_UnhandledExceptionPropagates()
    {
        const string source = @"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }
public sealed class OrderId { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default)
        => throw new System.InvalidOperationException(""boom"");
}

public sealed class ValidationException : System.Exception { }

[ExceptionHandler]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
        => Task.FromResult(ExceptionHandlerResult<TResult>.Unhandled());
}";

        using var compiled = CompileAssembly(source);
        var dispatcherType = compiled.Assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var commandType = compiled.Assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var sendAsync = dispatcherType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public)!;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var task = (Task)sendAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(commandType)!, CancellationToken.None })!;
            await task;
        });
    }

    [Fact]
    public async Task ExceptionHandler_Runtime_VoidAsyncHandlerCanBeHandled()
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

public sealed class PingCommand { }

[Handler]
public sealed class PingHandler
{
    public Task HandleAsync(PingCommand cmd, CancellationToken ct = default)
        => throw new ValidationException();
}

public sealed class ValidationException : System.Exception { }

[ExceptionHandler]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handled"");
        return Task.FromResult(ExceptionHandlerResult<TResult>.Handled(default!));
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokeSendAsync(compiled.Assembly, "PingCommand");

        Assert.Equal(new[] { "handled" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public void Diagnostic_AD015_ExceptionHandlerMustBeOpenGeneric()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class ValidationException : System.Exception { }

[ExceptionHandler]
public sealed class ValidationExceptionHandler : IExceptionHandler<object, object, ValidationException>
{
    public Task<ExceptionHandlerResult<object>> HandleAsync(object command, ValidationException exception, CancellationToken ct = default)
        => Task.FromResult(ExceptionHandlerResult<object>.Unhandled());
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD015" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD016_ExceptionHandlerMustImplementInterfaceWithFixedException()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[ExceptionHandler]
public sealed class NotAHandler<TCommand, TResult>
{
    public Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default) => default!;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD016" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD017_ExceptionHandlerMustExposePublicHandleAsync()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class ValidationException : System.Exception { }

[ExceptionHandler]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    Task<ExceptionHandlerResult<TResult>> IExceptionHandler<TCommand, TResult, ValidationException>.HandleAsync(TCommand command, ValidationException exception, CancellationToken ct)
        => Task.FromResult(ExceptionHandlerResult<TResult>.Unhandled());
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD017" && d.Severity == DiagnosticSeverity.Error);
    }

    // ---- Exception actions ----

    [Fact]
    public void ExceptionActionAttribute_GeneratedInAttributesFile()
    {
        var src = RunGenerator(string.Empty, out _)["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("ExceptionActionAttribute", src);
        Assert.Contains("IExceptionAction", src);
    }

    [Fact]
    public void ExceptionAction_GeneratesTryCatchWithExecuteAsyncCall()
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

public sealed class ValidationException : System.Exception { }

[ExceptionAction]
public sealed class LoggingExceptionAction<TCommand> : IExceptionAction<TCommand, ValidationException>
{
    public Task ExecuteAsync(TCommand command, ValidationException exception, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("catch (global::ValidationException ex)", src);
        Assert.Contains("LoggingExceptionAction<", src);
        Assert.Contains(".ExecuteAsync(command, ex, ct)", src);
    }

    [Fact]
    public void ExceptionAction_RegisteredInDI()
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

public sealed class ValidationException : System.Exception { }

[ExceptionAction]
public sealed class LoggingExceptionAction<TCommand> : IExceptionAction<TCommand, ValidationException>
{
    public Task ExecuteAsync(TCommand command, ValidationException exception, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped(typeof(global::LoggingExceptionAction<>));", src);
    }

    [Fact]
    public async Task ExceptionAction_Runtime_RunsBeforeHandlerAndDoesNotSuppress()
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
        => throw new ValidationException();
}

public sealed class ValidationException : System.Exception { }

[ExceptionAction]
public sealed class LoggingExceptionAction<TCommand> : IExceptionAction<TCommand, ValidationException>
{
    public Task ExecuteAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""action"");
        return Task.CompletedTask;
    }
}

[ExceptionHandler]
public sealed class ValidationExceptionHandler<TCommand, TResult> : IExceptionHandler<TCommand, TResult, ValidationException>
{
    public Task<ExceptionHandlerResult<TResult>> HandleAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler"");
        return Task.FromResult(ExceptionHandlerResult<TResult>.Handled((TResult)(object)new OrderId(""fallback"")));
    }
}";

        using var compiled = CompileAssembly(source);
        var result = await InvokeSendAsync(compiled.Assembly, "CreateOrderCommand");

        Assert.Equal(new[] { "action", "handler" }, GetRecorderEntries(compiled.Assembly));
        var valueProperty = result!.GetType().GetProperty("Value")!;
        Assert.Equal("fallback", valueProperty.GetValue(result));
    }

    [Fact]
    public async Task ExceptionAction_Runtime_AlwaysRunsEvenWithNoHandler()
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
        => throw new ValidationException();
}

public sealed class ValidationException : System.Exception { }

[ExceptionAction]
public sealed class LoggingExceptionAction<TCommand> : IExceptionAction<TCommand, ValidationException>
{
    public Task ExecuteAsync(TCommand command, ValidationException exception, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""action"");
        return Task.CompletedTask;
    }
}";

        using var compiled = CompileAssembly(source);
        var dispatcherType = compiled.Assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var dispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var commandType = compiled.Assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var sendAsync = dispatcherType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public)!;

        Exception? thrown = null;
        try
        {
            var task = (Task)sendAsync.Invoke(dispatcher, new object[] { Activator.CreateInstance(commandType)!, CancellationToken.None })!;
            await task;
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.NotNull(thrown);
        Assert.Equal(new[] { "action" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public void Diagnostic_AD018_ExceptionActionMustBeOpenGeneric()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class ValidationException : System.Exception { }

[ExceptionAction]
public sealed class LoggingExceptionAction : IExceptionAction<object, ValidationException>
{
    public Task ExecuteAsync(object command, ValidationException exception, CancellationToken ct = default) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD018" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD019_ExceptionActionMustImplementInterfaceWithFixedException()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[ExceptionAction]
public sealed class NotAnAction<TCommand>
{
    public Task ExecuteAsync(TCommand command, CancellationToken ct = default) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD019" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD020_ExceptionActionMustExposePublicExecuteAsync()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class ValidationException : System.Exception { }

[ExceptionAction]
public sealed class LoggingExceptionAction<TCommand> : IExceptionAction<TCommand, ValidationException>
{
    Task IExceptionAction<TCommand, ValidationException>.ExecuteAsync(TCommand command, ValidationException exception, CancellationToken ct)
        => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD020" && d.Severity == DiagnosticSeverity.Error);
    }

    // ---- Pre/post processors ----

    [Fact]
    public void PreProcessorAttribute_GeneratedInAttributesFile()
    {
        var src = RunGenerator(string.Empty, out _)["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("PreProcessorAttribute", src);
        Assert.Contains("IPreProcessor", src);
        Assert.Contains("PostProcessorAttribute", src);
        Assert.Contains("IPostProcessor", src);
    }

    [Fact]
    public void PreProcessor_GeneratesCallBeforeHandlerInvocation()
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

[PreProcessor]
public sealed class LoggingPreProcessor<TCommand> : IPreProcessor<TCommand>
{
    public Task ProcessAsync(TCommand command, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("LoggingPreProcessor<", src);
        Assert.Contains(".ProcessAsync(command, ct)", src);
    }

    [Fact]
    public void PostProcessor_GeneratesCallAfterHandlerInvocationWithResponse()
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

[PostProcessor]
public sealed class LoggingPostProcessor<TCommand, TResult> : IPostProcessor<TCommand, TResult>
{
    public Task ProcessAsync(TCommand command, TResult response, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("LoggingPostProcessor<", src);
        Assert.Contains(".ProcessAsync(command, _result, ct)", src);
    }

    [Fact]
    public void PreAndPostProcessors_RegisteredInDI()
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

[PreProcessor]
public sealed class LoggingPreProcessor<TCommand> : IPreProcessor<TCommand>
{
    public Task ProcessAsync(TCommand command, CancellationToken ct = default) => Task.CompletedTask;
}

[PostProcessor]
public sealed class LoggingPostProcessor<TCommand, TResult> : IPostProcessor<TCommand, TResult>
{
    public Task ProcessAsync(TCommand command, TResult response, CancellationToken ct = default) => Task.CompletedTask;
}", out _);

        var src = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped(typeof(global::LoggingPreProcessor<>));", src);
        Assert.Contains("services.AddScoped(typeof(global::LoggingPostProcessor<,>));", src);
    }

    [Fact]
    public async Task PreAndPostProcessors_Runtime_RunInCorrectOrderWithResponse()
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
        return Task.FromResult(new OrderId(""created""));
    }
}

[PreProcessor]
public sealed class LoggingPreProcessor<TCommand> : IPreProcessor<TCommand>
{
    public Task ProcessAsync(TCommand command, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""pre"");
        return Task.CompletedTask;
    }
}

[PostProcessor]
public sealed class LoggingPostProcessor<TCommand, TResult> : IPostProcessor<TCommand, TResult>
{
    public Task ProcessAsync(TCommand command, TResult response, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""post:"" + ((OrderId)(object)response!).Value);
        return Task.CompletedTask;
    }
}";

        using var compiled = CompileAssembly(source);
        var result = await InvokeSendAsync(compiled.Assembly, "CreateOrderCommand");

        Assert.Equal(new[] { "pre", "handler", "post:created" }, GetRecorderEntries(compiled.Assembly));
        var valueProperty = result!.GetType().GetProperty("Value")!;
        Assert.Equal("created", valueProperty.GetValue(result));
    }

    [Fact]
    public async Task PreAndPostProcessors_Runtime_WrapAroundCustomBehaviors()
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
public sealed class LoggingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""behavior-before"");
        var result = await next();
        Recorder.Entries.Add(""behavior-after"");
        return result;
    }
}

[PreProcessor]
public sealed class LoggingPreProcessor<TCommand> : IPreProcessor<TCommand>
{
    public Task ProcessAsync(TCommand command, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""pre"");
        return Task.CompletedTask;
    }
}

[PostProcessor]
public sealed class LoggingPostProcessor<TCommand, TResult> : IPostProcessor<TCommand, TResult>
{
    public Task ProcessAsync(TCommand command, TResult response, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""post"");
        return Task.CompletedTask;
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokeSendAsync(compiled.Assembly, "CreateOrderCommand");

        // Pre/post processors sit innermost, right around the handler call, inside custom behaviors.
        Assert.Equal(
            new[] { "behavior-before", "pre", "handler", "post", "behavior-after" },
            GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public void Diagnostic_AD021_PreProcessorMustBeOpenGeneric()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[PreProcessor]
public sealed class LoggingPreProcessor : IPreProcessor<object>
{
    public Task ProcessAsync(object command, CancellationToken ct = default) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD021" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD022_PreProcessorMustImplementInterface()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[PreProcessor]
public sealed class NotAPreProcessor<TCommand>
{
    public Task ProcessAsync(TCommand command, CancellationToken ct = default) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD022" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD023_PreProcessorMustExposePublicProcessAsync()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[PreProcessor]
public sealed class LoggingPreProcessor<TCommand> : IPreProcessor<TCommand>
{
    Task IPreProcessor<TCommand>.ProcessAsync(TCommand command, CancellationToken ct) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD023" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD024_PostProcessorMustBeOpenGeneric()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[PostProcessor]
public sealed class LoggingPostProcessor : IPostProcessor<object, object>
{
    public Task ProcessAsync(object command, object response, CancellationToken ct = default) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD024" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD025_PostProcessorMustImplementInterface()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[PostProcessor]
public sealed class NotAPostProcessor<TCommand, TResult>
{
    public Task ProcessAsync(TCommand command, TResult response, CancellationToken ct = default) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD025" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD026_PostProcessorMustExposePublicProcessAsync()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[PostProcessor]
public sealed class LoggingPostProcessor<TCommand, TResult> : IPostProcessor<TCommand, TResult>
{
    Task IPostProcessor<TCommand, TResult>.ProcessAsync(TCommand command, TResult response, CancellationToken ct) => Task.CompletedTask;
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD026" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Diagnostic_AD027_ConstrainedBehaviorMatchingNoCommand_ReportsWarning()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public interface IAudited { }

// No command in this compilation implements IAudited.
public sealed class PlainCommand { }

[Handler]
public sealed class PlainHandler
{
    public Task<int> HandleAsync(PlainCommand cmd, CancellationToken ct = default) => Task.FromResult(1);
}

[Behavior(Order = 0)]
public sealed class AuditBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
    where TCommand : IAudited
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}", out var diagnostics);

        var warning = Assert.Single(diagnostics, d => d.Id == "AD027");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("AuditBehavior", warning.GetMessage());
        Assert.Contains("IAudited", warning.GetMessage());

        // Since AuditBehavior never matches, PlainCommand's SendAsync should stay unwrapped.
        var src = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.DoesNotContain("AuditBehavior<", src);
    }

    [Fact]
    public void Diagnostic_AD027_ConstrainedBehaviorMatchingSomeCommand_NoWarning()
    {
        RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public interface IAudited { }

public sealed class AuditedCommand : IAudited { }

[Handler]
public sealed class AuditedHandler
{
    public Task<int> HandleAsync(AuditedCommand cmd, CancellationToken ct = default) => Task.FromResult(1);
}

[Behavior(Order = 0)]
public sealed class AuditBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
    where TCommand : IAudited
{
    public Task<TResult> HandleAsync(TCommand command, System.Func<Task<TResult>> next, CancellationToken ct = default) => next();
}", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD027");
    }

    [Fact]
    public void Tracing_GeneratesTelemetryAndTracingDispatcher()
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

        var dispatcherSrc = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("public static class AutoDispatchTelemetry", dispatcherSrc);
        Assert.Contains("public const string ActivitySourceName = \"AutoDispatch\";", dispatcherSrc);
        Assert.Contains("internal sealed class TracingDispatcher : IDispatcher", dispatcherSrc);
        Assert.Contains("AutoDispatchTelemetry.ActivitySource.StartActivity(\"AutoDispatch.SendAsync\"", dispatcherSrc);

        var registrationSrc = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("public sealed class AutoDispatchOptions", registrationSrc);
        Assert.Contains("EnableTracing", registrationSrc);
        Assert.Contains("new global::AutoDispatch.TracingDispatcher(", registrationSrc);
    }

    [Fact]
    public async Task Tracing_Runtime_WrapsSendAsyncInActivityAndPreservesResult()
    {
        const string source = @"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }
public sealed class OrderId
{
    public string Value { get; }
    public OrderId(string value) => Value = value;
}

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.FromResult(new OrderId(""created""));
}";

        using var compiled = CompileAssembly(source);
        var dispatcherType = compiled.Assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var tracingDispatcherType = compiled.Assembly.GetType("AutoDispatch.TracingDispatcher", throwOnError: true)!;
        var innerDispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var tracingDispatcher = Activator.CreateInstance(tracingDispatcherType, innerDispatcher)!;
        var commandType = compiled.Assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var sendAsync = tracingDispatcherType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public)!;

        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == "AutoDispatch",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            var task = (Task)sendAsync.Invoke(tracingDispatcher, new object[] { Activator.CreateInstance(commandType)!, CancellationToken.None })!;
            await task;

            var resultProperty = task.GetType().GetProperty("Result")!;
            var orderId = resultProperty.GetValue(task)!;
            Assert.Equal("created", (string)orderId.GetType().GetProperty("Value")!.GetValue(orderId)!);
        }
        finally
        {
            listener.Dispose();
        }

        var activity = Assert.Single(activities);
        Assert.Equal("AutoDispatch.SendAsync", activity.OperationName);
        Assert.Equal("CreateOrderCommand", activity.Tags.Single(t => t.Key == "autodispatch.command_type").Value);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
    }

    [Fact]
    public async Task Tracing_Runtime_RecordsErrorStatusWhenHandlerThrows()
    {
        const string source = @"
using AutoDispatch;
using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }
public sealed class OrderId { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<OrderId> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => throw new InvalidOperationException(""boom"");
}";

        using var compiled = CompileAssembly(source);
        var dispatcherType = compiled.Assembly.GetType("AutoDispatch.Dispatcher", throwOnError: true)!;
        var tracingDispatcherType = compiled.Assembly.GetType("AutoDispatch.TracingDispatcher", throwOnError: true)!;
        var innerDispatcher = Activator.CreateInstance(dispatcherType, new ReflectionServiceProvider())!;
        var tracingDispatcher = Activator.CreateInstance(tracingDispatcherType, innerDispatcher)!;
        var commandType = compiled.Assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var sendAsync = tracingDispatcherType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.Public)!;

        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == "AutoDispatch",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            var task = (Task)sendAsync.Invoke(tracingDispatcher, new object[] { Activator.CreateInstance(commandType)!, CancellationToken.None })!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }
        finally
        {
            listener.Dispose();
        }

        var activity = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    // ---- Notification pipeline behaviors ([NotificationBehavior]) ----

    [Fact]
    public void Attributes_ContainsNotificationBehaviorAttributeAndInterface()
    {
        var sources = RunGenerator(string.Empty, out _);
        var src = sources["AutoDispatch.Attributes.g.cs"];
        Assert.Contains("NotificationBehaviorAttribute", src);
        Assert.Contains("INotificationPipelineBehavior<TNotification>", src);
    }

    [Fact]
    public void NotificationBehavior_WrapsPublishAsyncAndRegistersInDI()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class SendEmailOnOrderCreated
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default) => Task.CompletedTask;
}

[NotificationBehavior(Order = 0)]
public sealed class LoggingNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
{
    public Task HandleAsync(TNotification notification, System.Func<Task> next, CancellationToken ct = default) => next();
}", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var dispatcher = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("LoggingNotificationBehavior<global::OrderCreated>", dispatcher);

        var registration = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("services.AddScoped(typeof(global::LoggingNotificationBehavior<>));", registration);
    }

    [Fact]
    public void NotificationBehavior_NotAGenericClass_ReportsAD028()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[NotificationBehavior]
public sealed class BrokenNotificationBehavior : INotificationPipelineBehavior<object>
{
    public Task HandleAsync(object notification, System.Func<Task> next, CancellationToken ct = default) => next();
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD028" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NotificationBehavior_DoesNotImplementInterface_ReportsAD029()
    {
        var sources = RunGenerator(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

[NotificationBehavior]
public sealed class BrokenNotificationBehavior<TNotification>
{
    public Task HandleAsync(TNotification notification, System.Func<Task> next, CancellationToken ct = default) => next();
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD029" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NotificationBehavior_InvalidHandleAsyncSignature_ReportsAD030()
    {
        var sources = RunGenerator(@"
using AutoDispatch;

[NotificationBehavior]
public sealed class BrokenNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
{
    public void HandleAsync(TNotification notification) { }
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD030" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task NotificationBehavior_Runtime_WrapsSequentialFanOut()
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

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class FirstHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""first"");
        return Task.CompletedTask;
    }
}

[NotificationHandler]
public sealed class SecondHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""second"");
        return Task.CompletedTask;
    }
}

[NotificationBehavior(Order = 0)]
public sealed class LoggingNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
{
    public async Task HandleAsync(TNotification notification, System.Func<Task> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""before"");
        await next();
        Recorder.Entries.Add(""after"");
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokePublishAsync(compiled.Assembly, "OrderCreated");

        Assert.Equal(new[] { "before", "first", "second", "after" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public async Task NotificationBehavior_Runtime_MultipleBehaviorsFollowDeclarationOrder()
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

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class OnlyHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler"");
        return Task.CompletedTask;
    }
}

[NotificationBehavior]
public sealed class FirstBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
{
    public async Task HandleAsync(TNotification notification, System.Func<Task> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""first:before"");
        await next();
        Recorder.Entries.Add(""first:after"");
    }
}

[NotificationBehavior]
public sealed class SecondBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
{
    public async Task HandleAsync(TNotification notification, System.Func<Task> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""second:before"");
        await next();
        Recorder.Entries.Add(""second:after"");
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokePublishAsync(compiled.Assembly, "OrderCreated");

        Assert.Equal(
            new[] { "first:before", "second:before", "handler", "second:after", "first:after" },
            GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public async Task NotificationBehavior_Runtime_CanShortCircuitFanOut()
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

public sealed class OrderCreated { }

[NotificationHandler]
public sealed class OnlyHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""handler"");
        return Task.CompletedTask;
    }
}

[NotificationBehavior]
public sealed class ShortCircuitBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
{
    public Task HandleAsync(TNotification notification, System.Func<Task> next, CancellationToken ct = default)
    {
        Recorder.Entries.Add(""short-circuit"");
        return Task.CompletedTask;
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokePublishAsync(compiled.Assembly, "OrderCreated");

        Assert.Equal(new[] { "short-circuit" }, GetRecorderEntries(compiled.Assembly));
    }

    [Fact]
    public async Task NotificationBehavior_Runtime_WrapsParallelPublishFanOut()
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

[ParallelPublish]
public sealed class OrderCreated { }

[NotificationHandler]
public sealed class FirstHandler
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct = default)
    {
        lock (Recorder.Entries) { Recorder.Entries.Add(""handler""); }
        return Task.CompletedTask;
    }
}

[NotificationBehavior(Order = 0)]
public sealed class LoggingNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
{
    public async Task HandleAsync(TNotification notification, System.Func<Task> next, CancellationToken ct = default)
    {
        lock (Recorder.Entries) { Recorder.Entries.Add(""before""); }
        await next();
        lock (Recorder.Entries) { Recorder.Entries.Add(""after""); }
    }
}";

        using var compiled = CompileAssembly(source);
        await InvokePublishAsync(compiled.Assembly, "OrderCreated");

        Assert.Equal(new[] { "before", "handler", "after" }, GetRecorderEntries(compiled.Assembly));
    }
}

