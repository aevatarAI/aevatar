namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeScriptQueryPort
{
    Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<ScopeScriptSummary?> GetByScriptIdAsync(
        string scopeId,
        string scriptId,
        CancellationToken ct = default);
}
