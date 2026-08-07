using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Workflows;

public sealed record WorkflowDefinitionBindObservationScopeLeasePreparation(
    string ActorId,
    string CommandId);

public interface IWorkflowDefinitionBindObservationScopeLeasePreparationPort
{
    Task<WorkflowDefinitionBindObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string commandId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        WorkflowDefinitionBindObservationScopeLeasePreparation preparation,
        CancellationToken ct = default);
}

public interface IWorkflowDefinitionBindObservationProjectionLease
{
    string ActorId { get; }

    string CommandId { get; }
}

public interface IWorkflowDefinitionBindObservationProjectionPort
    : IEventSinkProjectionLifecyclePort<IWorkflowDefinitionBindObservationProjectionLease, EventEnvelope>
{
    Task<EventSinkProjectionAttachment<IWorkflowDefinitionBindObservationProjectionLease>?>
        AttachExistingDefinitionProjectionAsync(
            string actorId,
            string commandId,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default);
}
