namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelWorkflowDraftRunInteractionPort
{
    Task DispatchAsync(NeedsWorkflowDraftRunEvent request, CancellationToken ct);

    Task StartWorkflowInteractionAsync(string runActorId, NeedsWorkflowDraftRunEvent request, CancellationToken ct);
}
