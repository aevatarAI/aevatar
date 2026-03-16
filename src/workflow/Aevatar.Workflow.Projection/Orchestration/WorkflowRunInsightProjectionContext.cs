using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowRunInsightProjectionContext
    : IProjectionContext, IProjectionStreamSubscriptionContext
{
    public required string ProjectionId { get; init; }
    public required string RootActorId { get; init; }
    public required string RunActorId { get; init; }

    public IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }
}
