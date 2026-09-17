using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowDefinitionBindObservationProjectionPort
    : EventSinkProjectionLifecyclePortBase<
        IWorkflowDefinitionBindObservationProjectionLease,
        WorkflowDefinitionBindObservationRuntimeLease,
        EventEnvelope>,
      IWorkflowDefinitionBindObservationProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<WorkflowDefinitionBindObservationRuntimeLease>
        _attachExistingLeaseLookup;

    public WorkflowDefinitionBindObservationProjectionPort(
        WorkflowExecutionProjectionOptions options,
        IProjectionScopeReleaseService<WorkflowDefinitionBindObservationRuntimeLease> releaseService,
        WorkflowDefinitionBindObservationSessionEventHub sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<WorkflowDefinitionBindObservationRuntimeLease>
            attachExistingLeaseLookup)
        : base(() => options.Enabled, releaseService, sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ??
                                     throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
    }

    public async Task<EventSinkProjectionAttachment<IWorkflowDefinitionBindObservationProjectionLease>?>
        AttachExistingDefinitionProjectionAsync(
            string actorId,
            string commandId,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        var lease = await _attachExistingLeaseLookup.TryGetAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId.Trim(),
                ProjectionKind = WorkflowProjectionKinds.DefinitionBindObservation,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = commandId.Trim(),
            },
            ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IWorkflowDefinitionBindObservationProjectionLease>(
                lease,
                liveSinkLease);
    }
}
