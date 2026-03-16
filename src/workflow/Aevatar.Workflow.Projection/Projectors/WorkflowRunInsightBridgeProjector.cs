using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.AI.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.Orchestration;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Projectors;

/// <summary>
/// EventEnvelope -> WorkflowRunInsightGAgent bridge projector.
/// </summary>
public sealed class WorkflowRunInsightBridgeProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private readonly IWorkflowRunInsightActorPort _insightActorPort;
    private readonly IProjectionPortActivationService<WorkflowRunInsightRuntimeLease> _insightActivationService;
    private readonly IEventDeduplicator _deduplicator;
    private readonly IProjectionClock _clock;

    public WorkflowRunInsightBridgeProjector(
        IWorkflowRunInsightActorPort insightActorPort,
        IProjectionPortActivationService<WorkflowRunInsightRuntimeLease> insightActivationService,
        IEventDeduplicator deduplicator,
        IProjectionClock clock)
    {
        _insightActorPort = insightActorPort ?? throw new ArgumentNullException(nameof(insightActorPort));
        _insightActivationService = insightActivationService ?? throw new ArgumentNullException(nameof(insightActivationService));
        _deduplicator = deduplicator ?? throw new ArgumentNullException(nameof(deduplicator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await _insightActorPort.EnsureActorAsync(context.RootActorId, ct);
        _ = await _insightActivationService.EnsureAsync(
            context.RootActorId,
            context.WorkflowName,
            context.Input,
            context.CommandId,
            ct);
    }

    public async ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        var observed = ResolveObservedEnvelope(envelope);
        if (observed?.Payload == null)
            return;

        if (!string.IsNullOrWhiteSpace(observed.Id))
        {
            var dedupKey = $"{context.RootActorId}:{observed.Id}";
            if (!await _deduplicator.TryRecordAsync(dedupKey))
                return;
        }

        var now = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        if (!TryBuildObservedEvent(context, observed, now, out var evt))
            return;

        await _insightActorPort.PublishObservedAsync(context.RootActorId, evt, ct);
    }

    public ValueTask CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        var completedAt = _clock.UtcNow;
        return new ValueTask(CompleteCoreAsync(context, topology, completedAt, ct));
    }

    private async Task CompleteCoreAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        DateTimeOffset completedAt,
        CancellationToken ct)
    {
        await _insightActorPort.CaptureTopologyAsync(
            context.RootActorId,
            context.WorkflowName,
            context.CommandId,
            topology,
            completedAt,
            ct);
    }

    private static bool TryBuildObservedEvent(
        WorkflowExecutionProjectionContext context,
        EventEnvelope observed,
        DateTimeOffset observedAt,
        out WorkflowRunInsightObservedEvent evt)
    {
        var payload = observed.Payload;
        if (payload == null)
        {
            evt = null!;
            return false;
        }

        evt = new WorkflowRunInsightObservedEvent
        {
            RootActorId = context.RootActorId,
            WorkflowName = context.WorkflowName ?? string.Empty,
            CommandId = context.CommandId ?? string.Empty,
            StateVersion = ResolveObservedStateVersion(observed),
            SourceEventId = observed.Id ?? string.Empty,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(observedAt.ToUniversalTime()),
            SourcePublisherActorId = ResolvePublisher(observed),
            ObservedType = payload.TypeUrl ?? string.Empty,
            ObservedPayload = payload.Clone(),
        };
        return payload.Is(StartWorkflowEvent.Descriptor)
               || payload.Is(StepRequestEvent.Descriptor)
               || payload.Is(StepCompletedEvent.Descriptor)
               || payload.Is(WorkflowSuspendedEvent.Descriptor)
               || payload.Is(WorkflowCompletedEvent.Descriptor)
               || payload.Is(WaitingForSignalEvent.Descriptor)
               || payload.Is(WorkflowSignalBufferedEvent.Descriptor)
               || payload.Is(TextMessageStartEvent.Descriptor)
               || payload.Is(TextMessageContentEvent.Descriptor)
               || payload.Is(TextMessageEndEvent.Descriptor)
               || payload.Is(ChatResponseEvent.Descriptor)
               || payload.Is(TextMessageReasoningEvent.Descriptor)
               || payload.Is(ToolCallEvent.Descriptor)
               || payload.Is(ToolResultEvent.Descriptor);
    }

    private static EventEnvelope? ResolveObservedEnvelope(EventEnvelope envelope)
    {
        if (CommittedStateEventEnvelope.TryCreateObservedEnvelope(envelope, out var observed) &&
            observed?.Payload != null)
        {
            return observed;
        }

        return envelope.Payload == null ? null : envelope;
    }

    private static string ResolvePublisher(EventEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.Route?.PublisherActorId) ? "(unknown)" : envelope.Route.PublisherActorId;

    private static long ResolveObservedStateVersion(EventEnvelope envelope)
    {
        if (CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out _, out _, out var stateVersion) &&
            stateVersion > 0)
        {
            return stateVersion;
        }

        return 0;
    }
}
