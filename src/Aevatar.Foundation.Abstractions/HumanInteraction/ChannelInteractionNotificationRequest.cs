using Aevatar.Foundation.Abstractions.Interactions;

namespace Aevatar.Foundation.Abstractions.HumanInteraction;

public sealed record ChannelInteractionNotificationRequest
{
    public required string ActorId { get; init; }

    public required string RunId { get; init; }

    public required string StepId { get; init; }

    public required string DeliveryTargetId { get; init; }

    public InteractionSpec? InteractionSpec { get; init; }

    public InteractionTemplateSpec? InteractionTemplateSpec { get; init; }
}
