using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.AI.ToolProviders.Channel;

/// <summary>
/// Exposes the channel-neutral interactive reply tools to the agent tool discovery pipeline.
/// </summary>
public sealed class ChannelInteractiveReplyToolSource : IAgentToolSource
{
    private readonly IInteractiveReplyCollector _collector;

    public ChannelInteractiveReplyToolSource(IInteractiveReplyCollector collector)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<IAgentTool> tools = [new ReplyWithInteractionTool(_collector)];
        return Task.FromResult(tools);
    }
}
