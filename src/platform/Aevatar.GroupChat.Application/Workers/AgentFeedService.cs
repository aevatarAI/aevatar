using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Application.Workers;

public sealed class AgentFeedService
{
    private readonly IStreamProvider _streamProvider;
    private readonly IAgentFeedHintHandler _handler;

    public AgentFeedService(
        IStreamProvider streamProvider,
        IAgentFeedHintHandler handler)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var streamId = AgentFeedHintStreamIds.ForAgent(agentId);
        return _streamProvider.GetStream(streamId).SubscribeAsync<AgentFeedHint>(
            hint => _handler.HandleAsync(hint, ct),
            ct);
    }
}
