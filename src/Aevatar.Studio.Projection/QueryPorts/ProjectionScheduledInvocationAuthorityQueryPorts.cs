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
        if (document == null ||
            document.AuthorizationRevision < 0 ||
            string.IsNullOrWhiteSpace(document.ImplementationWorkflowId))
            return null;
        return new ScheduledInvocationMemberEvidence(
            ResolveAuthorizationRevision(document.AuthorizationRevision),
            document.ImplementationWorkflowId,
            document.LastBoundRevisionId,
            document.PublishedServiceId);
    }

    private static long ResolveAuthorizationRevision(long rawRevision) =>
        rawRevision == 0 ? 1 : rawRevision;
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
        var normalizedRevisionId = workflowRevisionId.Trim();
        if (catalog == null ||
            !catalog.TryGetPreparedArtifact(normalizedRevisionId, out var artifact) ||
            artifact.ImplementationKind != ServiceImplementationKind.Workflow ||
            !string.Equals(artifact.RevisionId, normalizedRevisionId, StringComparison.Ordinal) ||
            artifact.DeploymentPlan?.WorkflowPlan is not { } workflowPlan ||
            workflowPlan.AuthorizationEvidence == null ||
            workflowPlan.CapabilityAdmissionPlan == null ||
            !HasMatchingAdmissionEvidence(workflowPlan, normalizedRevisionId))
        {
            return null;
        }

        var evidence = workflowPlan.AuthorizationEvidence;
        return new ScheduledInvocationWorkflowEvidence(
            catalog.StateVersion,
            evidence.ExternalCapabilities.Select(static capability => capability.Clone()).ToArray(),
            evidence.OwnerLlmRouteRequired,
            evidence.ServiceGrantRequirement,
            workflowPlan.CapabilityAdmissionPlan.Clone());
    }

    private static bool HasMatchingAdmissionEvidence(
        WorkflowServiceDeploymentPlan workflowPlan,
        string revisionId)
    {
        var plan = workflowPlan.CapabilityAdmissionPlan;
        var evidence = workflowPlan.AuthorizationEvidence;
        try
        {
            if (!string.Equals(
                    plan.AdmissionDigest,
                    WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan),
                    StringComparison.Ordinal))
            {
                return false;
            }

            var admittedCapabilities = WorkflowCapabilityAdmissionPlanIntegrity
                .DistinctCapabilities(plan);
            if (!admittedCapabilities
                    .Select(WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey)
                    .SequenceEqual(
                        evidence.ExternalCapabilities
                            .Select(WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey)
                            .Order(StringComparer.Ordinal),
                        StringComparer.Ordinal) ||
                evidence.ServiceGrantRequirement !=
                WorkflowServiceGrantRequirementClassifier.Classify(admittedCapabilities))
            {
                return false;
            }

            if (!WorkflowCapabilityAdmissionPlanIntegrity
                    .RequiresExplicitRequestBindingIdentity(plan))
            {
                return true;
            }

            var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity
                .RequireExplicitBindingIdentity(workflowPlan.WorkflowId, workflowPlan.RevisionId);
            return string.Equals(bindingIdentity.RevisionId, revisionId, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
