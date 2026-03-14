using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptExecutionProjectionContext
    : IProjectionContext, IProjectionStreamSubscriptionContext
{
    public required string ProjectionId { get; init; }

    public required string RootActorId { get; init; }

    public ScriptReadModelDocument? CurrentSemanticReadModelDocument { get; set; }

    string IProjectionContext.ProjectionId => ProjectionId;

    public IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }
}
