namespace Aevatar.Audit.Abstractions.Models;

public sealed record AuditTrailAppendReceipt(
    string AuditId,
    string AuditActorId,
    DateTimeOffset OccurredAt);
