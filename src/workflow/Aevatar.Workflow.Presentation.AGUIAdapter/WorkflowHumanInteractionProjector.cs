using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Workflow.Abstractions;
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
        if (string.IsNullOrWhiteSpace(evt.DeliveryTargetId))
            return;

        // Refactor (iter79/cluster-079-secure-input-suspension-metadata-bag):
        //   Old pattern: WorkflowSuspendedEvent.Metadata string bag for secure/input_mode/redacted_output/variable
        //   New principle (delete framing): typed bool secure + string redacted_output + reuse variable_name; Metadata open extension only; reserved keys read-only fallback
        var annotations = BuildAnnotations(evt);

        var request = new HumanInteractionRequest
        {
            ActorId = context.RootActorId,
            RunId = evt.RunId,
            StepId = evt.StepId,
            SuspensionType = evt.SuspensionType,
            Prompt = evt.Prompt,
            Content = string.IsNullOrWhiteSpace(evt.Content) ? null : evt.Content,
            Options = ResolveOptions(evt.SuspensionType),
            TimeoutSeconds = evt.TimeoutSeconds,
            Annotations = annotations,
        };

        await _humanInteractionPort.DeliverSuspensionAsync(
            request,
            evt.DeliveryTargetId,
            ct);
    }

    private static IReadOnlyList<string> ResolveOptions(string suspensionType) =>
        suspensionType switch
        {
            "human_approval" => ["approve", "reject"],
            "human_input" => ["submit"],
            "secure_input" => ["submit"],
            _ => Array.Empty<string>(),
        };

    private static Dictionary<string, string> BuildAnnotations(WorkflowSuspendedEvent evt)
    {
        var annotations = WorkflowSuspendedSecureInputMetadata.FilterOpenExtensionMetadata(evt.Metadata);
        var variableName = WorkflowSuspendedSecureInputMetadata.ResolveVariableName(evt.VariableName, evt.Metadata);
        var secure = WorkflowSuspendedSecureInputMetadata.ResolveSecure(evt.Secure, evt.Metadata);
        var redactedOutput = WorkflowSuspendedSecureInputMetadata.ResolveRedactedOutput(evt.RedactedOutput, evt.Metadata);

        if (!string.IsNullOrWhiteSpace(variableName))
            annotations["variable"] = variableName;
        if (secure)
            annotations["secure"] = "true";
        if (!string.IsNullOrWhiteSpace(redactedOutput))
            annotations["redacted_output"] = redactedOutput;

        return annotations;
    }
}
