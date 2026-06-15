using Aevatar.Foundation.Abstractions.Interactions;

namespace Aevatar.Workflow.Core.Primitives;

public sealed class StepPresentation
{
    public InteractionSpec? InteractionSpec { get; init; }

    public InteractionTemplateSpec? InteractionTemplateSpec { get; init; }

    public string? DeliveryTargetId { get; init; }

    public static bool HasInteractionSpec(InteractionSpec? spec) =>
        spec is not null &&
        (!string.IsNullOrWhiteSpace(spec.Title) ||
         !string.IsNullOrWhiteSpace(spec.Body) ||
         spec.Actions.Count > 0 ||
         spec.Fields.Count > 0 ||
         spec.Cards.Count > 0 ||
         spec.Disposition != InteractionDisposition.Unspecified);

    public static bool HasInteractionTemplateSpec(InteractionTemplateSpec? spec) =>
        spec is not null &&
        (!string.IsNullOrWhiteSpace(spec.TemplateId) ||
         spec.TemplateVariable.Count > 0);

    public static bool HasPresentation(StepPresentation? presentation) =>
        presentation is not null &&
        (HasInteractionSpec(presentation.InteractionSpec) ||
         HasInteractionTemplateSpec(presentation.InteractionTemplateSpec) ||
         !string.IsNullOrWhiteSpace(presentation.DeliveryTargetId));
}
