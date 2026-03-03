using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionSinkSubscriptionManager
    : IProjectionPortSinkSubscriptionManager<WorkflowExecutionRuntimeLease, IEventSink<WorkflowRunEvent>, WorkflowRunEvent>
{
}

public interface IWorkflowProjectionLiveSinkForwarder
    : IProjectionPortLiveSinkForwarder<WorkflowExecutionRuntimeLease, IEventSink<WorkflowRunEvent>, WorkflowRunEvent>
{
}

public abstract class WorkflowProjectionLifecyclePortServiceBase
    : EventSinkProjectionLifecyclePortServiceBase<IWorkflowExecutionProjectionLease, WorkflowExecutionRuntimeLease, WorkflowRunEvent>
{
    protected WorkflowProjectionLifecyclePortServiceBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionPortActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionPortReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IWorkflowProjectionSinkSubscriptionManager sinkSubscriptionManager,
        IWorkflowProjectionLiveSinkForwarder liveSinkForwarder)
        : base(
            projectionEnabledAccessor,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder,
            ResolveRuntimeLeaseOrThrow)
    {
    }

    private static WorkflowExecutionRuntimeLease ResolveRuntimeLeaseOrThrow(IWorkflowExecutionProjectionLease lease) =>
        lease as WorkflowExecutionRuntimeLease
        ?? throw new InvalidOperationException("Unsupported workflow projection lease implementation.");
}
