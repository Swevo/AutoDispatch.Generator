using AutoDispatch.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AutoDispatch.Tests;

public class CodeFixProviderTests
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
}
";

    private static async Task<(Document Document, ImmutableArray<Diagnostic> Diagnostics)> CreateDocumentAsync(string userSource)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var diDocId = DocumentId.CreateNewId(projectId);
        var userDocId = DocumentId.CreateNewId(projectId);

        var refs = ((string?)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .ToList();

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
            .AddMetadataReferences(projectId, refs)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddDocument(diDocId, "DI.cs", DependencyInjectionStub)
            .AddDocument(userDocId, "User.cs", userSource);

        var project = solution.GetProject(projectId)!;
        var compilation = (await project.GetCompilationAsync())!;

        var driver = CSharpGeneratorDriver.Create(new AutoDispatchGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        var document = solution.GetDocument(userDocId)!;
        return (document, diagnostics);
    }

    [Fact]
    public async Task AD003_CodeFix_AddsCancellationTokenParameter()
    {
        var (document, _) = await CreateDocumentAsync(@"
using AutoDispatch;
using System.Threading.Tasks;

public sealed record CreateOrderCommand(string CustomerId);

[Handler]
public sealed class CreateOrderHandler
{
    public Task HandleAsync(CreateOrderCommand cmd) => Task.CompletedTask;
}");

        var compilation = (await document.Project.GetCompilationAsync())!;
        var driver = CSharpGeneratorDriver.Create(new AutoDispatchGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        var ad003 = diagnostics.First(d => d.Id == "AD003");

        // Diagnostics from the generator point at source locations in the user document/tree,
        // so re-anchor the diagnostic against the workspace document's syntax tree.
        var tree = await document.GetSyntaxTreeAsync();
        var mappedDiagnostic = Diagnostic.Create(ad003.Descriptor, Location.Create(tree!, ad003.Location.SourceSpan));

        var provider = new AddCancellationTokenCodeFixProvider();
        CodeAction? registeredAction = null;
        var context = new CodeFixContext(document, mappedDiagnostic, (action, _) => registeredAction = action, default);
        await provider.RegisterCodeFixesAsync(context);

        Assert.NotNull(registeredAction);
        var operations = await registeredAction!.GetOperationsAsync(default);
        var applyChanges = operations.OfType<ApplyChangesOperation>().Single();
        var newDocument = applyChanges.ChangedSolution.GetDocument(document.Id)!;
        var newText = (await newDocument.GetTextAsync()).ToString();

        Assert.Contains("CancellationToken ct = default", newText);
    }

    [Fact]
    public async Task AD001_CodeFix_AddsHandleAsyncStub()
    {
        var (document, _) = await CreateDocumentAsync(@"
using AutoDispatch;

[Handler]
public sealed class EmptyHandler
{
}");

        var compilation = (await document.Project.GetCompilationAsync())!;
        var driver = CSharpGeneratorDriver.Create(new AutoDispatchGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        var ad001 = diagnostics.First(d => d.Id == "AD001");

        var tree = await document.GetSyntaxTreeAsync();
        var mappedDiagnostic = Diagnostic.Create(ad001.Descriptor, Location.Create(tree!, ad001.Location.SourceSpan));

        var provider = new AddHandleAsyncStubCodeFixProvider();
        CodeAction? registeredAction = null;
        var context = new CodeFixContext(document, mappedDiagnostic, (action, _) => registeredAction = action, default);
        await provider.RegisterCodeFixesAsync(context);

        Assert.NotNull(registeredAction);
        var operations = await registeredAction!.GetOperationsAsync(default);
        var applyChanges = operations.OfType<ApplyChangesOperation>().Single();
        var newDocument = applyChanges.ChangedSolution.GetDocument(document.Id)!;
        var newText = (await newDocument.GetTextAsync()).ToString();

        Assert.Contains("HandleAsync", newText);
        Assert.Contains("NotImplementedException", newText);
    }

    [Fact]
    public async Task AD008_CodeFix_AddsCancellationTokenParameter()
    {
        var (document, _) = await CreateDocumentAsync(@"
using AutoDispatch;
using System.Threading.Tasks;

public sealed record OrderCreated(string OrderId);

[NotificationHandler]
public sealed class LogOrderCreated
{
    public Task HandleAsync(OrderCreated notification) => Task.CompletedTask;
}");

        var compilation = (await document.Project.GetCompilationAsync())!;
        var driver = CSharpGeneratorDriver.Create(new AutoDispatchGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        var ad008 = diagnostics.First(d => d.Id == "AD008");

        var tree = await document.GetSyntaxTreeAsync();
        var mappedDiagnostic = Diagnostic.Create(ad008.Descriptor, Location.Create(tree!, ad008.Location.SourceSpan));

        var provider = new AddCancellationTokenCodeFixProvider();
        CodeAction? registeredAction = null;
        var context = new CodeFixContext(document, mappedDiagnostic, (action, _) => registeredAction = action, default);
        await provider.RegisterCodeFixesAsync(context);

        Assert.NotNull(registeredAction);
        var operations = await registeredAction!.GetOperationsAsync(default);
        var applyChanges = operations.OfType<ApplyChangesOperation>().Single();
        var newDocument = applyChanges.ChangedSolution.GetDocument(document.Id)!;
        var newText = (await newDocument.GetTextAsync()).ToString();

        Assert.Contains("CancellationToken ct = default", newText);
    }

    [Fact]
    public async Task AD007_CodeFix_AddsHandleAsyncStub()
    {
        var (document, _) = await CreateDocumentAsync(@"
using AutoDispatch;

public sealed record OrderCreated(string OrderId);

[NotificationHandler]
public sealed class EmptyNotificationHandler
{
}");

        var compilation = (await document.Project.GetCompilationAsync())!;
        var driver = CSharpGeneratorDriver.Create(new AutoDispatchGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        var ad007 = diagnostics.First(d => d.Id == "AD007");

        var tree = await document.GetSyntaxTreeAsync();
        var mappedDiagnostic = Diagnostic.Create(ad007.Descriptor, Location.Create(tree!, ad007.Location.SourceSpan));

        var provider = new AddHandleAsyncStubCodeFixProvider();
        CodeAction? registeredAction = null;
        var context = new CodeFixContext(document, mappedDiagnostic, (action, _) => registeredAction = action, default);
        await provider.RegisterCodeFixesAsync(context);

        Assert.NotNull(registeredAction);
        var operations = await registeredAction!.GetOperationsAsync(default);
        var applyChanges = operations.OfType<ApplyChangesOperation>().Single();
        var newDocument = applyChanges.ChangedSolution.GetDocument(document.Id)!;
        var newText = (await newDocument.GetTextAsync()).ToString();

        Assert.Contains("HandleAsync", newText);
        Assert.Contains("NotImplementedException", newText);
    }
}
