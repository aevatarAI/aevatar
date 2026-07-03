namespace Aevatar.Audit.Hosting.EndpointAudit;

public sealed record EndpointAuditTarget(
    string Kind,
    string Id,
    string DisplayName = "");
