namespace Aevatar.Audit.Hosting;

public sealed record AuditTrailEndpointAuditMetadata(
    string Capability,
    string Operation,
    string AccessLevel);
