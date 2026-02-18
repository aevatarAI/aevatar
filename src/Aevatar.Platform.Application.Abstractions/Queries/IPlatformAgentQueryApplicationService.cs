using Aevatar.Platform.Abstractions.Catalog;

namespace Aevatar.Platform.Application.Abstractions.Queries;

public interface IPlatformAgentQueryApplicationService
{
    IReadOnlyList<AgentCapability> ListAgents();

    Uri? ResolveCommandRoute(string subsystem, string command);

    Uri? ResolveQueryRoute(string subsystem, string query);
}
