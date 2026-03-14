using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowBindingProjectionPortService
    : EventSinkProjectionLifecyclePortServiceBase<IProjectionPortSessionLease, WorkflowBindingRuntimeLease, EventEnvelope>
{
    private const string ProjectionName = "workflow-actor-binding";

    public WorkflowBindingProjectionPortService(
        IProjectionPortActivationService<WorkflowBindingRuntimeLease> activationService,
        IProjectionPortReleaseService<WorkflowBindingRuntimeLease> releaseService,
        IEventSinkProjectionSubscriptionManager<WorkflowBindingRuntimeLease, EventEnvelope> sinkSubscriptionManager,
        IEventSinkProjectionLiveForwarder<WorkflowBindingRuntimeLease, EventEnvelope> liveSinkForwarder)
        : base(
            static () => true,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
    }

    public Task<IProjectionPortSessionLease?> EnsureActorProjectionAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            actorId,
            ProjectionName,
            input: string.Empty,
            commandId: actorId,
            ct);
}
