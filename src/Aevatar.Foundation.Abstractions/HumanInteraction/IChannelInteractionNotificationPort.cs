namespace Aevatar.Foundation.Abstractions.HumanInteraction;

public interface IChannelInteractionNotificationPort
{
    Task DeliverAsync(
        ChannelInteractionNotificationRequest request,
        CancellationToken cancellationToken = default);
}
