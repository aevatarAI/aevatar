namespace Aevatar.Foundation.Abstractions.HumanInteraction;

public interface IHumanInteractionPort
{
    Task DeliverSuspensionAsync(
        HumanInteractionRequest request,
        string deliveryTargetId,
        CancellationToken cancellationToken = default);
}
