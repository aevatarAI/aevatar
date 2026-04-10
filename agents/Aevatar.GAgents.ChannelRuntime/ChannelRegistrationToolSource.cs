using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tool source that exposes channel_registrations tool to NyxIdChatGAgent.
/// Allows the agent to register, list, and delete channel bot registrations
/// so users don't need to call the REST API manually.
/// </summary>
public sealed class ChannelRegistrationToolSource : IAgentToolSource
{
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly IActorRuntime _actorRuntime;

    public ChannelRegistrationToolSource(
        IChannelBotRegistrationQueryPort queryPort,
        IActorRuntime actorRuntime)
    {
        _queryPort = queryPort;
        _actorRuntime = actorRuntime;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<IAgentTool> tools = [new ChannelRegistrationTool(_queryPort, _actorRuntime)];
        return Task.FromResult(tools);
    }
}
