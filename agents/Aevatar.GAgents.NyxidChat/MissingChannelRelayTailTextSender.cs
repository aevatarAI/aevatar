using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

internal sealed class MissingChannelRelayTailTextSender : IChannelRelayTailTextSender
{
    public Task<ChannelRelayTailTextSendResult> SendTailSegmentsAsync(
        ChannelRelayTailTextSendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ChannelRelayTailTextSendResult.Failed(
            "relay_tail_segment_sender_missing",
            "Relay tail text sender is not registered."));
    }
}
