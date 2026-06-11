namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeBindingReadinessQueryPort
{
    Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
        ScopeBindingReadinessRequest request,
        CancellationToken ct = default);
}
