namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeWorkflowTemplateEnsurePort
{
    Task<ScopeWorkflowTemplateEnsureResult> EnsureAsync(
        ScopeWorkflowTemplateEnsureRequest request,
        CancellationToken ct = default);
}
