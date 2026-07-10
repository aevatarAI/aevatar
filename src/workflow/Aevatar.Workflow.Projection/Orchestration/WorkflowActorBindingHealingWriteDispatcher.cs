using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Binding-specific write dispatcher that guarantees a workflow definition actor's binding slot can
/// never stay occupied by a Run-kind document.
/// </summary>
/// <remarks>
/// Scoped strictly to <see cref="WorkflowActorBindingDocument"/>; the generic
/// <c>ProjectionWriteResultEvaluator</c> (shared by every projection) is left untouched. The generic
/// evaluator compares only <c>StateVersion</c>, so a Run-kind document frozen at a high run version
/// would permanently reject the definition's own low-version Definition write as Stale. Before a
/// Definition-kind upsert, this decorator detects a Run-kind document already holding that <c>_id</c>
/// and removes it first, clearing the slot so the authoritative Definition document is then written at
/// its own true version (no fabricated version). This is the heal path for any definition <c>_id</c>
/// that was clobbered by a relayed run-bind before the projector skip-on-relay fix landed.
/// </remarks>
public sealed class WorkflowActorBindingHealingWriteDispatcher
    : IProjectionWriteDispatcher<WorkflowActorBindingDocument>
{
    private readonly IProjectionWriteDispatcher<WorkflowActorBindingDocument> _inner;
    private readonly IProjectionDocumentReader<WorkflowActorBindingDocument, string> _reader;

    public WorkflowActorBindingHealingWriteDispatcher(
        IProjectionWriteDispatcher<WorkflowActorBindingDocument> inner,
        IProjectionDocumentReader<WorkflowActorBindingDocument, string> reader)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<ProjectionWriteResult> UpsertAsync(
        WorkflowActorBindingDocument readModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        if (readModel.ActorKind == WorkflowActorKind.Definition &&
            !string.IsNullOrWhiteSpace(readModel.Id))
        {
            var existing = await _reader.GetAsync(readModel.Id, ct);
            if (existing != null && existing.ActorKind == WorkflowActorKind.Run)
                await _inner.DeleteAsync(readModel.Id, ct);
        }

        return await _inner.UpsertAsync(readModel, ct);
    }

    public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();
        return _inner.DeleteAsync(id, ct);
    }
}
