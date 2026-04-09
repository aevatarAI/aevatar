using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IGAgentActorStore"/>.
/// Completely stateless: no fields hold snapshot or subscription state.
/// Reads use per-request temporary subscription to the ReadModel GAgent.
/// Writes send commands to the Write GAgent.
/// </summary>
internal sealed class ActorBackedGAgentActorStore : IGAgentActorStore
{
    private const string WriteActorIdPrefix = "gagent-registry-";

    private readonly IActorRuntime _runtime;
    private readonly IActorEventSubscriptionProvider _subscriptions;
    private readonly IAppScopeResolver _scopeResolver;
    private readonly ILogger<ActorBackedGAgentActorStore> _logger;

    public ActorBackedGAgentActorStore(
        IActorRuntime runtime,
        IActorEventSubscriptionProvider subscriptions,
        IAppScopeResolver scopeResolver,
        ILogger<ActorBackedGAgentActorStore> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<GAgentActorGroup>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await ReadFromReadModelAsync(cancellationToken);
        if (state is null)
            return [];

        return state.Groups
            .Select(g => new GAgentActorGroup(
                g.GagentType,
                g.ActorIds.ToList().AsReadOnly()))
            .ToList()
            .AsReadOnly();
    }

    public async Task AddActorAsync(
        string gagentType, string actorId,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        await SendCommandAsync(actor, new ActorRegisteredEvent
        {
            GagentType = gagentType,
            ActorId = actorId,
        }, cancellationToken);
    }

    public async Task RemoveActorAsync(
        string gagentType, string actorId,
        CancellationToken cancellationToken = default)
    {
        var actor = await EnsureWriteActorAsync(cancellationToken);
        await SendCommandAsync(actor, new ActorUnregisteredEvent
        {
            GagentType = gagentType,
            ActorId = actorId,
        }, cancellationToken);
    }

    // ── Per-request readmodel read (no service-level state) ──

    private async Task<GAgentRegistryState?> ReadFromReadModelAsync(CancellationToken ct)
    {
        var readModelActorId = ResolveReadModelActorId();
        var tcs = new TaskCompletionSource<GAgentRegistryState?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await _subscriptions.SubscribeAsync<EventEnvelope>(
            readModelActorId,
            envelope =>
            {
                if (envelope.Payload?.Is(GAgentRegistryStateSnapshotEvent.Descriptor) == true)
                {
                    var snapshot = envelope.Payload.Unpack<GAgentRegistryStateSnapshotEvent>();
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
        return actor ?? await _runtime.CreateAsync<GAgentRegistryGAgent>(actorId, ct);
    }

    private async Task EnsureReadModelActorAsync(string readModelActorId, CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(readModelActorId);
        if (actor is null)
            await _runtime.CreateAsync<GAgentRegistryReadModelGAgent>(readModelActorId, ct);
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
}
