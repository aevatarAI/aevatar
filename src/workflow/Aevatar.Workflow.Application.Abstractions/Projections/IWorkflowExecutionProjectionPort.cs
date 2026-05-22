using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionProjectionPort
    : IEventSinkProjectionLifecyclePort<IWorkflowExecutionProjectionLease, WorkflowRunEventEnvelope>
{
    Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct = default);

    // Refactor (iter35/cluster-039-observation-binder-attach-only):
    //   Old pattern: Command observation binders synchronously ensure and attach projection leases before dispatch,让 request/command preparation 拥有 projection lifecycle。
    //   New principle: Command observation binders request attach only for an existing projection-owned session; cold sessions return unavailable without command-side ensure/activate.
    Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
        string rootActorId,
        string commandId,
        IEventSink<WorkflowRunEventEnvelope> sink,
        CancellationToken ct = default);
}
