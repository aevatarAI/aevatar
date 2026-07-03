namespace Aevatar.Audit.Abstractions.EndpointAudit;

public sealed record EndpointAuditTarget(
    string Kind,
    string Id,
    string DisplayName = "");
