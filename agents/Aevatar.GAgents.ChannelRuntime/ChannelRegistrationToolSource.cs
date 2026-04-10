using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tool source that exposes channel_registrations tool to NyxIdChatGAgent.
/// Uses IServiceProvider for lazy resolution to avoid DI failures during
/// Orleans grain activation (IActorRuntime may not be available at singleton
/// construction time).
/// </summary>
public sealed class ChannelRegistrationToolSource : IAgentToolSource
{
    private readonly IServiceProvider _serviceProvider;

    public ChannelRegistrationToolSource(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var queryPort = _serviceProvider.GetService<IChannelBotRegistrationQueryPort>();
        var actorRuntime = _serviceProvider.GetService<IActorRuntime>();

        if (queryPort is null || actorRuntime is null)
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        IReadOnlyList<IAgentTool> tools = [new ChannelRegistrationTool(queryPort, actorRuntime)];
        return Task.FromResult(tools);
    }
}
