using Aevatar.Audit;

namespace Aevatar.Audit.Abstractions.Models;

public sealed record AuditTrailQuery
{
    public DateTimeOffset? OccurredFrom { get; init; }

    public DateTimeOffset? OccurredTo { get; init; }

    public string? ScopeId { get; init; }

    public string? AuditActorId { get; init; }

    public IReadOnlyList<string>? AuditActorIds { get; init; }

    public AuditActorKind? ActorKind { get; init; }

    public string? IdentityKeyId { get; init; }

    public string? OperationName { get; init; }

    public AuditOperationKind? OperationKind { get; init; }

    public AuditOutcome? Outcome { get; init; }

    public AuditLifecyclePhase? LifecyclePhase { get; init; }

    public AuditTerminalOutcome? TerminalOutcome { get; init; }

    public bool RequireChatProvenance { get; init; }

    public AuditChatSurface? ChatSurface { get; init; }

    public string? ChatConversationId { get; init; }

    public AuditSensitivityLevel? SensitivityLevel { get; init; }

    public AuditCapturePlane? CapturePlane { get; init; }

    public string? TargetKind { get; init; }

    public string? TargetId { get; init; }

    public string? TraceId { get; init; }

    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public string? RequestId { get; init; }

    public string? CommandId { get; init; }

    public string? CallId { get; init; }

    public string? SessionId { get; init; }

    public string? WorkflowRunId { get; init; }

    public string? ApprovalId { get; init; }

    public string? CommittedEventId { get; init; }

    public string? CommittedActorId { get; init; }

    public string? CommittedActorType { get; init; }

    public string? CommittedEventTypeUrl { get; init; }

    public long? CommittedStateVersion { get; init; }

    public string? Cursor { get; init; }

    public int Take { get; init; } = 100;
}
