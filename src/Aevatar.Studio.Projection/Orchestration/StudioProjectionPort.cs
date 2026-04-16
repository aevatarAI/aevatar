using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Studio.Projection.Orchestration;

/// <summary>
/// Activates the Studio materialization scope for a given Studio actor so
/// the <c>ProjectionMaterializationScopeGAgent&lt;StudioMaterializationContext&gt;</c>
/// subscribes to its event stream and hands every committed event to the
/// registered Studio projectors. Without this activation the actor writes
/// fine but nothing gets materialized into the read-model document store —
/// every GET then returns defaults and "persisted" data appears to vanish
/// on refresh.
///
/// Mirror of
/// <c>Aevatar.GAgentService.Governance.Projection.Orchestration.ServiceConfigurationProjectionPort</c>
/// and <c>Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationProjectionPort</c>,
/// applied here to the Studio runtime lease.
/// </summary>
public sealed class StudioProjectionPort
    : MaterializationProjectionPortBase<StudioMaterializationRuntimeLease>
{
    public StudioProjectionPort(
        IProjectionScopeActivationService<StudioMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<StudioMaterializationRuntimeLease?> EnsureProjectionAsync(
        string actorId,
        string projectionKind,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return Task.FromResult<StudioMaterializationRuntimeLease?>(null);
        if (string.IsNullOrWhiteSpace(projectionKind))
            return Task.FromResult<StudioMaterializationRuntimeLease?>(null);

        return EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
    }
}

/// <summary>
/// Canonical projection-kind strings for the Studio materialization scope.
/// Each actor type gets its own kind so the scope agent IDs don't collide.
/// </summary>
public static class StudioProjectionKinds
{
    public const string UserConfig = "user-config";
    public const string RoleCatalog = "role-catalog";
    public const string ConnectorCatalog = "connector-catalog";
    public const string ChatHistoryIndex = "chat-history-index";
    public const string ChatConversation = "chat-conversation";
    public const string GAgentRegistry = "gagent-registry";
    public const string UserMemory = "user-memory";
    public const string StreamingProxyParticipant = "streaming-proxy-participant";
}
