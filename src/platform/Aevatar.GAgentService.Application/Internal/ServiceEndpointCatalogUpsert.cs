using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Internal;

internal static class ServiceEndpointCatalogUpsert
{
    public static async Task EnsureAsync(
        ServiceDefinitionSpec serviceDefinition,
        IServiceGovernanceCommandPort governanceCommandPort,
        IServiceGovernanceQueryPort governanceQueryPort,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serviceDefinition);
        ArgumentNullException.ThrowIfNull(governanceCommandPort);
        ArgumentNullException.ThrowIfNull(governanceQueryPort);

        var identity = serviceDefinition.Identity
            ?? throw new InvalidOperationException("service identity is required.");
        var existingCatalog = await governanceQueryPort.GetEndpointCatalogAsync(identity, ct);
        var upsertSpec = BuildSpec(serviceDefinition, existingCatalog);
        if (existingCatalog == null)
        {
            await governanceCommandPort.CreateEndpointCatalogAsync(new CreateServiceEndpointCatalogCommand
            {
                Spec = upsertSpec,
            }, ct);
            return;
        }

        await governanceCommandPort.UpdateEndpointCatalogAsync(new UpdateServiceEndpointCatalogCommand
        {
            Spec = upsertSpec,
        }, ct);
    }

    private static ServiceEndpointCatalogSpec BuildSpec(
        ServiceDefinitionSpec serviceDefinition,
        ServiceEndpointCatalogSnapshot? existingCatalog)
    {
        var spec = new ServiceEndpointCatalogSpec
        {
            Identity = serviceDefinition.Identity?.Clone()
                ?? throw new InvalidOperationException("service identity is required."),
        };
        var existingEndpointsById = existingCatalog?.Endpoints.ToDictionary(x => x.EndpointId, StringComparer.Ordinal)
            ?? new Dictionary<string, ServiceEndpointExposureSnapshot>(StringComparer.Ordinal);
        foreach (var endpoint in serviceDefinition.Endpoints)
        {
            existingEndpointsById.TryGetValue(endpoint.EndpointId, out var existingEndpoint);
            var exposure = new ServiceEndpointExposureSpec
            {
                EndpointId = endpoint.EndpointId,
                DisplayName = endpoint.DisplayName,
                Kind = endpoint.Kind,
                RequestTypeUrl = endpoint.RequestTypeUrl,
                ResponseTypeUrl = endpoint.ResponseTypeUrl,
                Description = endpoint.Description,
                ExposureKind = existingEndpoint?.ExposureKind ?? ServiceEndpointExposureKind.Internal,
            };
            if (existingEndpoint != null)
                exposure.PolicyIds.Add(existingEndpoint.PolicyIds);

            spec.Endpoints.Add(exposure);
        }

        return spec;
    }
}
