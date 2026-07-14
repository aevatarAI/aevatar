using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionScheduledInvocationMemberQueryPort(
    IProjectionDocumentReader<StudioMemberCurrentStateDocument, string> reader)
    : IScheduledInvocationMemberQueryPort
{
    public async Task<ScheduledInvocationMemberFact?> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var document = await reader.GetAsync($"studio-member:{scopeId.Trim()}:{memberId.Trim()}", ct);
        if (document == null || string.IsNullOrWhiteSpace(document.ImplementationWorkflowId))
            return null;
        return new ScheduledInvocationMemberFact(
            document.StateVersion,
            document.ImplementationWorkflowId,
            document.LastBoundRevisionId,
            document.PublishedServiceId);
    }
}

public sealed class ProjectionScheduledInvocationWorkflowQueryPort(
    IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string> reader)
    : IScheduledInvocationWorkflowQueryPort
{
    public async Task<ScheduledInvocationWorkflowFact?> GetAsync(string workflowId, CancellationToken ct = default)
    {
        var document = await reader.GetAsync(workflowId.Trim(), ct);
        return document?.AuthorizationDependencies == null
            ? null
            : new ScheduledInvocationWorkflowFact(document.StateVersion, document.AuthorizationDependencies.Clone());
    }
}

public sealed class ProjectionScheduledInvocationConnectorQueryPort(
    IProjectionDocumentReader<ConnectorCatalogCurrentStateDocument, string> reader)
    : IScheduledInvocationConnectorQueryPort
{
    public async Task<ScheduledInvocationVersionFact?> GetAsync(string scopeId, CancellationToken ct = default)
    {
        var document = await reader.GetAsync($"connector-catalog-{scopeId.Trim()}", ct);
        return document == null ? null : new ScheduledInvocationVersionFact(document.StateVersion);
    }
}

public sealed class ProjectionScheduledInvocationOwnerLLMQueryPort(
    IProjectionDocumentReader<UserConfigCurrentStateDocument, string> reader)
    : IScheduledInvocationOwnerLLMQueryPort
{
    public async Task<ScheduledInvocationVersionFact?> GetAsync(string scopeId, CancellationToken ct = default)
    {
        var document = await reader.GetAsync($"user-config-{scopeId.Trim()}", ct);
        return document == null ? null : new ScheduledInvocationVersionFact(document.StateVersion);
    }
}
