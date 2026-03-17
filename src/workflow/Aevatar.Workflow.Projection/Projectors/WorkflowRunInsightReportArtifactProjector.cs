using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowRunInsightReportArtifactProjector
    : IProjectionArtifactMaterializer<WorkflowExecutionMaterializationContext>
{
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _reportReader;
    private readonly IProjectionWriteDispatcher<WorkflowRunInsightReportDocument> _reportWriter;
    private readonly IProjectionWriteDispatcher<WorkflowRunTimelineDocument> _timelineWriter;
    private readonly IProjectionWriteDispatcher<WorkflowRunGraphArtifactDocument> _graphWriter;

    public WorkflowRunInsightReportArtifactProjector(
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> reportReader,
        IProjectionWriteDispatcher<WorkflowRunInsightReportDocument> reportWriter,
        IProjectionWriteDispatcher<WorkflowRunTimelineDocument> timelineWriter,
        IProjectionWriteDispatcher<WorkflowRunGraphArtifactDocument> graphWriter)
    {
        _reportReader = reportReader ?? throw new ArgumentNullException(nameof(reportReader));
        _reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        _timelineWriter = timelineWriter ?? throw new ArgumentNullException(nameof(timelineWriter));
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

        var existing = await _reportReader.GetAsync(context.RootActorId, ct);
        if (existing != null && WorkflowExecutionArtifactMaterializationSupport.ShouldSkip(existing, stateEvent))
            return;

        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, DateTimeOffset.UtcNow);
        var readModel = existing?.DeepClone() ??
                        WorkflowExecutionArtifactMaterializationSupport.CreateReportDocument(
                            context,
                            state,
                            stateEvent,
                            observedAt);

        WorkflowExecutionArtifactMaterializationSupport.ApplyReportBase(readModel, context, state, stateEvent, observedAt);
        WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(readModel, stateEvent, observedAt);
        await _reportWriter.UpsertAsync(readModel, ct);
        await _timelineWriter.UpsertAsync(
            WorkflowExecutionArtifactMaterializationSupport.BuildTimelineDocument(readModel),
            ct);
        await _graphWriter.UpsertAsync(
            WorkflowExecutionArtifactMaterializationSupport.BuildGraphDocument(readModel),
            ct);
    }
}
