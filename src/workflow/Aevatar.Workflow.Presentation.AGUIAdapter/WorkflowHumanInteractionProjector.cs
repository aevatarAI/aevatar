using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public sealed class WorkflowHumanInteractionProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext>
{
    private readonly IHumanInteractionPort _humanInteractionPort;
    private readonly ILogger<WorkflowHumanInteractionProjector> _logger;

    public WorkflowHumanInteractionProjector(
        IHumanInteractionPort humanInteractionPort,
        ILogger<WorkflowHumanInteractionProjector> logger)
    {
        _humanInteractionPort = humanInteractionPort ?? throw new ArgumentNullException(nameof(humanInteractionPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var sourceEnvelope = CommittedStateEventEnvelope.TryCreateObservedEnvelope(envelope, out var observedEnvelope) &&
                             observedEnvelope?.Payload != null
            ? observedEnvelope
            : envelope;
        if (sourceEnvelope.Payload?.Is(WorkflowSuspendedEvent.Descriptor) != true)
            return;

        var evt = sourceEnvelope.Payload.Unpack<WorkflowSuspendedEvent>();
        if (evt.SuspensionType == "tool_approval")
        {
            _logger.LogInformation(
                "Skipping workflow human interaction suspension delivery: actor={ActorId}, run={RunId}, step={StepId}, reason=tool_approval",
                context.RootActorId,
                evt.RunId,
                evt.StepId);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.DeliveryTargetId))
        {
            _logger.LogWarning(
                "Skipping workflow human interaction suspension delivery: actor={ActorId}, run={RunId}, step={StepId}, suspensionType={SuspensionType}, reason=missing_delivery_target_id",
                context.RootActorId,
                evt.RunId,
                evt.StepId,
                evt.SuspensionType);
            return;
        }

        // Refactor (iter163/cluster-003-workflow-suspension-legacy-metadata):
        //   Old pattern: WorkflowSuspendedEvent.Metadata fallback for variable/secure/redacted_output reserved keys.
        //   New principle: typed suspension fields are the single source; Metadata is open extension data only.
        var annotations = BuildAnnotations(evt);

        var request = new HumanInteractionRequest
        {
            ActorId = context.RootActorId,
            RunId = evt.RunId,
            StepId = evt.StepId,
            SourceEventId = sourceEnvelope.Id,
            IssuedAtUnixMs = AGUIEventEnvelopeMappingHelpers.ToUnixMs(sourceEnvelope.Timestamp) ?? 0,
            SuspensionType = evt.SuspensionType,
            Prompt = ResolvePrompt(evt),
            Content = string.IsNullOrWhiteSpace(evt.Content) ? null : evt.Content,
            Options = ResolveOptions(evt),
            InteractionSpec = StepPresentation.HasInteractionSpec(evt.Interaction) ? evt.Interaction.Clone() : null,
            TimeoutSeconds = evt.TimeoutSeconds,
            Annotations = annotations,
        };

        _logger.LogInformation(
            "Delivering workflow human interaction suspension: actor={ActorId}, run={RunId}, step={StepId}, suspensionType={SuspensionType}, deliveryTargetId={DeliveryTargetId}, options={OptionsCount}, timeoutSeconds={TimeoutSeconds}",
            request.ActorId,
            request.RunId,
            request.StepId,
            request.SuspensionType,
            evt.DeliveryTargetId,
            request.Options.Count,
            request.TimeoutSeconds);

        await _humanInteractionPort.DeliverSuspensionAsync(
            request,
            evt.DeliveryTargetId,
            ct);

        _logger.LogInformation(
            "Delivered workflow human interaction suspension to port: actor={ActorId}, run={RunId}, step={StepId}, deliveryTargetId={DeliveryTargetId}",
            request.ActorId,
            request.RunId,
            request.StepId,
            evt.DeliveryTargetId);
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
