using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowRunInsightReportArtifactProjector
    : IProjectionArtifactMaterializer<WorkflowExecutionMaterializationContext>
{
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _reportReader;
    private readonly IProjectionWriteDispatcher<WorkflowRunInsightReportDocument> _reportWriter;
    private readonly IProjectionGraphWriter<WorkflowRunInsightReportDocument> _graphWriter;

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    public WorkflowRunInsightReportArtifactProjector(
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> reportReader,
        IProjectionWriteDispatcher<WorkflowRunInsightReportDocument> reportWriter,
        IProjectionGraphWriter<WorkflowRunInsightReportDocument> graphWriter)
    {
        _reportReader = reportReader ?? throw new ArgumentNullException(nameof(reportReader));
        _reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        _graphWriter = graphWriter ?? throw new ArgumentNullException(nameof(graphWriter));
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

        var existing = await _reportReader.GetAsync(context.RootActorId, ct);
        if (existing != null && WorkflowExecutionArtifactMaterializationSupport.ShouldSkip(existing, stateEvent))
            return;

        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, DateTimeOffset.UtcNow);
        var readModel = existing?.Clone() ??
                        WorkflowExecutionArtifactMaterializationSupport.CreateReportDocument(
                            context,
                            state,
                            stateEvent,
                            observedAt);

        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(readModel, context, state, stateEvent, observedAt);
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(readModel, stateEvent, observedAt);
        await _reportWriter.UpsertAsync(readModel, ct);
        await _graphWriter.UpsertAsync(readModel, ct);
    }
}
