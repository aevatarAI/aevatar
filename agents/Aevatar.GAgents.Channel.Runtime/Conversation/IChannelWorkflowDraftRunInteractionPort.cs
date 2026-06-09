namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelWorkflowDraftRunInteractionPort
{
    Task DispatchAsync(NeedsWorkflowDraftRunEvent request, CancellationToken ct);
}
