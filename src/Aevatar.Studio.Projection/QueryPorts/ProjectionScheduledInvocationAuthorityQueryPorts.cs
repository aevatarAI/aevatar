using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;

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
            evidence.ExternalCapabilities.Select(static capability => capability.Clone()).ToArray(),
            evidence.OwnerLlmRouteRequired,
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

public sealed class ProjectionScheduledInvocationOwnerLLMQueryPort(
    IProjectionDocumentReader<UserConfigCurrentStateDocument, string> reader)
    : IScheduledInvocationOwnerLLMEvidenceQueryPort
{
    private const string NyxIdProxyRoutePrefix = "/api/v1/proxy/s/";
    private const string NyxIdGatewayRoute = "/api/v1/llm/gateway/v1";

    public async Task<ScheduledInvocationOwnerLLMEvidence?> GetAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var document = await reader.GetAsync($"user-config-{scopeId.Trim()}", ct);
        if (document == null)
            return null;

        return document.LlmSelection == null
            ? MapLegacyEvidence(document)
            : MapTypedEvidence(document.StateVersion, document.LlmSelection);
    }

    private static ScheduledInvocationOwnerLLMEvidence MapTypedEvidence(
        long stateVersion,
        UserLlmSelection selection)
    {
        var route = selection.RouteValue ?? string.Empty;
        var serviceId = selection.NyxIdUserServiceId ?? string.Empty;
        var serviceSlug = selection.ServiceSlugSnapshot ?? string.Empty;

        if (selection.RouteKind == UserLlmRouteKind.Gateway &&
            string.Equals(route, NyxIdGatewayRoute, StringComparison.Ordinal) &&
            serviceId.Length == 0 &&
            serviceSlug.Length == 0)
        {
            return Evidence(stateVersion, AuthorizationGrantRequirement.NotRequired);
        }

        if (selection.RouteKind == UserLlmRouteKind.NyxIdUserService &&
            IsCanonicalNonEmpty(route) &&
            IsCanonicalNonEmpty(serviceId) &&
            IsCanonicalNonEmpty(serviceSlug) &&
            IsExactProxyRoute(route, serviceSlug))
        {
            return new ScheduledInvocationOwnerLLMEvidence(
                stateVersion,
                serviceId,
                serviceSlug,
                AuthorizationGrantRequirement.Required);
        }

        return Evidence(stateVersion, AuthorizationGrantRequirement.Unspecified);
    }

    private static ScheduledInvocationOwnerLLMEvidence MapLegacyEvidence(
        UserConfigCurrentStateDocument document)
    {
        var route = document.PreferredLlmRoute?.Trim() ?? string.Empty;
        var serviceSlug = ResolveProxyServiceSlug(route);
        return serviceSlug.Length == 0
            ? Evidence(document.StateVersion, AuthorizationGrantRequirement.Unspecified)
            : new ScheduledInvocationOwnerLLMEvidence(
                document.StateVersion,
                string.Empty,
                serviceSlug,
                AuthorizationGrantRequirement.Required);
    }

    private static ScheduledInvocationOwnerLLMEvidence Evidence(
        long stateVersion,
        AuthorizationGrantRequirement requirement) =>
        new(stateVersion, string.Empty, string.Empty, requirement);

    private static bool IsCanonicalNonEmpty(string value) =>
        value.Length > 0 && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsExactProxyRoute(string route, string serviceSlug) =>
        serviceSlug.Length > 0 &&
        !serviceSlug.Contains('/') &&
        string.Equals(route, $"{NyxIdProxyRoutePrefix}{serviceSlug}", StringComparison.Ordinal);

    private static string ResolveProxyServiceSlug(string route)
    {
        if (!route.StartsWith(NyxIdProxyRoutePrefix, StringComparison.Ordinal))
            return string.Empty;

        var serviceSlug = route[NyxIdProxyRoutePrefix.Length..].Trim();
        return serviceSlug.Length == 0 || serviceSlug.Contains('/') ? string.Empty : serviceSlug;
    }
}
