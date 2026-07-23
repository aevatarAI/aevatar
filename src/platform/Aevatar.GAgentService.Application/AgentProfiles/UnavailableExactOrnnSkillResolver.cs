using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class UnavailableExactOrnnSkillResolver : IExactOrnnSkillResolver
{
    public Task<ExactOrnnSkillResolutionResult> ResolveAsync(
        string nyxIdAccessToken,
        ExactOrnnSkillReference reference,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ExactOrnnSkillResolutionResult.Failed(
            "ORNN_DEPENDENCY_UNAVAILABLE",
            "The exact Ornn skill dependency is unavailable."));
    }
}
