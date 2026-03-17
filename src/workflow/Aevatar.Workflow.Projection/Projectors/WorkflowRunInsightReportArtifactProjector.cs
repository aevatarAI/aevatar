using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowRunInsightReportArtifactProjector
    : IProjectionArtifactMaterializer<WorkflowExecutionMaterializationContext>
{
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _documentReader;
    private readonly IProjectionWriteDispatcher<WorkflowRunInsightReportDocument> _writeDispatcher;

    public WorkflowRunInsightReportArtifactProjector(
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
        if (!WorkflowExecutionArtifactMaterializationSupport.TryUnpackRootStateEnvelope(envelope, out var stateEvent, out var state) ||
            stateEvent == null ||
            state == null)
            return;

        var existing = await _documentReader.GetAsync(context.RootActorId, ct);
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
        await _writeDispatcher.UpsertAsync(readModel, ct);
    }
}
