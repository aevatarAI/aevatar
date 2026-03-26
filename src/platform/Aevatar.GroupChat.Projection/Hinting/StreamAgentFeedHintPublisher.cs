using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;

namespace Aevatar.GroupChat.Projection.Hinting;

public sealed class StreamAgentFeedHintPublisher : IAgentFeedHintPublisher
{
    private readonly IStreamProvider _streamProvider;

    public StreamAgentFeedHintPublisher(IStreamProvider streamProvider)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    public Task PublishAsync(AgentFeedHint hint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return _streamProvider
            .GetStream(AgentFeedHintStreamIds.ForAgent(hint.AgentId))
            .ProduceAsync(hint, ct);
    }
}
