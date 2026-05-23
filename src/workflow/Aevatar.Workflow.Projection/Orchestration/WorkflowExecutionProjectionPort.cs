using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IWorkflowExecutionProjectionLease, WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>,
      IWorkflowExecutionProjectionPort
{
    private readonly IActorRuntime _runtime;

    public WorkflowExecutionProjectionPort(
        WorkflowExecutionProjectionOptions options,
        IProjectionScopeActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionScopeReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IProjectionSessionEventHub<WorkflowRunEventEnvelope> sessionEventHub,
        IActorRuntime runtime)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sessionEventHub)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    public async Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
        string rootActorId,
        string commandId,
        IEventSink<WorkflowRunEventEnvelope> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(rootActorId) ||
            string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            rootActorId,
            WorkflowProjectionKinds.ExecutionSession,
            ProjectionRuntimeMode.SessionObservation,
            commandId);
        if (!await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false))
            return null;

        var lease = new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = rootActorId,
            ProjectionKind = WorkflowProjectionKinds.ExecutionSession,
            SessionId = commandId,
        });
        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>(lease, liveSinkLease);
    }
}
