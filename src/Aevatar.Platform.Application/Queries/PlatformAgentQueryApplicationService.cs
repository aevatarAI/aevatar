using Aevatar.Platform.Abstractions.Catalog;
using Aevatar.Platform.Application.Abstractions.Queries;

namespace Aevatar.Platform.Application.Queries;

public sealed class PlatformAgentQueryApplicationService : IPlatformAgentQueryApplicationService
{
    private readonly IAgentCatalog _catalog;
    private readonly IAgentCommandRouter _commandRouter;
    private readonly IAgentQueryRouter _queryRouter;

    public PlatformAgentQueryApplicationService(
        IAgentCatalog catalog,
        IAgentCommandRouter commandRouter,
        IAgentQueryRouter queryRouter)
    {
        _catalog = catalog;
        _commandRouter = commandRouter;
        _queryRouter = queryRouter;
    }

    public IReadOnlyList<AgentCapability> ListAgents() => _catalog.List();

    public Uri? ResolveCommandRoute(string subsystem, string command) =>
        _commandRouter.Resolve(subsystem, command);

    public Uri? ResolveQueryRoute(string subsystem, string query) =>
        _queryRouter.Resolve(subsystem, query);
}
