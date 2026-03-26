namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeWorkflowQueryPort
{
    Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
        string scopeId,
        string appId,
        CancellationToken ct = default);

    Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default);

    Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
        string scopeId,
        string appId,
        string workflowId,
        CancellationToken ct = default);

    Task<ScopeWorkflowSummary?> GetByActorIdAsync(
        string scopeId,
        string actorId,
        CancellationToken ct = default);

    Task<ScopeWorkflowSummary?> GetByActorIdAsync(
        string scopeId,
        string appId,
        string actorId,
        CancellationToken ct = default);
}
