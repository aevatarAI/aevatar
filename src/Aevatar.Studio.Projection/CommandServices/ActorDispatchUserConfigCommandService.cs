using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Dispatches user-config write commands to the <c>UserConfigGAgent</c>.
/// For the first save in a scope, the <c>user-config-{scopeId}</c> actor
/// does not exist yet — we lazily create it via <see cref="IActorRuntime"/>
/// before dispatching, mirroring the actor-backed catalog stores
/// (<c>ActorBackedRoleCatalogStore.EnsureWriteActorAsync</c>).
///
/// We also activate the Studio projection scope for this actor via
/// <see cref="StudioProjectionPort"/> so <c>UserConfigCurrentStateProjector</c>
/// actually subscribes to the actor's committed events and writes them to
/// the read-model store. Without this, saves look successful but the read
/// document is never created and refreshing the page reverts to defaults.
/// </summary>
internal sealed class ActorDispatchUserConfigCommandService : IUserConfigCommandService
{
    private const string ActorIdPrefix = "user-config-";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly StudioProjectionPort _projectionPort;

    public ActorDispatchUserConfigCommandService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAppScopeResolver scopeResolver,
        StudioProjectionPort projectionPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task SaveAsync(UserConfig config, CancellationToken ct = default)
    {
        var actorId = ActorIdPrefix + (_scopeResolver.Resolve()?.ScopeId ?? "default");

        // Ensure the actor exists before dispatching. Without this, the first
        // save for a scope fails with "Actor {actorId} is not initialized".
        var existing = await _runtime.GetAsync(actorId);
        if (existing is null)
        {
            await _runtime.CreateAsync<UserConfigGAgent>(actorId, ct);
        }

        // Activate the projection scope so the materialization subscribes to
        // the actor's stream before the save event is published. Idempotent —
        // safe to call on every save.
        await _projectionPort.EnsureProjectionAsync(
            actorId, StudioProjectionKinds.UserConfig, ct);

        var evt = new UserConfigUpdatedEvent
        {
            DefaultModel = config.DefaultModel,
            PreferredLlmRoute = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute),
            RuntimeMode = UserConfigRuntime.NormalizeMode(config.RuntimeMode),
            LocalRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.LocalRuntimeBaseUrl,
                UserConfigRuntimeDefaults.LocalRuntimeBaseUrl),
            RemoteRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.RemoteRuntimeBaseUrl,
                UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl),
            MaxToolRounds = config.MaxToolRounds,
        };

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                actorId, TopologyAudience.Self),
        };

        await _dispatchPort.DispatchAsync(actorId, envelope, ct);
    }
}
