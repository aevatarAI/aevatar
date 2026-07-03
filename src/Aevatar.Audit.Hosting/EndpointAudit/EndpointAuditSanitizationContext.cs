using Microsoft.AspNetCore.Http;

namespace Aevatar.Audit.Hosting.EndpointAudit;

public sealed record EndpointAuditSanitizationContext(
    HttpContext HttpContext,
    EndpointAuditMetadata Metadata,
    IReadOnlyList<object?> Arguments,
    object? Result);
