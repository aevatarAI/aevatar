namespace Aevatar.Audit.Hosting;

public interface IAuditTrailQueryPort
{
    Task<AuditTrailQueryResult> QueryAsync(AuditTrailQuery query, CancellationToken cancellationToken = default);
}

public interface IAuditActorIdentityHasher
{
    string ComputeAuditActorId(AuditExternalActorIdentity identity);
}

public sealed record AuditTrailQuery(
    string ScopeId,
    string? AuditActorId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Take,
    bool IsAdminRead);

public sealed record AuditTrailQueryResult(
    IReadOnlyList<AuditTrailRecord> Records,
    DateTimeOffset ReadTimestampUtc,
    string? QueryWatermark);

public sealed record AuditTrailRecord(
    string Id,
    string ScopeId,
    string AuditActorId,
    string Action,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string? ResourceType = null,
    string? ResourceId = null,
    string? CorrelationId = null);

public sealed record AuditExternalActorIdentity(
    string Provider,
    string Subject);

public sealed record AuditTrailReadResponse(
    IReadOnlyList<AuditTrailRecordResponse> Records,
    DateTimeOffset ReadTimestampUtc,
    string? QueryWatermark);

public sealed record AuditTrailRecordResponse(
    string Id,
    string ScopeId,
    string AuditActorId,
    string Action,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string? ResourceType,
    string? ResourceId,
    string? CorrelationId);

public sealed record AuditActorResolutionRequest(
    string Provider,
    string Subject);

public sealed record AuditActorResolutionResponse(
    string AuditActorId,
    DateTimeOffset ReadTimestampUtc);
