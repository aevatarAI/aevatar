namespace Aevatar.GAgentService.Projection.Contexts;

public sealed class ServiceServingSetProjectionContext
    : IProjectionContext, IProjectionStreamSubscriptionContext
{
    public required string ProjectionId { get; init; }

    public required string RootActorId { get; init; }

    string IProjectionContext.ProjectionId => ProjectionId;

    public IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }
}
