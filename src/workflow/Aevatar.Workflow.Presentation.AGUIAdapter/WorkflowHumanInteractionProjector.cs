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

        // Refactor (iter163/cluster-003-workflow-suspension-legacy-metadata):
        //   Old pattern: WorkflowSuspendedEvent.Metadata fallback for variable/secure/redacted_output reserved keys.
        //   New principle: typed suspension fields are the single source; Metadata is open extension data only.
        var annotations = BuildAnnotations(evt);

        var options = evt.ExpectedOptions.Count > 0
            ? (IReadOnlyList<string>)evt.ExpectedOptions.ToArray()
            : evt.SuspensionType.DefaultExpectedOptions();

        var request = new HumanInteractionRequest
        {
            ActorId = context.RootActorId,
            RunId = evt.RunId,
            StepId = evt.StepId,
            SuspensionType = evt.SuspensionType.ToWireName(),
            Prompt = evt.Prompt,
            Content = string.IsNullOrWhiteSpace(evt.Content) ? null : evt.Content,
            Options = options,
            TimeoutSeconds = evt.TimeoutSeconds,
            Annotations = annotations,
        };

        await _humanInteractionPort.DeliverSuspensionAsync(
            request,
            evt.DeliveryTargetId,
            ct);
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
