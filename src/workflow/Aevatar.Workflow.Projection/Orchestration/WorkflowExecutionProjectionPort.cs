using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IWorkflowExecutionProjectionLease, WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>,
      IWorkflowExecutionProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<WorkflowExecutionRuntimeLease> _attachExistingLeaseLookup;

    public WorkflowExecutionProjectionPort(
        WorkflowExecutionProjectionOptions options,
        IProjectionScopeReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IProjectionSessionEventHub<WorkflowRunEventEnvelope> sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<WorkflowExecutionRuntimeLease> attachExistingLeaseLookup)
        : base(
            () => options.Enabled,
            releaseService,
            sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
    }

    // Refactor (iter51/issue-898-projection-attach-existing-side-read):
    //   Old pattern: Feature projection ports duplicated IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()) for attach-existing checks (post-#884 #884 fixed 3 ports but more remained).
    //   New principle: All attach-existing lease lookups go through typed IProjectionScopeAttachExistingLeaseLookup<TLease>; CI guard prevents recurrence.
    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    public async Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
        string rootActorId,
        string commandId,
        IEventSink<WorkflowRunEventEnvelope> sink,
        CancellationToken ct = default)
    {
        // Refactor (iter101/cluster-104): Old lifecycle base exposed direct ensure/rebuild-like activation to request ports; workflow observation now attaches only to existing sessions.
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(rootActorId) ||
            string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        var lease = await _attachExistingLeaseLookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = rootActorId,
            ProjectionKind = WorkflowProjectionKinds.ExecutionSession,
            Mode = ProjectionRuntimeMode.SessionObservation,
            SessionId = commandId,
        }, ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>(lease, liveSinkLease);
    }
}
