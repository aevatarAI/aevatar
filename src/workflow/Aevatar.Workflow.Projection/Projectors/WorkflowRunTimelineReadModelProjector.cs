using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowRunTimelineReadModelProjector
    : IProjectionMaterializer<WorkflowExecutionMaterializationContext>
{
    private readonly IProjectionDocumentReader<WorkflowRunTimelineDocument, string> _documentReader;
    private readonly IProjectionWriteDispatcher<WorkflowRunTimelineDocument> _writeDispatcher;

    public WorkflowRunTimelineReadModelProjector(
        IProjectionDocumentReader<WorkflowRunTimelineDocument, string> documentReader,
        IProjectionWriteDispatcher<WorkflowRunTimelineDocument> writeDispatcher)
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
        var document = existing?.DeepClone() ??
                       WorkflowExecutionArtifactProjectionSupport.CreateTimelineDocument(
            context,
            state,
            stateEvent,
            observedAt);

        WorkflowExecutionArtifactProjectionSupport.ApplyTimelineBase(document, context, state, stateEvent, observedAt);
        WorkflowExecutionArtifactProjectionSupport.ApplyObservedPayloadToTimeline(
            document,
            stateEvent,
            observedAt,
            context.RootActorId);
        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
