using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionRuntimeLease
    : ProjectionRuntimeLeaseBase<IEventSink<WorkflowRunEvent>>,
      IWorkflowExecutionProjectionLease,
      IProjectionPortSessionLease
{
    public WorkflowExecutionRuntimeLease(WorkflowExecutionProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context;
        CommandId = context.CommandId;
    }

    public string ActorId => RootEntityId;
    public string CommandId { get; }
    public WorkflowExecutionProjectionContext Context { get; }

    public string ScopeId => RootEntityId;
    public string SessionId => CommandId;
}
