; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.16.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
AD100 | AutoDispatch.Migration | Info | Detects classes implementing MediatR's IRequestHandler<,>/IRequestHandler<> that can be migrated to AutoDispatch's [Handler]
AD101 | AutoDispatch.Migration | Info | Detects classes implementing MediatR's INotificationHandler<> that can be migrated to AutoDispatch's [NotificationHandler]
