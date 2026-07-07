using Microsoft.AspNetCore.Http;

namespace Aevatar.Audit.Hosting.EndpointAudit;

public delegate ValueTask<EndpointAuditTarget?> EndpointAuditTargetResolver(HttpContext httpContext);

public delegate ValueTask<string> EndpointAuditSummarySanitizer(EndpointAuditSanitizationContext context);
