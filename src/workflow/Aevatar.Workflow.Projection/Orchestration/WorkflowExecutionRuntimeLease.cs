using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

// Refactor (iter367/cluster-issue377): Old pattern: runtime lease implemented IProjectionPortSessionLease.
// Refactor (iter367/cluster-issue377): Old pattern: ScopeId repeated Context.RootActorId for session routing.
// Refactor (iter367/cluster-issue377): New principle: workflow session context is the routing authority.
// Refactor (iter367/cluster-issue377): New principle: lifecycle attach uses Context.RootActorId and Context.SessionId.
public sealed class WorkflowExecutionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<WorkflowRunEventEnvelope>,
      IWorkflowExecutionProjectionLease,
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

    public string SessionId => CommandId;
}
