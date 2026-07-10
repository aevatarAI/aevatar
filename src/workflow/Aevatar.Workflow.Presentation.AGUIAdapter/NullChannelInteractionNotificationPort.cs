using Aevatar.Foundation.Abstractions.HumanInteraction;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class NullChannelInteractionNotificationPort : IChannelInteractionNotificationPort
{
    public Task DeliverAsync(
        ChannelInteractionNotificationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
