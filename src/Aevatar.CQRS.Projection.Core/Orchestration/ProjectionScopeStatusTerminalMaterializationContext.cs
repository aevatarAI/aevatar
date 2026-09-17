namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Activation context of the terminal status materializer of one source projection scope.
/// <see cref="RootActorId"/> is the source scope actor id; the projection kind is the opaque
/// actor-id namespace of the terminal materializer, distinct from the legacy status shadow.
/// </summary>
public sealed class ProjectionScopeStatusTerminalMaterializationContext
    : IProjectionMaterializationContext
{
    public const string ProjectionKindValue = "projection-scope-status-terminal";

    public required string RootActorId { get; init; }

    public string ProjectionKind => ProjectionKindValue;
}

public sealed class ProjectionScopeStatusTerminalRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ProjectionScopeStatusTerminalMaterializationContext>
{
    public ProjectionScopeStatusTerminalRuntimeLease(ProjectionScopeStatusTerminalMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ProjectionScopeStatusTerminalMaterializationContext Context { get; }
}
