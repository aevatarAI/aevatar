namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeWorkflowCommandPort
{
    Task<ScopeWorkflowUpsertResult> UpsertAsync(
        ScopeWorkflowUpsertRequest request,
        CancellationToken ct = default);
}
