using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace AutoDispatch.CodeFixes;

/// <summary>
/// Detects classes that still implement MediatR's <c>IRequestHandler&lt;,&gt;</c>,
/// <c>IRequestHandler&lt;&gt;</c>, or <c>INotificationHandler&lt;&gt;</c> interfaces and reports a
/// suggestion (with a one-click <see cref="MediatRMigrationCodeFixProvider"/> fix) to convert them
/// to the equivalent AutoDispatch <c>[Handler]</c>/<c>[NotificationHandler]</c> attribute. This
/// analyzer never requires a reference to AutoDispatch.Generator's own diagnostics — it only looks
/// for MediatR's well-known interface names, so it is a no-op (and costs nothing) in any
/// compilation that doesn't reference MediatR.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MediatRMigrationAnalyzer : DiagnosticAnalyzer
{
    public const string RequestHandlerDiagnosticId = "AD100";
    public const string NotificationHandlerDiagnosticId = "AD101";

    internal static readonly DiagnosticDescriptor RequestHandlerRule = new(
        id: RequestHandlerDiagnosticId,
        title: "MediatR request handler can be migrated to AutoDispatch",
        messageFormat: "'{0}' implements MediatR's IRequestHandler<{1}> and can be converted to an AutoDispatch [Handler] (zero-reflection, compile-time dispatch)",
        category: "AutoDispatch.Migration",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "AutoDispatch can generate a compile-time SendAsync dispatcher for this handler with no runtime reflection. Apply the code fix to add [Handler], remove the MediatR interface, and rename Handle to HandleAsync.");

    internal static readonly DiagnosticDescriptor NotificationHandlerRule = new(
        id: NotificationHandlerDiagnosticId,
        title: "MediatR notification handler can be migrated to AutoDispatch",
        messageFormat: "'{0}' implements MediatR's INotificationHandler<{1}> and can be converted to an AutoDispatch [NotificationHandler] (zero-reflection, compile-time dispatch)",
        category: "AutoDispatch.Migration",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "AutoDispatch can generate a compile-time PublishAsync dispatcher for this handler with no runtime reflection. Apply the code fix to add [NotificationHandler], remove the MediatR interface, and rename Handle to HandleAsync.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(RequestHandlerRule, NotificationHandlerRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var compilation = compilationContext.Compilation;
            var requestHandler2 = compilation.GetTypeByMetadataName("MediatR.IRequestHandler`2");
            var requestHandler1 = compilation.GetTypeByMetadataName("MediatR.IRequestHandler`1");
            var notificationHandler1 = compilation.GetTypeByMetadataName("MediatR.INotificationHandler`1");

            if (requestHandler2 is null && requestHandler1 is null && notificationHandler1 is null)
            {
                // MediatR isn't referenced in this compilation at all — nothing to migrate, and no
                // further per-symbol work is registered, so this analyzer costs nothing here.
                return;
            }

            compilationContext.RegisterSymbolAction(
                symbolContext => AnalyzeType(symbolContext, requestHandler2, requestHandler1, notificationHandler1),
                SymbolKind.NamedType);
        });
    }

    private static void AnalyzeType(
        SymbolAnalysisContext context,
        INamedTypeSymbol? requestHandler2,
        INamedTypeSymbol? requestHandler1,
        INamedTypeSymbol? notificationHandler1)
    {
        if (context.Symbol is not INamedTypeSymbol typeSymbol ||
            typeSymbol.TypeKind != TypeKind.Class ||
            typeSymbol.IsAbstract ||
            IsAlreadyMigrated(typeSymbol))
        {
            return;
        }

        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var original = iface.OriginalDefinition;
            var location = typeSymbol.Locations.FirstOrDefault() ?? Location.None;

            if (requestHandler2 is not null &&
                iface.TypeArguments.Length == 2 &&
                SymbolEqualityComparer.Default.Equals(original, requestHandler2))
            {
                var args = string.Join(", ", iface.TypeArguments.Select(static t => t.Name));
                context.ReportDiagnostic(Diagnostic.Create(RequestHandlerRule, location, typeSymbol.Name, args));
                return;
            }

            if (requestHandler1 is not null &&
                iface.TypeArguments.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(original, requestHandler1))
            {
                context.ReportDiagnostic(Diagnostic.Create(RequestHandlerRule, location, typeSymbol.Name, iface.TypeArguments[0].Name));
                return;
            }

            if (notificationHandler1 is not null &&
                iface.TypeArguments.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(original, notificationHandler1))
            {
                context.ReportDiagnostic(Diagnostic.Create(NotificationHandlerRule, location, typeSymbol.Name, iface.TypeArguments[0].Name));
                return;
            }
        }
    }

    private static bool IsAlreadyMigrated(INamedTypeSymbol typeSymbol)
    {
        foreach (var attribute in typeSymbol.GetAttributes())
        {
            switch (attribute.AttributeClass?.Name)
            {
                case "HandlerAttribute":
                case "CommandHandlerAttribute":
                case "QueryHandlerAttribute":
                case "NotificationHandlerAttribute":
                    return true;
            }
        }

        return false;
    }
}
