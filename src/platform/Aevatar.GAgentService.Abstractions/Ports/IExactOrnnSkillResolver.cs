using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IExactOrnnSkillResolver
{
    Task<ExactOrnnSkillResolutionResult> ResolveAsync(
        string nyxIdAccessToken,
        ExactOrnnSkillReference reference,
        CancellationToken ct = default);
}
