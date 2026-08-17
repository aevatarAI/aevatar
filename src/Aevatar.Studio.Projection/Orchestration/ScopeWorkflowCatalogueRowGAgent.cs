using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Orchestration;

[GAgent("gagent.scope-workflow-catalogue-row")]
public sealed class ScopeWorkflowCatalogueRowGAgent : GAgentBase<ScopeWorkflowCatalogueRowState>, IProjectedActor
{
    public const string DurableProjectionKind = "scope-workflow-catalogue-row";

    public static string ProjectionKind => DurableProjectionKind;

    public ScopeWorkflowCatalogueRowGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleObserveSourcesAsync(ObserveScopeWorkflowCatalogueSourcesCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.ScopeId) || string.IsNullOrWhiteSpace(command.WorkflowId))
            return;

        if (RepresentsCurrentState(State, command))
            return;

        await PersistDomainEventAsync(new ScopeWorkflowCatalogueRowSourcesObservedEvent
        {
            ScopeId = command.ScopeId.Trim(),
            WorkflowId = command.WorkflowId.Trim(),
            DraftSource = command.DraftSource?.Clone(),
            ServiceSource = command.ServiceSource?.Clone(),
            ObservationEventId = command.ObservationEventId ?? string.Empty,
            ObservedAt = command.ObservedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            DraftWatermarkUtc = command.DraftWatermarkUtc?.Clone(),
            ServiceWatermarkUtc = command.ServiceWatermarkUtc?.Clone(),
        });
    }

    protected override ScopeWorkflowCatalogueRowState TransitionState(
        ScopeWorkflowCatalogueRowState current,
        IMessage evt) =>
        Transition(current, evt);

    internal static ScopeWorkflowCatalogueRowState Transition(
        ScopeWorkflowCatalogueRowState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScopeWorkflowCatalogueRowSourcesObservedEvent>(ApplySourcesObserved)
            .OrCurrent();

    internal static bool RepresentsCurrentState(
        ScopeWorkflowCatalogueRowState state,
        ObserveScopeWorkflowCatalogueSourcesCommand command) =>
        string.Equals(state.ScopeId, command.ScopeId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.WorkflowId, command.WorkflowId?.Trim(), StringComparison.Ordinal) &&
        SameSource(state.DraftSource, command.DraftSource) &&
        SameSource(state.ServiceSource, command.ServiceSource) &&
        SameWatermark(state.DraftWatermarkUtc, command.DraftWatermarkUtc) &&
        SameWatermark(state.ServiceWatermarkUtc, command.ServiceWatermarkUtc);

    private static ScopeWorkflowCatalogueRowState ApplySourcesObserved(
        ScopeWorkflowCatalogueRowState state,
        ScopeWorkflowCatalogueRowSourcesObservedEvent evt)
    {
        var next = state.Clone();
        next.ScopeId = evt.ScopeId ?? string.Empty;
        next.WorkflowId = evt.WorkflowId ?? string.Empty;
        ApplySource(
            evt.DraftSource,
            evt.DraftWatermarkUtc,
            state.DraftWatermarkUtc,
            source => next.DraftSource = source,
            watermark => next.DraftWatermarkUtc = watermark);
        ApplySource(
            evt.ServiceSource,
            evt.ServiceWatermarkUtc,
            state.ServiceWatermarkUtc,
            source => next.ServiceSource = source,
            watermark => next.ServiceWatermarkUtc = watermark);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.IsNullOrWhiteSpace(evt.ObservationEventId)
            ? BuildEventId(next.ScopeId, next.WorkflowId, next.LastAppliedEventVersion)
            : evt.ObservationEventId.Trim();
        next.ObservedAt = evt.ObservedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }

    private static void ApplySource(
        ScopeWorkflowCatalogueSourceSnapshot? incoming,
        Timestamp? incomingWatermark,
        Timestamp? currentWatermark,
        Action<ScopeWorkflowCatalogueSourceSnapshot?> setSource,
        Action<Timestamp?> setWatermark)
    {
        if (CompareWatermark(incomingWatermark, currentWatermark) < 0)
            return;

        setSource(incoming?.Clone());
        setWatermark(incomingWatermark?.Clone());
    }

    private static int CompareWatermark(Timestamp? left, Timestamp? right) =>
        (left?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
        .CompareTo(right?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);

    private static bool SameSource(
        ScopeWorkflowCatalogueSourceSnapshot? current,
        ScopeWorkflowCatalogueSourceSnapshot? incoming) =>
        current == null && incoming == null ||
        current != null && incoming != null && current.Equals(incoming);

    private static bool SameWatermark(Timestamp? current, Timestamp? incoming) =>
        current == null && incoming == null ||
        current != null && incoming != null && current.Equals(incoming);

    private static string BuildEventId(string scopeId, string workflowId, long version) =>
        $"{scopeId}:{workflowId}:catalogue-row:{version}";
}
