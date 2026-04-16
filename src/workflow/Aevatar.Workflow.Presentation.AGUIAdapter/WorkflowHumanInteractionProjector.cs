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

        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in evt.Metadata)
            annotations[key] = value;

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
}
