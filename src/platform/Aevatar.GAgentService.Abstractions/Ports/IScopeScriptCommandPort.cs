namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeScriptCommandPort
{
    Task<ScopeScriptUpsertResult> UpsertAsync(
        ScopeScriptUpsertRequest request,
        CancellationToken ct = default);
}
