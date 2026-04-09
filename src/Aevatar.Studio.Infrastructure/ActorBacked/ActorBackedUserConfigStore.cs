using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Infrastructure.Storage;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
                UserConfigRuntimeDefaults.LocalRuntimeBaseUrl),
            RemoteRuntimeBaseUrl = UserConfigRuntime.NormalizeBaseUrl(
                config.RemoteRuntimeBaseUrl,
                UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl),
            MaxToolRounds = config.MaxToolRounds,
        };
        await SendCommandAsync(actor, evt, cancellationToken);
    }

    // ── Per-request readmodel read (no service-level state) ──

    private async Task<UserConfigGAgentState?> ReadFromReadModelAsync(CancellationToken ct)
    {
        var readModelActorId = ResolveReadModelActorId();
        var tcs = new TaskCompletionSource<UserConfigGAgentState?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await _subscriptions.SubscribeAsync<EventEnvelope>(
            readModelActorId,
            envelope =>
            {
                if (envelope.Payload?.Is(UserConfigStateSnapshotEvent.Descriptor) == true)
                {
                    var snapshot = envelope.Payload.Unpack<UserConfigStateSnapshotEvent>();
                    tcs.TrySetResult(snapshot.Snapshot);
                }
                return Task.CompletedTask;
            },
            ct);

        // Activate readmodel actor (triggers OnActivateAsync → PublishAsync snapshot)
        await EnsureReadModelActorAsync(readModelActorId, ct);

        // Wait for snapshot with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout waiting for readmodel snapshot from {ActorId}", readModelActorId);
            return null;
        }
    }

    // ── Actor resolution ──

    private string ResolveScopeId()
    {
        var scope = _scopeResolver.Resolve();
        return scope?.ScopeId ?? "default";
    }

    private string ResolveWriteActorId() => WriteActorIdPrefix + ResolveScopeId();
    private string ResolveReadModelActorId() => ResolveWriteActorId() + "-readmodel";

    private async Task<IActor> EnsureWriteActorAsync(CancellationToken ct)
    {
        var actorId = ResolveWriteActorId();
        var actor = await _runtime.GetAsync(actorId);
        return actor ?? await _runtime.CreateAsync<UserConfigGAgent>(actorId, ct);
    }

    private async Task EnsureReadModelActorAsync(string readModelActorId, CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(readModelActorId);
        if (actor is null)
            await _runtime.CreateAsync<UserConfigReadModelGAgent>(readModelActorId, ct);
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

    private UserConfig CreateDefaultConfig() =>
        new(
            DefaultModel: string.Empty,
            PreferredLlmRoute: UserConfigLlmRouteDefaults.Gateway,
            RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
            LocalRuntimeBaseUrl: _storageOptions.ResolveDefaultLocalRuntimeBaseUrl(),
            RemoteRuntimeBaseUrl: _storageOptions.ResolveDefaultRemoteRuntimeBaseUrl());
}
