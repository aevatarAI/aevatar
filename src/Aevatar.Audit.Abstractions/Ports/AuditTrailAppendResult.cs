namespace Aevatar.Audit.Abstractions.Ports;

public sealed record AuditTrailAppendResult(
    AuditTrailAppendStatus Status,
    string AuditId,
    string AuditActorId = "",
    DateTimeOffset? OccurredAt = null,
    string Message = "")
{
    public static AuditTrailAppendResult Appended(
        string auditId,
        string auditActorId = "",
        DateTimeOffset? occurredAt = null) =>
        new(AuditTrailAppendStatus.Appended, auditId, auditActorId, occurredAt);

    public static AuditTrailAppendResult Duplicate(string auditId) =>
        new(AuditTrailAppendStatus.Duplicate, auditId);

    public static AuditTrailAppendResult Conflict(string auditId, string message) =>
        new(AuditTrailAppendStatus.Conflict, auditId, Message: message);

    public static AuditTrailAppendResult StoreUnavailable(string auditId, string message) =>
        new(AuditTrailAppendStatus.StoreUnavailable, auditId, Message: message);
}

public enum AuditTrailAppendStatus
{
    Appended = 0,
    Duplicate = 1,
    Conflict = 2,
    StoreUnavailable = 3,
}
