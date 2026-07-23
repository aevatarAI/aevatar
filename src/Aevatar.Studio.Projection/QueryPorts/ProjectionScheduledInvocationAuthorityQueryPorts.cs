using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Projection.ReadModels;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionScheduledInvocationMemberQueryPort(
    IProjectionDocumentReader<StudioMemberCurrentStateDocument, string> reader)
    : IScheduledInvocationMemberEvidenceQueryPort
{
    public async Task<ScheduledInvocationMemberEvidence?> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var document = await reader.GetAsync($"studio-member:{scopeId.Trim()}:{memberId.Trim()}", ct);
        if (document == null || string.IsNullOrWhiteSpace(document.ImplementationWorkflowId))
            return null;
        return new ScheduledInvocationMemberEvidence(
            document.StateVersion,
            document.ImplementationWorkflowId,
            document.LastBoundRevisionId,
            document.PublishedServiceId);
    }
}

public sealed class ProjectionScheduledInvocationWorkflowQueryPort(
    IServiceRevisionCatalogQueryReader revisionCatalogReader)
    : IScheduledInvocationWorkflowEvidenceQueryPort
{
    public async Task<ScheduledInvocationWorkflowEvidence?> GetAsync(
        string scopeId,
        string publishedServiceId,
        string workflowRevisionId,
        CancellationToken ct = default)
    {
        var identity = new ServiceIdentity
        {
            TenantId = scopeId.Trim(),
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = publishedServiceId.Trim(),
        };
        var catalog = await revisionCatalogReader.GetAsync(identity, ct);
        if (catalog == null ||
            !catalog.TryGetPreparedArtifact(workflowRevisionId.Trim(), out var artifact) ||
            artifact.ImplementationKind != ServiceImplementationKind.Workflow ||
            artifact.DeploymentPlan?.WorkflowPlan?.AuthorizationEvidence == null)
        {
            return null;
        }

        var evidence = artifact.DeploymentPlan.WorkflowPlan.AuthorizationEvidence;
        return new ScheduledInvocationWorkflowEvidence(
            catalog.StateVersion,
            evidence.ConnectorCapabilityRefs.ToArray(),
            evidence.OwnerLlmRouteRequired,
            evidence.NyxIdServiceIds.ToArray(),
            evidence.NyxIdServiceSlugs.ToArray(),
            evidence.ServiceGrantRequirement);
    }
}

public sealed class ProjectionScheduledInvocationConnectorQueryPort(
    IProjectionDocumentReader<ConnectorCatalogCurrentStateDocument, string> reader)
    : IScheduledInvocationConnectorEvidenceQueryPort
{
    public async Task<ScheduledInvocationConnectorEvidence?> GetAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var document = await reader.GetAsync($"connector-catalog-{scopeId.Trim()}", ct);
        if (document?.StateRoot?.Is(ConnectorCatalogState.Descriptor) != true)
            return null;

        var state = document.StateRoot.Unpack<ConnectorCatalogState>();
        return new ScheduledInvocationConnectorEvidence(
            document.StateVersion,
            state.Connectors
                .Where(static connector => connector.Enabled && !string.IsNullOrWhiteSpace(connector.Name))
                .Select(static connector => connector.Name.Trim())
                .ToArray());
    }
}

public sealed class ProjectionScheduledInvocationOwnerLLMQueryPort
    : IScheduledInvocationOwnerLLMEvidenceQueryPort
{
    private const string NyxIdProxyRoutePrefix = "/api/v1/proxy/s/";
    private const string NyxIdGatewayRoute = "/api/v1/llm/gateway/v1";
    private readonly IProjectionDocumentReader<UserConfigCurrentStateDocument, string> _reader;
    private readonly string _defaultRoutePreference;

    public ProjectionScheduledInvocationOwnerLLMQueryPort(
        IProjectionDocumentReader<UserConfigCurrentStateDocument, string> reader,
        IOptions<ScheduledInvocationOwnerLLMRouteOptions>? options = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _defaultRoutePreference = options?.Value.DefaultRoutePreference?.Trim() ?? string.Empty;
    }

    public async Task<ScheduledInvocationOwnerLLMEvidence?> GetAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var document = await _reader.GetAsync($"user-config-{scopeId.Trim()}", ct);
        var stateVersion = document?.StateVersion ?? 0;
        var route = NormalizeRoutePreference(document?.PreferredLlmRoute);
        if (route.Length == 0)
            route = NormalizeRoutePreference(_defaultRoutePreference);

        if (route.Length == 0 ||
            string.Equals(route.TrimEnd('/'), NyxIdGatewayRoute, StringComparison.OrdinalIgnoreCase))
        {
            return new ScheduledInvocationOwnerLLMEvidence(
                stateVersion,
                string.Empty,
                string.Empty,
                AuthorizationGrantRequirement.NotRequired);
        }

        var serviceSlug = route.StartsWith(NyxIdProxyRoutePrefix, StringComparison.Ordinal)
            ? route[NyxIdProxyRoutePrefix.Length..].Trim('/')
            : route.Trim('/');
        if (serviceSlug.Length == 0 || serviceSlug.Contains('/'))
        {
            return new ScheduledInvocationOwnerLLMEvidence(
                stateVersion,
                string.Empty,
                string.Empty,
                AuthorizationGrantRequirement.Unspecified);
        }

        return new ScheduledInvocationOwnerLLMEvidence(
            stateVersion,
            string.Empty,
            serviceSlug,
            AuthorizationGrantRequirement.Required);
    }

    private static string NormalizeRoutePreference(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 ||
            string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "gateway", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.Contains("://", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized
            : $"{NyxIdProxyRoutePrefix}{normalized.Trim('/')}";
    }
}
