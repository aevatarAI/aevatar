namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeWorkflowSaveAndBindPort
{
    Task<ScopeWorkflowSaveAndBindResult> SaveAndBindAsync(
        ScopeWorkflowSaveAndBindRequest request,
        CancellationToken ct = default);
}
