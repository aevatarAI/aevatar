using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// Re-materializes a stale/missing <c>ChatRoutePolicyCurrentStateDocument</c> by re-firing the SAME
/// committed-state activation plan a write fires (see
/// <see cref="ChatRoutePolicyCommittedStateProjectionActivationPlanProvider"/>) — but for a known policy
/// actor id and without committing a new event. Used by the voice ingress to self-heal long-idle
/// projection staleness without touching the read-model-only query path or the write-only command port.
/// </summary>
internal sealed class ChatRoutePolicyProjectionRecoveryPort : IChatRoutePolicyProjectionRecoveryPort
{
    // Must match ChatRoutePolicyCommandPort's actor-id derivation ("chat-route-policy:{scopeId}").
    private const string ActorIdPrefix = "chat-route-policy:";

    private readonly IActorRuntime _actorRuntime;
    private readonly ProjectionActivationPlanDispatcher _dispatcher;
    private readonly ILogger<ChatRoutePolicyProjectionRecoveryPort> _logger;

    public ChatRoutePolicyProjectionRecoveryPort(
        IActorRuntime actorRuntime,
        ProjectionActivationPlanDispatcher dispatcher,
        ILogger<ChatRoutePolicyProjectionRecoveryPort>? logger = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger<ChatRoutePolicyProjectionRecoveryPort>.Instance;
    }

    public async Task<bool> TryRematerializeAsync(OwnerScope callerScope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerScope);

        // The policy actor is keyed by the owner-scope id (ChatRoutePolicyCommandPort:
        // "chat-route-policy:{scopeId}"). For the NyxID-native voice caller that scope id is the
        // NyxUserId. Without it there is no actor to re-materialize on this path.
        var scopeId = callerScope.NyxUserId;
        if (string.IsNullOrWhiteSpace(scopeId))
            return false;

        var actorId = $"{ActorIdPrefix}{scopeId.Trim()}";
        try
        {
            // Only re-materialize policies that ACTUALLY EXIST. The materialization dispatch durably
            // creates a projection-scope grain for the actor; firing it for a scope that never had a
            // policy (the source grain has no committed events) would leave an empty, useless scope grain
            // behind for every policyless caller. Gate on the same cheap, non-activating existence check
            // the projection runtime itself uses (ProjectionScopeActorRuntime.EnsureExistsAsync). This
            // never creates the source grain — ExistsAsync is a read; the heal only re-projects
            // already-committed events (no new commit, no grain mutation, no version bump).
            if (!await _actorRuntime.ExistsAsync(actorId))
                return false;

            // Identical to the plan ChatRoutePolicyCommittedStateProjectionActivationPlanProvider yields
            // on a commit, re-fired for the known actor id. It re-projects from already-committed events:
            // no new commit, no grain mutation, no State.Version bump.
            await _dispatcher.DispatchAsync(
                new ProjectionActivationPlan
                {
                    LeaseType = typeof(ChatRoutePolicyMaterializationRuntimeLease),
                    StartRequest = new ProjectionScopeStartRequest
                    {
                        RootActorId = actorId,
                        ProjectionKind = ChatRoutePolicyGAgent.ProjectionKind,
                        Mode = ProjectionRuntimeMode.DurableMaterialization,
                    },
                },
                ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "chat-route-policy projection re-materialize failed for actor {ActorId}", actorId);
            return false;
        }
    }
}
