using Microsoft.AspNetCore.Http;

namespace Aevatar.Audit.Abstractions.EndpointAudit;

public sealed record EndpointAuditSanitizationContext(
    HttpContext HttpContext,
    EndpointAuditMetadata Metadata,
    IReadOnlyList<object?> Arguments,
    object? Result);
