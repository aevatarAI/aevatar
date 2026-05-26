using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionProjectionPort
    : IEventSinkProjectionLifecyclePort<IWorkflowExecutionProjectionLease, WorkflowRunEventEnvelope>
{
    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
        string rootActorId,
        string commandId,
        IEventSink<WorkflowRunEventEnvelope> sink,
        CancellationToken ct = default);
}
