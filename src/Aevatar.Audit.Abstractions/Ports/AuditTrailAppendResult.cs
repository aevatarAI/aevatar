namespace Aevatar.Audit.Abstractions.Ports;

public sealed record AuditTrailAppendResult(AuditTrailAppendStatus Status, string AuditId, string Message = "")
{
    public static AuditTrailAppendResult Appended(string auditId) =>
        new(AuditTrailAppendStatus.Appended, auditId);

    public static AuditTrailAppendResult Duplicate(string auditId) =>
        new(AuditTrailAppendStatus.Duplicate, auditId);

    public static AuditTrailAppendResult Conflict(string auditId, string message) =>
        new(AuditTrailAppendStatus.Conflict, auditId, message);

    public static AuditTrailAppendResult StoreUnavailable(string auditId, string message) =>
        new(AuditTrailAppendStatus.StoreUnavailable, auditId, message);
}

public enum AuditTrailAppendStatus
{
    Appended = 0,
    Duplicate = 1,
    Conflict = 2,
    StoreUnavailable = 3,
}
