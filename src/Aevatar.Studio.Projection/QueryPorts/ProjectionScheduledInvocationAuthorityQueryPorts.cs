using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
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
    public async Task<ScheduledInvocationOwnerLLMEvidence?> GetAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var document = await reader.GetAsync($"user-config-{scopeId.Trim()}", ct);
        if (document == null)
            return null;

        return document.LlmSelection == null
            ? Unspecified(document.StateVersion)
            : MapTypedEvidence(document.StateVersion, document.LlmSelection);
    }

    private static ScheduledInvocationOwnerLLMEvidence MapTypedEvidence(
        long stateVersion,
        LLMSelection selection)
    {
        try
        {
            LLMSelectionPolicy.ValidateSelection(selection);
        }
        catch (InvalidOperationException)
        {
            return Unspecified(stateVersion);
        }

        if (selection.ModelSelection.Kind != LLMModelSelectionKind.ExplicitModel)
            return Unspecified(stateVersion);

        var mapped = new ScheduledInvocationOwnerLLMSelection
        {
            RouteKind = selection.RouteKind switch
            {
                LLMRouteKind.Gateway => LLMRouteKind.Gateway,
                LLMRouteKind.NyxIdUserService => LLMRouteKind.NyxIdUserService,
                _ => LLMRouteKind.Unspecified,
            },
            RouteValue = selection.RouteValue,
            NyxIdUserServiceId = selection.NyxIdUserServiceId,
            ServiceSlugSnapshot = selection.ServiceSlugSnapshot,
            Model = selection.ModelSelection.ModelId,
        };

        return ScheduledInvocationOwnerLLMSelectionPolicy.IsDurableSelectionValid(mapped)
            ? new ScheduledInvocationOwnerLLMEvidence(stateVersion, mapped)
            : Unspecified(stateVersion);
    }

    private static ScheduledInvocationOwnerLLMEvidence Unspecified(long stateVersion) =>
        new(stateVersion, new ScheduledInvocationOwnerLLMSelection());
}
