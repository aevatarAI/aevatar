using Aevatar.Foundation.Abstractions.HumanInteraction;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class NullHumanInteractionPort : IHumanInteractionPort
{
    public Task DeliverSuspensionAsync(
        HumanInteractionRequest request,
        string deliveryTargetId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeliverApprovalResolutionAsync(
        HumanApprovalResolution resolution,
        string deliveryTargetId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
