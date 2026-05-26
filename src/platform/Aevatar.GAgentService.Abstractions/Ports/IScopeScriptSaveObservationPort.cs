namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeScriptSaveObservationPort
{
    Task<ScopeScriptSaveObservationResult> ObserveAsync(
        string scopeId,
        string scriptId,
        ScopeScriptSaveObservationRequest request,
        CancellationToken ct = default);
}
