namespace Aevatar.Foundation.Abstractions.HumanInteraction;

public interface IHumanInteractionPort
{
    Task DeliverSuspensionAsync(
        HumanInteractionRequest request,
        string deliveryTargetId,
        CancellationToken cancellationToken = default);

    Task DeliverApprovalResolutionAsync(
        HumanApprovalResolution resolution,
        string deliveryTargetId,
        CancellationToken cancellationToken = default);
}
