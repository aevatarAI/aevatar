using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IUserConfigStore"/>.
/// Per-scope isolation: each scope gets its own <c>user-config-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedUserConfigStore : IUserConfigStore, IAsyncDisposable
{
    private const string ActorIdPrefix = "user-config-";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly ILogger<ActorBackedUserConfigStore> _logger;

    private readonly ConcurrentDictionary<string, ScopeState> _scopes = new(StringComparer.Ordinal);

    public ActorBackedUserConfigStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        IAppScopeResolver scopeResolver,
        ILogger<ActorBackedUserConfigStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        var scopeState = await EnsureScopeAsync(cancellationToken);

        var state = scopeState.Snapshot;
        if (state is null)
            return CreateDefaultConfig();

        return new UserConfig(
            DefaultModel: state.DefaultModel,
            PreferredLlmRoute: string.IsNullOrEmpty(state.PreferredLlmRoute)
                ? UserConfigLlmRouteDefaults.Gateway
                : state.PreferredLlmRoute,
            RuntimeMode: string.IsNullOrEmpty(state.RuntimeMode)
                ? UserConfigRuntimeDefaults.LocalMode
                : state.RuntimeMode,
            LocalRuntimeBaseUrl: string.IsNullOrEmpty(state.LocalRuntimeBaseUrl)
                ? UserConfigRuntimeDefaults.LocalRuntimeBaseUrl
                : state.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: string.IsNullOrEmpty(state.RemoteRuntimeBaseUrl)
                ? UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl
                : state.RemoteRuntimeBaseUrl,
            MaxToolRounds: state.MaxToolRounds);
    }

    public async Task SaveAsync(UserConfig config, CancellationToken cancellationToken = default)
    {
        var scopeState = await EnsureScopeAsync(cancellationToken);
        var actor = await EnsureActorAsync(scopeState.ActorId, cancellationToken);
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
        await SendCommandAsync(actor, evt, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes.Values)
        {
            if (scope.Subscription is not null)
                await scope.Subscription.DisposeAsync();
        }
    }

    private string ResolveScopeId()
    {
        var scope = _scopeResolver.Resolve();
        return scope?.ScopeId ?? "default";
    }

    private async Task<ScopeState> EnsureScopeAsync(CancellationToken ct)
    {
        var scopeId = ResolveScopeId();
        var actorId = ActorIdPrefix + scopeId;

        var scopeState = _scopes.GetOrAdd(actorId, _ => new ScopeState(actorId));

        if (scopeState.Initialized)
            return scopeState;

        await scopeState.InitLock.WaitAsync(ct);
        try
        {
            if (scopeState.Initialized)
                return scopeState;

            scopeState.Subscription = await _subscriptions.SubscribeAsync<EventEnvelope>(
                actorId,
                envelope => HandleConfigEventAsync(actorId, envelope),
                ct);

            await EnsureActorAsync(actorId, ct);
            scopeState.Initialized = true;
        }
        finally
        {
            scopeState.InitLock.Release();
        }

        return scopeState;
    }

    private Task HandleConfigEventAsync(string actorId, EventEnvelope envelope)
    {
        if (envelope.Payload is null)
            return Task.CompletedTask;

        if (envelope.Payload.Is(UserConfigStateSnapshotEvent.Descriptor))
        {
            var snapshot = envelope.Payload.Unpack<UserConfigStateSnapshotEvent>();
            if (_scopes.TryGetValue(actorId, out var scopeState))
            {
                scopeState.Snapshot = snapshot.Snapshot;
                _logger.LogDebug("User config readmodel updated for {ActorId}", actorId);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<IActor> EnsureActorAsync(string actorId, CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(actorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<UserConfigGAgent>(actorId, ct);
    }

    private static async Task SendCommandAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }

    private static UserConfig CreateDefaultConfig() =>
        new(
            DefaultModel: string.Empty,
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl);

    private sealed class ScopeState(string actorId)
    {
        public string ActorId { get; } = actorId;
        public SemaphoreSlim InitLock { get; } = new(1, 1);
        public volatile bool Initialized;
        public volatile UserConfigGAgentState? Snapshot;
        public IAsyncDisposable? Subscription;
    }
}
