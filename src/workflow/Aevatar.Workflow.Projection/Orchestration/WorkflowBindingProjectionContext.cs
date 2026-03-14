namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowBindingProjectionContext
    : IProjectionContext,
      IProjectionStreamSubscriptionContext
{
    public required string ProjectionId { get; init; }

    public required string RootActorId { get; init; }

    string IProjectionContext.ProjectionId => ProjectionId;

    public IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }
}
