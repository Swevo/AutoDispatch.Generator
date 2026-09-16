using AutoDispatch.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AutoDispatch.Tests;

/// <summary>
/// Tests for <see cref="MediatRMigrationAnalyzer"/> and <see cref="MediatRMigrationCodeFixProvider"/>
/// (AD100/AD101): detecting MediatR-style handlers and converting them to AutoDispatch. These
/// tests stand in a minimal local <c>MediatR</c> namespace with the well-known interface shapes
/// instead of referencing the real MediatR package, since the analyzer only ever looks up types by
/// fully-qualified metadata name.
/// </summary>
public class MediatRMigrationTests
{
    private const string MediatRStub = @"
using System.Threading;
using System.Threading.Tasks;

namespace MediatR
{
    public interface IRequestHandler<in TRequest, TResponse>
    {
        Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
    }

    public interface IRequestHandler<in TRequest>
    {
        Task Handle(TRequest request, CancellationToken cancellationToken);
    }

    public interface INotificationHandler<in TNotification>
    {
        Task Handle(TNotification notification, CancellationToken cancellationToken);
    }
}
";

    private static async Task<(Document Document, ImmutableArray<Diagnostic> AnalyzerDiagnostics)> CreateDocumentAsync(string userSource)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var mediatRDocId = DocumentId.CreateNewId(projectId);
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
            .AddDocument(mediatRDocId, "MediatR.cs", MediatRStub)
            .AddDocument(userDocId, "User.cs", userSource);

        var project = solution.GetProject(projectId)!;
        var compilation = (await project.GetCompilationAsync())!;

        var driver = CSharpGeneratorDriver.Create(new AutoDispatchGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generatedCompilation, out _);

        var analyzer = new MediatRMigrationAnalyzer();
        var withAnalyzers = generatedCompilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();

        var document = solution.GetDocument(userDocId)!;
        return (document, diagnostics);
    }

    [Fact]
    public async Task AD100_ReportedOnMediatRRequestHandler()
    {
        var (_, diagnostics) = await CreateDocumentAsync(@"
using MediatR;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }

public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, int>
{
    public Task<int> Handle(CreateOrderCommand request, CancellationToken cancellationToken) => Task.FromResult(1);
}");

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "AD100");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("CreateOrderHandler", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AD101_ReportedOnMediatRNotificationHandler()
    {
        var (_, diagnostics) = await CreateDocumentAsync(@"
using MediatR;
using System.Threading;
using System.Threading.Tasks;

public sealed class OrderCreated { }

public sealed class SendEmailOnOrderCreated : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "AD101");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("SendEmailOnOrderCreated", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AD100_NotReportedWhenAlreadyMigrated()
    {
        var (_, diagnostics) = await CreateDocumentAsync(@"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct = default) => Task.FromResult(1);
}");

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD100" || d.Id == "AD101");
    }

    [Fact]
    public async Task AD100_NotReportedWhenMediatRIsNotReferenced()
    {
        var (_, diagnostics) = await CreateDocumentAsync(@"
using System.Threading;
using System.Threading.Tasks;

public sealed class PlainClass
{
    public Task<int> Handle(object request, CancellationToken cancellationToken) => Task.FromResult(1);
}");

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD100" || d.Id == "AD101");
    }

    [Fact]
    public async Task AD100_CodeFix_ConvertsToHandlerAttributeAndRenamesMethod()
    {
        var (document, diagnostics) = await CreateDocumentAsync(@"
using MediatR;
using System.Threading;
using System.Threading.Tasks;

public sealed class CreateOrderCommand { }

public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, int>
{
    public Task<int> Handle(CreateOrderCommand request, CancellationToken cancellationToken) => Task.FromResult(1);
}");

        var ad100 = diagnostics.First(d => d.Id == "AD100");
        var tree = await document.GetSyntaxTreeAsync();
        var mappedDiagnostic = Diagnostic.Create(ad100.Descriptor, Location.Create(tree!, ad100.Location.SourceSpan));

        var provider = new MediatRMigrationCodeFixProvider();
        CodeAction? registeredAction = null;
        var context = new CodeFixContext(document, mappedDiagnostic, (action, _) => registeredAction = action, default);
        await provider.RegisterCodeFixesAsync(context);

        Assert.NotNull(registeredAction);
        var operations = await registeredAction!.GetOperationsAsync(default);
        var applyChanges = operations.OfType<ApplyChangesOperation>().Single();
        var newDocument = applyChanges.ChangedSolution.GetDocument(document.Id)!;
        var newText = (await newDocument.GetTextAsync()).ToString();

        Assert.Contains("[global::AutoDispatch.HandlerAttribute]", newText);
        Assert.DoesNotContain("IRequestHandler<CreateOrderCommand, int>", newText);
        Assert.Contains("public Task<int> HandleAsync(CreateOrderCommand request, CancellationToken cancellationToken)", newText);
    }

    [Fact]
    public async Task AD101_CodeFix_ConvertsToNotificationHandlerAttributeAndRenamesMethod()
    {
        var (document, diagnostics) = await CreateDocumentAsync(@"
using MediatR;
using System.Threading;
using System.Threading.Tasks;

public sealed class OrderCreated { }

public sealed class SendEmailOnOrderCreated : INotificationHandler<OrderCreated>
{
    public Task Handle(OrderCreated notification, CancellationToken cancellationToken) => Task.CompletedTask;
}");

        var ad101 = diagnostics.First(d => d.Id == "AD101");
        var tree = await document.GetSyntaxTreeAsync();
        var mappedDiagnostic = Diagnostic.Create(ad101.Descriptor, Location.Create(tree!, ad101.Location.SourceSpan));

        var provider = new MediatRMigrationCodeFixProvider();
        CodeAction? registeredAction = null;
        var context = new CodeFixContext(document, mappedDiagnostic, (action, _) => registeredAction = action, default);
        await provider.RegisterCodeFixesAsync(context);

        Assert.NotNull(registeredAction);
        var operations = await registeredAction!.GetOperationsAsync(default);
        var applyChanges = operations.OfType<ApplyChangesOperation>().Single();
        var newDocument = applyChanges.ChangedSolution.GetDocument(document.Id)!;
        var newText = (await newDocument.GetTextAsync()).ToString();

        Assert.Contains("[global::AutoDispatch.NotificationHandlerAttribute]", newText);
        Assert.DoesNotContain("INotificationHandler<OrderCreated>", newText);
        Assert.Contains("public Task HandleAsync(OrderCreated notification, CancellationToken cancellationToken)", newText);
    }
}
