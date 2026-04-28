using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class AgentBuilderToolSource : IAgentToolSource
{
    private readonly IServiceProvider _serviceProvider;

    public AgentBuilderToolSource(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IAgentTool> tools = [new AgentBuilderTool(_serviceProvider)];
        return Task.FromResult(tools);
    }
}
