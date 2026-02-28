using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptNetworkPolicy : IScriptNetworkPolicy
{
    public Task<NetworkAccessDecision> AuthorizeAsync(ScriptNetworkRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = request;
        return Task.FromResult(new NetworkAccessDecision(false, "NETWORK_ACCESS_DENIED", "network access is denied by default"));
    }
}
