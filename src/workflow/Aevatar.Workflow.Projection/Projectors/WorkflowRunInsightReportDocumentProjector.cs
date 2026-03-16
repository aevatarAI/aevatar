using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowRunInsightReportDocumentProjector
    : IProjectionMaterializer<WorkflowExecutionMaterializationContext>
{
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _documentReader;
    private readonly IProjectionWriteDispatcher<WorkflowRunInsightReportDocument> _writeDispatcher;

    public WorkflowRunInsightReportDocumentProjector(
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> documentReader,
        IProjectionWriteDispatcher<WorkflowRunInsightReportDocument> writeDispatcher)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
    }

    public async ValueTask ProjectAsync(
        WorkflowExecutionMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!WorkflowExecutionArtifactProjectionSupport.TryUnpackRootStateEnvelope(envelope, out var stateEvent, out var state) ||
            stateEvent == null ||
            state == null)
            return;

        var existing = await _documentReader.GetAsync(context.RootActorId, ct);
        if (existing != null && WorkflowExecutionArtifactProjectionSupport.ShouldSkip(existing, stateEvent))
            return;

        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, DateTimeOffset.UtcNow);
        var readModel = existing?.DeepClone() ??
                        WorkflowExecutionArtifactProjectionSupport.CreateReportDocument(
                            context,
                            state,
                            stateEvent,
                            observedAt);

        WorkflowExecutionArtifactProjectionSupport.ApplyReportBase(readModel, context, state, stateEvent, observedAt);
        WorkflowExecutionArtifactProjectionSupport.ApplyObservedPayloadToReport(readModel, stateEvent, observedAt);
        await _writeDispatcher.UpsertAsync(readModel, ct);
    }
}
