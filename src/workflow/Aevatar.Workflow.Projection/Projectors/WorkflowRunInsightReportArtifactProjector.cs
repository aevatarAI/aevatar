using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowRunInsightReportArtifactProjector
    : IProjectionArtifactMaterializer<WorkflowExecutionMaterializationContext>
{
    private readonly IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string> _reportMutator;
    private readonly IProjectionGraphWriter<WorkflowRunInsightReportDocument> _graphWriter;
    private readonly IVersionedProjectionGraphStore? _versionedGraphStore;
    private readonly WorkflowRunIncrementalGraphMaterializer? _incrementalGraphMaterializer;
    private readonly ProjectionGraphProviderStatus? _graphProviderStatus;

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    public WorkflowRunInsightReportArtifactProjector(
        IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string> reportMutator,
        IProjectionGraphWriter<WorkflowRunInsightReportDocument> graphWriter)
        : this(reportMutator, graphWriter, null, null)
    {
    }

    public WorkflowRunInsightReportArtifactProjector(
        IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string> reportMutator,
        IProjectionGraphWriter<WorkflowRunInsightReportDocument> graphWriter,
        IVersionedProjectionGraphStore? versionedGraphStore,
        WorkflowRunIncrementalGraphMaterializer? incrementalGraphMaterializer,
        ProjectionGraphProviderStatus? graphProviderStatus = null)
    {
        _reportMutator = reportMutator ?? throw new ArgumentNullException(nameof(reportMutator));
        _graphWriter = graphWriter ?? throw new ArgumentNullException(nameof(graphWriter));
        _versionedGraphStore = versionedGraphStore;
        _incrementalGraphMaterializer = incrementalGraphMaterializer;
        _graphProviderStatus = graphProviderStatus;
    }

    public async ValueTask ProjectAsync(
        WorkflowExecutionMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!WorkflowExecutionArtifactMaterializationSupport.TryUnpackRootStateEnvelope(envelope, out var stateEvent, out var state) ||
            stateEvent == null ||
            state == null)
            return;

        // The report document is keyed by the authoritative run actor. A child WorkflowRunState can be
        // relayed through the parent's observation stream, but its state version and step set belong to
        // another authority and must never overwrite or incrementally mutate the parent report.
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        if (!string.Equals(context.RootActorId, publisherActorId, StringComparison.Ordinal))
            return;

        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, DateTimeOffset.UtcNow);
        var mutation = await _reportMutator.MutateAsync(
            context.RootActorId,
            existing => ReduceReport(existing, context, state, stateEvent, observedAt),
            ct);
        if (mutation.WriteResult.IsRejected)
        {
            throw new InvalidOperationException(
                $"Workflow report mutation was rejected with {mutation.WriteResult.Disposition}.");
        }

        var readModel = mutation.Document;
        if (readModel == null)
            throw new InvalidOperationException("Workflow report materialization produced no document.");
        if (readModel.StateVersion > stateEvent.Version)
            return;
        if (readModel.StateVersion != stateEvent.Version ||
            !string.Equals(readModel.LastEventId, stateEvent.EventId ?? string.Empty, StringComparison.Ordinal))
        {
            var maintenancePrecedence = readModel.StateVersion == stateEvent.Version
                ? ProjectionWriteResultEvaluator.EvaluateSameVersionMaintenancePrecedence(
                    readModel.LastEventId,
                    stateEvent.EventId)
                : null;
            if (maintenancePrecedence?.Disposition == ProjectionWriteDisposition.Stale)
                return;

            throw new InvalidOperationException(
                $"Workflow report version {stateEvent.Version} was not committed for event '{stateEvent.EventId}'.");
        }

        if (_graphProviderStatus is { Enabled: false })
            return;

        if (!WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(context.MaterializationRoute))
        {
            await _graphWriter.UpsertAsync(readModel, context.ProjectionKind, ct);
            return;
        }

        if (_versionedGraphStore == null || _incrementalGraphMaterializer == null)
        {
            throw new InvalidOperationException(
                "The active workflow graph route requires the versioned incremental graph runtime.");
        }

        var delta = WorkflowRunIncrementalGraphMaterializer.RequiresOwnerGraphReplacement(stateEvent)
            ? _incrementalGraphMaterializer.BuildFullCandidateDelta(
                readModel,
                context.ProjectionKind,
                context.MaterializationRoute!)
            : _incrementalGraphMaterializer.BuildIncrementalDelta(
                readModel,
                stateEvent,
                context.ProjectionKind,
                context.MaterializationRoute!);
        var result = await _versionedGraphStore.ApplyDeltaAsync(delta, ct);
        if (IsCommitted(result))
        {
            return;
        }

        // A committed state snapshot is authoritative at its source version, but the actor may
        // publish one snapshot after a batch advances that version by more than one. The normal
        // incremental delta cannot safely bridge that gap because intermediate payload-specific
        // mutations may be absent. Repair from the complete report instead. EventIdConflict is
        // included for the crash window where that repair committed but the projection scope
        // watermark did not: replay first rebuilds the normal delta, then proves the same full
        // candidate is already present through ExactDuplicate.
        if (result.Disposition is ProjectionGraphDeltaApplyDisposition.Gap or
            ProjectionGraphDeltaApplyDisposition.EventIdConflict)
        {
            var repairDelta = _incrementalGraphMaterializer.BuildFullCandidateDelta(
                readModel,
                context.ProjectionKind,
                context.MaterializationRoute!);
            var repairResult = await _versionedGraphStore.ApplyDeltaAsync(repairDelta, ct);
            if (IsCommitted(repairResult))
                return;

            throw new InvalidOperationException(
                $"Workflow graph delta was rejected with {result.Disposition}: {result.Detail} " +
                $"The full-owner repair was rejected with {repairResult.Disposition}: {repairResult.Detail}");
        }

        throw new InvalidOperationException(
            $"Workflow graph delta was rejected with {result.Disposition}: {result.Detail}");
    }

    private static bool IsCommitted(ProjectionGraphDeltaApplyResult result) =>
        result.Disposition is ProjectionGraphDeltaApplyDisposition.Applied or
            ProjectionGraphDeltaApplyDisposition.ExactDuplicate;

    private static WorkflowRunInsightReportDocument ReduceReport(
        WorkflowRunInsightReportDocument? existing,
        WorkflowExecutionMaterializationContext context,
        WorkflowRunState state,
        StateEvent stateEvent,
        DateTimeOffset observedAt)
    {
        if (existing != null && existing.StateVersion > stateEvent.Version)
            return existing;
        if (existing != null && existing.StateVersion == stateEvent.Version)
        {
            var maintenancePrecedence = ProjectionWriteResultEvaluator
                .EvaluateSameVersionMaintenancePrecedence(existing.LastEventId, stateEvent.EventId);
            if (maintenancePrecedence?.Disposition == ProjectionWriteDisposition.Stale)
                return existing;
            if (maintenancePrecedence?.Disposition != ProjectionWriteDisposition.Applied)
            {
                if (!string.Equals(existing.LastEventId, stateEvent.EventId ?? string.Empty, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Workflow report version {stateEvent.Version} is already bound to event '{existing.LastEventId}'.");
                }

                return existing;
            }
        }

        var readModel = existing?.Clone() ??
                        WorkflowExecutionArtifactMaterializationSupport.CreateReportDocument(
                            context,
                            state,
                            stateEvent,
                            observedAt);
        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(
            readModel,
            context,
            state,
            stateEvent,
            observedAt);
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
            readModel,
            stateEvent,
            observedAt);
        return readModel;
    }
}
