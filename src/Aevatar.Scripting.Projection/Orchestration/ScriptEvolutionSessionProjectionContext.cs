namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionSessionProjectionContext
    : IProjectionContext, IProjectionStreamSubscriptionContext
{
    public string ProjectionId { get; set; } = string.Empty;
    public string RootActorId { get; set; } = string.Empty;
    public string ProposalId { get; set; } = string.Empty;

    public IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }
}
