using Aevatar.Foundation.Abstractions.Interactions;

namespace Aevatar.Workflow.Core.Primitives;

public sealed class StepPresentation
{
    public InteractionSpec? InteractionSpec { get; init; }

    public static bool HasInteractionSpec(InteractionSpec? spec) =>
        spec is not null &&
        (!string.IsNullOrWhiteSpace(spec.Title) ||
         !string.IsNullOrWhiteSpace(spec.Body) ||
         spec.Actions.Count > 0 ||
         spec.Fields.Count > 0 ||
         spec.Cards.Count > 0 ||
         spec.Disposition != InteractionDisposition.Unspecified);
}
