using Aevatar.Foundation.Abstractions;

namespace Aevatar.AI.Abstractions.Agents;

public interface IRoleAgent : IAgent
{
    void SetRoleName(string name);
    Task ConfigureAsync(RoleAgentConfig config, CancellationToken ct = default);
}
