using Aevatar.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Audit.Hosting.EndpointAudit;

public static class EndpointAuditRouteBuilderExtensions
{
    public static RouteHandlerBuilder WithEndpointAudit(
        this RouteHandlerBuilder builder,
        EndpointAuditMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(metadata);

        return builder
            .WithMetadata(metadata)
            .AddEndpointFilter<EndpointAuditFilter>();
    }

    public static RouteHandlerBuilder WithEndpointAudit(
        this RouteHandlerBuilder builder,
        string operationName,
        AuditSensitivityLevel sensitivityLevel,
        string targetKind,
        EndpointAuditTargetResolver targetResolver,
        EndpointAuditSummarySanitizer? requestSanitizer = null,
        EndpointAuditSummarySanitizer? resultSanitizer = null,
        AuditOperationKind operationKind = AuditOperationKind.Api,
        bool captureUnauthenticated = false)
    {
        return builder.WithEndpointAudit(new EndpointAuditMetadata(
            operationName,
            sensitivityLevel,
            targetKind,
            targetResolver,
            requestSanitizer ?? EndpointAuditSanitizers.RouteOnlyRequest,
            resultSanitizer ?? EndpointAuditSanitizers.StatusOnlyResult,
            operationKind,
            captureUnauthenticated));
    }
}
