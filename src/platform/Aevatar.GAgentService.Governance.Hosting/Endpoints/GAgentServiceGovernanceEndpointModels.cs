using Aevatar.GAgentService.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.GAgentService.Governance.Hosting.Endpoints;

internal static class GAgentServiceGovernanceEndpointModels
{
    internal static ServiceIdentity ToIdentity(string? tenantId, string? appId, string? @namespace, string serviceId)
    {
        return new ServiceIdentity
        {
            TenantId = tenantId?.Trim() ?? string.Empty,
            AppId = appId?.Trim() ?? string.Empty,
            Namespace = @namespace?.Trim() ?? string.Empty,
            ServiceId = serviceId?.Trim() ?? string.Empty,
        };
    }

    internal sealed record ServiceIdentityHttpRequest(
        string TenantId,
        string AppId,
        string Namespace);

    internal sealed record ServiceIdentityQuery(
        [FromQuery(Name = "tenantId")] string? TenantId,
        [FromQuery(Name = "appId")] string? AppId,
        [FromQuery(Name = "namespace")] string? Namespace);
}
