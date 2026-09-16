using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowDefinitionBindObservationRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<EventEnvelope>,
      IWorkflowDefinitionBindObservationProjectionLease,
      IProjectionContextRuntimeLease<WorkflowDefinitionBindObservationProjectionContext>
{
    public WorkflowDefinitionBindObservationRuntimeLease(
        WorkflowDefinitionBindObservationProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        CommandId = context.SessionId;
    }

    public string ActorId => RootEntityId;

    public string CommandId { get; }

    public WorkflowDefinitionBindObservationProjectionContext Context { get; }
}
