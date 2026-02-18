using Aevatar.Platform.Abstractions.Catalog;
using Microsoft.Extensions.Options;

namespace Aevatar.Platform.Infrastructure.Catalog;

public sealed class BuiltInAgentCatalog : IAgentCatalog, IAgentCommandRouter, IAgentQueryRouter
{
    private readonly SubsystemEndpointOptions _options;

    public BuiltInAgentCatalog(IOptions<SubsystemEndpointOptions> options)
    {
        _options = options.Value;
    }

    public IReadOnlyList<AgentCapability> List() =>
    [
        new AgentCapability(
            AgentType: "WorkflowGAgent",
            Subsystem: "workflow",
            CommandEndpoint: $"{_options.WorkflowBaseUrl.TrimEnd('/')}/api/commands",
            QueryEndpoint: $"{_options.WorkflowBaseUrl.TrimEnd('/')}/api/actors/{{actorId}}",
            StreamEndpoint: $"{_options.WorkflowBaseUrl.TrimEnd('/')}/api/ws/chat"),
        new AgentCapability(
            AgentType: "MakerWorkflowGAgent",
            Subsystem: "maker",
            CommandEndpoint: $"{_options.MakerBaseUrl.TrimEnd('/')}/api/maker/runs",
            QueryEndpoint: $"{_options.MakerBaseUrl.TrimEnd('/')}/api/maker/runs/{{actorId}}",
            StreamEndpoint: string.Empty),
    ];

    public Uri? Resolve(string subsystem, string command)
    {
        if (string.Equals(subsystem, "workflow", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{_options.WorkflowBaseUrl.TrimEnd('/')}/api/{command.TrimStart('/')}");

        if (string.Equals(subsystem, "maker", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{_options.MakerBaseUrl.TrimEnd('/')}/api/{command.TrimStart('/')}");

        return null;
    }

    Uri? IAgentQueryRouter.Resolve(string subsystem, string query)
    {
        if (string.Equals(subsystem, "workflow", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{_options.WorkflowBaseUrl.TrimEnd('/')}/api/{query.TrimStart('/')}");

        if (string.Equals(subsystem, "maker", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{_options.MakerBaseUrl.TrimEnd('/')}/api/{query.TrimStart('/')}");

        return null;
    }
}
