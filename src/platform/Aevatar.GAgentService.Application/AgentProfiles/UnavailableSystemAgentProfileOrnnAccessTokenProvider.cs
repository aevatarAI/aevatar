using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class UnavailableSystemAgentProfileOrnnAccessTokenProvider
    : ISystemAgentProfileOrnnAccessTokenProvider
{
    public Task<string?> GetAccessTokenAsync(
        string definitionKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
