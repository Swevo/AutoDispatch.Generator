using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoDispatch.CodeFixes;

/// <summary>
/// Quick fix for AD003, AD008, and AD011: adds the missing <c>CancellationToken ct = default</c>
/// parameter to a <c>HandleAsync</c> method so cancellation flows through the generated
/// dispatcher/publisher/stream.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddCancellationTokenCodeFixProvider))]
[Shared]
public sealed class AddCancellationTokenCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("AD003", "AD008", "AD011");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan);
        var methodDeclaration = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (methodDeclaration is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Add CancellationToken parameter",
                createChangedDocument: ct => AddCancellationTokenAsync(context.Document, methodDeclaration, ct),
                equivalenceKey: "AddCancellationTokenParameter"),
            diagnostic);
    }

    private static async Task<Document> AddCancellationTokenAsync(
        Document document,
        MethodDeclarationSyntax methodDeclaration,
        CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var generator = editor.Generator;

        var parameter = generator.ParameterDeclaration(
            name: "ct",
            type: SyntaxFactory.ParseTypeName("System.Threading.CancellationToken"),
            initializer: SyntaxFactory.ParseExpression("default"));

        editor.AddParameter(methodDeclaration, parameter);
        return editor.GetChangedDocument();
    }
}

/// <summary>
/// Quick fix for AD001 and AD007: adds a starter <c>HandleAsync</c> method to a <c>[Handler]</c>
/// or <c>[NotificationHandler]</c> class that currently has none, so the diagnostic can be
/// resolved without leaving the IDE.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddHandleAsyncStubCodeFixProvider))]
[Shared]
public sealed class AddHandleAsyncStubCodeFixProvider : CodeFixProvider
{
    private const string StubMethod = @"public System.Threading.Tasks.Task HandleAsync(/* TODO: replace object with your command/notification type */ object command, System.Threading.CancellationToken ct = default)
    {
        throw new System.NotImplementedException();
    }";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("AD001", "AD007");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan);
        var classDeclaration = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDeclaration is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Add HandleAsync stub method",
                createChangedDocument: ct => AddStubMethodAsync(context.Document, classDeclaration, ct),
                equivalenceKey: "AddHandleAsyncStub"),
            diagnostic);
    }

    private static async Task<Document> AddStubMethodAsync(
        Document document,
        ClassDeclarationSyntax classDeclaration,
        CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var stubMethod = SyntaxFactory.ParseMemberDeclaration(StubMethod)!;
        editor.AddMember(classDeclaration, stubMethod);
        return editor.GetChangedDocument();
    }
}

/// <summary>
/// Quick fix for AD009: adds a starter <c>HandleAsync</c> method to a <c>[StreamHandler]</c>
/// class that currently has none, returning <c>IAsyncEnumerable&lt;object&gt;</c> so the
/// diagnostic can be resolved without leaving the IDE.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddStreamHandleAsyncStubCodeFixProvider))]
[Shared]
public sealed class AddStreamHandleAsyncStubCodeFixProvider : CodeFixProvider
{
    private const string StubMethod = @"public System.Collections.Generic.IAsyncEnumerable<object> HandleAsync(/* TODO: replace object with your query type */ object query, System.Threading.CancellationToken ct = default)
    {
        throw new System.NotImplementedException();
    }";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("AD009");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan);
        var classDeclaration = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDeclaration is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Add streaming HandleAsync stub method",
                createChangedDocument: ct => AddStubMethodAsync(context.Document, classDeclaration, ct),
                equivalenceKey: "AddStreamHandleAsyncStub"),
            diagnostic);
    }

    private static async Task<Document> AddStubMethodAsync(
        Document document,
        ClassDeclarationSyntax classDeclaration,
        CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var stubMethod = SyntaxFactory.ParseMemberDeclaration(StubMethod)!;
        editor.AddMember(classDeclaration, stubMethod);
        return editor.GetChangedDocument();
    }
}

/// <summary>
/// Quick fix for AD100 and AD101 (see <see cref="MediatRMigrationAnalyzer"/>): converts a MediatR
/// <c>IRequestHandler&lt;,&gt;</c>/<c>IRequestHandler&lt;&gt;</c>/<c>INotificationHandler&lt;&gt;</c>
/// class into its AutoDispatch equivalent by adding <c>[Handler]</c>/<c>[NotificationHandler]</c>,
/// removing the MediatR interface from the base list, and renaming MediatR's <c>Handle</c> method
/// to AutoDispatch's <c>HandleAsync</c> convention (parameters — including the existing
/// <c>CancellationToken</c> — are left untouched, since both frameworks pass them the same way).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MediatRMigrationCodeFixProvider))]
[Shared]
public sealed class MediatRMigrationCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(
        MediatRMigrationAnalyzer.RequestHandlerDiagnosticId,
        MediatRMigrationAnalyzer.NotificationHandlerDiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan);
        var classDeclaration = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDeclaration is null)
        {
            return;
        }

        var isNotification = diagnostic.Id == MediatRMigrationAnalyzer.NotificationHandlerDiagnosticId;
        var title = isNotification ? "Convert to AutoDispatch [NotificationHandler]" : "Convert to AutoDispatch [Handler]";

        context.RegisterCodeFix(
            CodeAction.Create(
                title: title,
                createChangedDocument: ct => ConvertAsync(context.Document, classDeclaration, isNotification, ct),
                equivalenceKey: title),
            diagnostic);
    }

    private static async Task<Document> ConvertAsync(
        Document document,
        ClassDeclarationSyntax classDeclaration,
        bool isNotification,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return document;
        }

        var compilation = semanticModel.Compilation;
        var requestHandler2 = compilation.GetTypeByMetadataName("MediatR.IRequestHandler`2");
        var requestHandler1 = compilation.GetTypeByMetadataName("MediatR.IRequestHandler`1");
        var notificationHandler1 = compilation.GetTypeByMetadataName("MediatR.INotificationHandler`1");

        // Find the base-list entry whose resolved symbol matches one of the known MediatR
        // interfaces, so it can be removed regardless of how many other interfaces/base classes
        // the handler also declares.
        BaseTypeSyntax? mediatRBaseType = null;
        if (classDeclaration.BaseList is not null)
        {
            foreach (var baseType in classDeclaration.BaseList.Types)
            {
                var original = (semanticModel.GetTypeInfo(baseType.Type, cancellationToken).Type as INamedTypeSymbol)?.OriginalDefinition;
                if (original is null)
                {
                    continue;
                }

                if (SymbolEqualityComparer.Default.Equals(original, requestHandler2) ||
                    SymbolEqualityComparer.Default.Equals(original, requestHandler1) ||
                    SymbolEqualityComparer.Default.Equals(original, notificationHandler1))
                {
                    mediatRBaseType = baseType;
                    break;
                }
            }
        }

        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var generator = editor.Generator;

        var attributeTypeName = isNotification ? "global::AutoDispatch.NotificationHandlerAttribute" : "global::AutoDispatch.HandlerAttribute";
        editor.AddAttribute(classDeclaration, generator.Attribute(attributeTypeName));

        if (mediatRBaseType is not null)
        {
            editor.RemoveNode(mediatRBaseType);
        }

        foreach (var member in classDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            if (member.Identifier.Text == "Handle")
            {
                editor.ReplaceNode(member, member.WithIdentifier(SyntaxFactory.Identifier("HandleAsync")));
            }
        }

        return editor.GetChangedDocument();
    }
}