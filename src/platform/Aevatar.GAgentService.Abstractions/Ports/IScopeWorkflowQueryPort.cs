namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeWorkflowQueryPort
{
    Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default);

    Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default);

    Task<ScopeWorkflowSummary?> GetByActorIdAsync(
        string scopeId,
        string actorId,
        CancellationToken ct = default);
}

public interface IScopeWorkflowCatalogueCommittedSourcePort
{
    Task<IReadOnlyList<ScopeWorkflowSummary>> ListCatalogueAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<ScopeWorkflowCatalogueLookupResult> LookupCatalogueByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default);
}
