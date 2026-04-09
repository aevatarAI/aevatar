using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IUserConfigStore"/>.
/// Completely stateless: no fields hold snapshot or subscription state.
/// Reads use per-request temporary subscription to the ReadModel GAgent.
/// Writes send commands to the Write GAgent.
/// Per-scope isolation: each scope gets its own <c>user-config-{scopeId}</c> actor.
/// </summary>
internal sealed class ActorBackedUserConfigStore : IUserConfigStore
{
    private const string WriteActorIdPrefix = "user-config-";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly StudioStorageOptions _storageOptions;
    private readonly ILogger<ActorBackedUserConfigStore> _logger;

    public ActorBackedUserConfigStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        IAppScopeResolver scopeResolver,
        IOptions<StudioStorageOptions> storageOptions,
        ILogger<ActorBackedUserConfigStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _storageOptions = storageOptions?.Value ?? new StudioStorageOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadFromReadModelAsync(cancellationToken);
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
                ? _storageOptions.ResolveDefaultLocalRuntimeBaseUrl()
                : state.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl: string.IsNullOrEmpty(state.RemoteRuntimeBaseUrl)
                ? _storageOptions.ResolveDefaultRemoteRuntimeBaseUrl()
                : state.RemoteRuntimeBaseUrl,
            MaxToolRounds: state.MaxToolRounds);
    }

    public async Task SaveAsync(UserConfig config, CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        var evt = new UserConfigUpdatedEvent
        {
            DefaultModel = config.DefaultModel,
            PreferredLlmRoute = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute),
            RuntimeMode = UserConfigRuntime.NormalizeMode(config.RuntimeMode),
            LocalRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.LocalRuntimeBaseUrl,
                _storageOptions.ResolveDefaultLocalRuntimeBaseUrl()),
            RemoteRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.RemoteRuntimeBaseUrl,
                _storageOptions.ResolveDefaultRemoteRuntimeBaseUrl()),
            MaxToolRounds = config.MaxToolRounds,
        };
        await ActorCommandDispatcher.SendAsync(actor, evt, cancellationToken);
    }

    // ── Per-request readmodel read (no service-level state) ──

    private Task<UserConfigGAgentState?> ReadFromReadModelAsync(CancellationToken ct)
    {
        return ReadModelSnapshotReader.ReadAsync<UserConfigGAgentState, UserConfigStateSnapshotEvent>(
            _subscriptions,
            _runtime,
            ResolveReadModelActorId(),
            typeof(UserConfigReadModelGAgent),
            UserConfigStateSnapshotEvent.Descriptor,
            evt => evt.Snapshot,
            _logger,
            ct);
    }

    // ── Actor resolution ──

    private string ResolveWriteActorId() => WriteActorIdPrefix + _scopeResolver.ResolveScopeIdOrDefault();
    private string ResolveReadModelActorId() => ResolveWriteActorId() + "-readmodel";

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<UserConfigGAgent>(actorId, ct);
    }

    private UserConfig CreateDefaultConfig() =>
        new(
            DefaultModel: string.Empty,
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: _storageOptions.ResolveDefaultLocalRuntimeBaseUrl(),
            RemoteRuntimeBaseUrl: _storageOptions.ResolveDefaultRemoteRuntimeBaseUrl());
}
