namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelLlmReplyInbox
{
    Task EnqueueAsync(NeedsLlmReplyEvent request, CancellationToken ct);
}
