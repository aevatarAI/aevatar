using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

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

        if (RepresentsCurrentState(command))
            return;

        await PersistDomainEventAsync(new ScopeWorkflowCatalogueRowSourcesObservedEvent
        {
            ScopeId = command.ScopeId.Trim(),
            WorkflowId = command.WorkflowId.Trim(),
            DraftSource = command.DraftSource?.Clone(),
            ServiceSource = command.ServiceSource?.Clone(),
            ObservationEventId = command.ObservationEventId ?? string.Empty,
            ObservedAt = command.ObservedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    protected override ScopeWorkflowCatalogueRowState TransitionState(
        ScopeWorkflowCatalogueRowState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScopeWorkflowCatalogueRowSourcesObservedEvent>(ApplySourcesObserved)
            .OrCurrent();

    private bool RepresentsCurrentState(ObserveScopeWorkflowCatalogueSourcesCommand command) =>
        string.Equals(State.ScopeId, command.ScopeId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(State.WorkflowId, command.WorkflowId?.Trim(), StringComparison.Ordinal) &&
        SameSource(State.DraftSource, command.DraftSource) &&
        SameSource(State.ServiceSource, command.ServiceSource);

    private static ScopeWorkflowCatalogueRowState ApplySourcesObserved(
        ScopeWorkflowCatalogueRowState state,
        ScopeWorkflowCatalogueRowSourcesObservedEvent evt)
    {
        var next = state.Clone();
        next.ScopeId = evt.ScopeId ?? string.Empty;
        next.WorkflowId = evt.WorkflowId ?? string.Empty;
        next.DraftSource = evt.DraftSource?.Clone();
        next.ServiceSource = evt.ServiceSource?.Clone();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.IsNullOrWhiteSpace(evt.ObservationEventId)
            ? BuildEventId(next.ScopeId, next.WorkflowId, next.LastAppliedEventVersion)
            : evt.ObservationEventId.Trim();
        next.ObservedAt = evt.ObservedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }

    private static bool SameSource(
        ScopeWorkflowCatalogueSourceSnapshot? current,
        ScopeWorkflowCatalogueSourceSnapshot? incoming) =>
        current == null && incoming == null ||
        current != null && incoming != null && current.Equals(incoming);

    private static string BuildEventId(string scopeId, string workflowId, long version) =>
        $"{scopeId}:{workflowId}:catalogue-row:{version}";
}
