using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Core.Projection;

internal static class AuditTrailDocumentFactory
{
    public static Audit.AuditTrailDocument Create(
        Audit.AuditRecord record,
        string auditId,
        string contentHash,
        DateTimeOffset observedAt) =>
        new()
        {
            Id = auditId,
            AuditId = auditId,
            ContentHash = contentHash,
            Record = record.Clone(),
            OccurredAt = Timestamp.FromDateTimeOffset(observedAt),
            UpdatedAt = record.RecordedAt?.Clone() ?? Timestamp.FromDateTimeOffset(observedAt),
            AuditActorId = Text(record.AuditActorId),
            ScopeId = Text(record.ScopeId),
            OperationName = Text(record.OperationName),
            Outcome = record.Outcome,
            SensitivityLevel = record.SensitivityLevel,
            TargetKind = TargetKind(record),
            TargetId = TargetId(record),
            RequestId = RequestId(record),
            CommandId = CommandId(record),
            CorrelationId = CorrelationId(record),
            SessionId = SessionId(record),
            WorkflowRunId = WorkflowRunId(record),
            CommittedEventId = CommittedEventId(record),
            CommittedActorId = CommittedActorId(record),
            CommittedActorType = CommittedActorType(record),
            CommittedEventTypeUrl = CommittedEventTypeUrl(record),
            CommittedStateVersion = CommittedStateVersion(record),
            EventKind = Text(record.EventKind),
            SchemaVersion = Text(record.SchemaVersion),
            RecordedAt = record.RecordedAt?.Clone(),
            LifecyclePhase = record.LifecyclePhase,
            TerminalOutcome = record.TerminalOutcome,
            Subject = Text(record.Subject),
            Source = Text(record.Source),
            TraceId = Text(record.Correlation?.TraceId),
            ToolArgumentsSha256 = Text(record.ToolExecution?.ArgumentsSha256),
            ToolIsMutation = record.ToolExecution?.IsMutation ?? false,
            ToolExecutionPhase = record.ToolExecution?.ExecutionPhase ?? Audit.AuditToolExecutionPhase.Unspecified,
        };

    private static string Text(string? value) => value ?? string.Empty;

    private static string TargetKind(Audit.AuditRecord record) => Text(record.Target?.Kind);

    private static string TargetId(Audit.AuditRecord record) => Text(record.Target?.Id);

    private static string RequestId(Audit.AuditRecord record) => Text(record.Correlation?.RequestId);

    private static string CommandId(Audit.AuditRecord record) => Text(record.Correlation?.CommandId);

    private static string CorrelationId(Audit.AuditRecord record) => Text(record.Correlation?.CorrelationId);

    private static string SessionId(Audit.AuditRecord record) => Text(record.Correlation?.SessionId);

    private static string WorkflowRunId(Audit.AuditRecord record) => Text(record.Correlation?.WorkflowRunId);

    private static string CommittedEventId(Audit.AuditRecord record) => Text(record.CommittedFactRef?.CommittedEventId);

    private static string CommittedActorId(Audit.AuditRecord record) => Text(record.CommittedFactRef?.ActorId);

    private static string CommittedActorType(Audit.AuditRecord record) => Text(record.CommittedFactRef?.ActorType);

    private static string CommittedEventTypeUrl(Audit.AuditRecord record) => Text(record.CommittedFactRef?.EventTypeUrl);

    private static long CommittedStateVersion(Audit.AuditRecord record) => record.CommittedFactRef?.StateVersion ?? 0;
}
