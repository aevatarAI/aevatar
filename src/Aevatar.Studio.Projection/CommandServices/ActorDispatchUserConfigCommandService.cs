using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Dispatches user-config write commands to the <see cref="UserConfigGAgent"/>.
/// Uses <see cref="IStudioActorBootstrap"/> so the actor is created (if
/// absent) and its projection scope is activated atomically before we
/// dispatch the command through <see cref="IActorDispatchPort"/>.
/// </summary>
internal sealed class ActorDispatchUserConfigCommandService : IUserConfigCommandService
{
    private const string ActorIdPrefix = "user-config-";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAppScopeResolver _scopeResolver;

    public ActorDispatchUserConfigCommandService(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort,
        IAppScopeResolver scopeResolver)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
    }

    public async Task SaveAsync(UserConfig config, CancellationToken ct = default)
    {
        var actorId = ActorIdPrefix + (_scopeResolver.Resolve()?.ScopeId ?? "default");

        var actor = await _bootstrap.EnsureAsync<UserConfigGAgent>(actorId, ct);

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
            // Direct route matches the target grain and triggers the event
            // handler pipeline — same pattern used by every other working
            // write path in the repo (GAgentService / ProjectionScope etc.).
            Route = EnvelopeRouteSemantics.CreateDirect(
                "aevatar.studio.projection.user-config", actor.Id),
        };

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }
}
