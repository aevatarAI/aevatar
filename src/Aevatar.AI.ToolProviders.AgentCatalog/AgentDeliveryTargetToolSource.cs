using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.AgentCatalog;

public sealed class AgentDeliveryTargetToolSource : IAgentToolSource
{
    private readonly IServiceProvider _serviceProvider;

    public AgentDeliveryTargetToolSource(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<IAgentTool> tools = [new AgentDeliveryTargetTool(_serviceProvider)];
        return Task.FromResult(tools);
    }
}
