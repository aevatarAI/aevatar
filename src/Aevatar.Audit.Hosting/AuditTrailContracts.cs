namespace Aevatar.Audit.Hosting;

public sealed record AuditTrailReadResponse(
    IReadOnlyList<AuditTrailRecordResponse> Records,
    DateTimeOffset ReadTimestampUtc,
    DateTimeOffset? QueryWatermark,
    string? NextCursor);

public sealed record AuditTrailRecordResponse(
    string Id,
    string ScopeId,
    string AuditActorId,
    string IdentityKeyId,
    string Action,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    string? ResourceType,
    string? ResourceId,
    string? CorrelationId);

public sealed record AuditActorResolutionRequest(
    string Provider,
    string Subject);

public sealed record AuditActorResolutionResponse(
    string AuditActorId,
    string IdentityKeyId,
    DateTimeOffset ReadTimestampUtc);
