namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeBindingCommandPort
{
    Task<ScopeBindingUpsertResult> UpsertAsync(
        ScopeBindingUpsertRequest request,
        CancellationToken ct = default);
}
