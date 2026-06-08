using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class WorkflowHumanInteractionProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext>
{
    private readonly IHumanInteractionPort _humanInteractionPort;

    public WorkflowHumanInteractionProjector(IHumanInteractionPort humanInteractionPort)
    {
        _humanInteractionPort = humanInteractionPort ?? throw new ArgumentNullException(nameof(humanInteractionPort));
    }

    public async ValueTask ProjectAsync(
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!ProjectionDispatchRouteFilter.ShouldDispatch(envelope))
            return;

        if (envelope.Payload?.Is(WorkflowSuspendedEvent.Descriptor) != true)
            return;

        var evt = envelope.Payload.Unpack<WorkflowSuspendedEvent>();
        if (evt.SuspensionType == "tool_approval")
            return;

        if (string.IsNullOrWhiteSpace(evt.DeliveryTargetId))
            return;

        // Refactor (iter163/cluster-003-workflow-suspension-legacy-metadata):
        //   Old pattern: WorkflowSuspendedEvent.Metadata fallback for variable/secure/redacted_output reserved keys.
        //   New principle: typed suspension fields are the single source; Metadata is open extension data only.
        var annotations = BuildAnnotations(evt);

        var request = new HumanInteractionRequest
        {
            ActorId = context.RootActorId,
            RunId = evt.RunId,
            StepId = evt.StepId,
            SuspensionType = evt.SuspensionType,
            Prompt = ResolvePrompt(evt),
            Content = string.IsNullOrWhiteSpace(evt.Content) ? null : evt.Content,
            Options = ResolveOptions(evt),
            InteractionSpec = StepPresentation.HasInteractionSpec(evt.Interaction) ? evt.Interaction.Clone() : null,
            TimeoutSeconds = evt.TimeoutSeconds,
            Annotations = annotations,
        };

        await _humanInteractionPort.DeliverSuspensionAsync(
            request,
            evt.DeliveryTargetId,
            ct);
    }

    private static string ResolvePrompt(WorkflowSuspendedEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.Prompt))
            return evt.Prompt;

        if (!string.IsNullOrWhiteSpace(evt.Interaction?.Body))
            return evt.Interaction.Body;

        return evt.Interaction?.Title ?? string.Empty;
    }

    private static IReadOnlyList<string> ResolveOptions(WorkflowSuspendedEvent evt)
    {
        if (StepPresentation.HasInteractionSpec(evt.Interaction))
        {
            var formActions = evt.Interaction.Actions
                .Where(action => action.Kind == InteractionActionKind.FormSubmit)
                .Select(action => action.ActionId)
                .Where(actionId => !string.IsNullOrWhiteSpace(actionId))
                .ToArray();
            if (formActions.Length > 0)
                return formActions;
        }

        return evt.SuspensionType switch
        {
            "human_approval" => ["approve", "reject"],
            "human_input" => ["submit"],
            "secure_input" => ["submit"],
            _ => Array.Empty<string>(),
        };
    }

    private static Dictionary<string, string> BuildAnnotations(WorkflowSuspendedEvent evt)
    {
        var annotations = WorkflowSuspendedSecureInputMetadata.FilterOpenExtensionMetadata(evt.Metadata);
        var variableName = WorkflowSuspendedSecureInputMetadata.ResolveTypedString(evt.VariableName);
        var secure = evt.Secure;
        var redactedOutput = WorkflowSuspendedSecureInputMetadata.ResolveTypedString(evt.RedactedOutput);

        if (!string.IsNullOrWhiteSpace(variableName))
            annotations["variable"] = variableName;
        if (secure)
            annotations["secure"] = "true";
        if (!string.IsNullOrWhiteSpace(redactedOutput))
            annotations["redacted_output"] = redactedOutput;

        return annotations;
    }
}
