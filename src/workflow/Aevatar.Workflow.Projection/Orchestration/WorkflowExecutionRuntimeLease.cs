using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<WorkflowRunEventEnvelope>,
      IWorkflowExecutionProjectionLease,
      IProjectionPortSessionLease,
      IProjectionContextRuntimeLease<WorkflowExecutionProjectionContext>
{
    public WorkflowExecutionRuntimeLease(WorkflowExecutionProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        CommandId = context.SessionId;
    }

    public string ActorId => RootEntityId;

    public string CommandId { get; }

    public WorkflowExecutionProjectionContext Context { get; }

    public string ScopeId => RootEntityId;

    public string SessionId => CommandId;
}
