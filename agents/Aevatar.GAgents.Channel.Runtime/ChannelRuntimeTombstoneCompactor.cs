using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ChannelRuntimeTombstoneCompactor
{
    // Stable publisher id for envelope routing — keeps compactor traffic
    // distinguishable from end-user / tool dispatches in tracing + log fields.
    private const string PublisherActorId = "channel-runtime.tombstone-compactor";

    private readonly IProjectionScopeWatermarkQueryPort _watermarkQueryPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IEnumerable<ITombstoneCompactionTarget> _targets;
    private readonly ILogger<ChannelRuntimeTombstoneCompactor> _logger;

    public ChannelRuntimeTombstoneCompactor(
        IProjectionScopeWatermarkQueryPort watermarkQueryPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IEnumerable<ITombstoneCompactionTarget> targets,
        ILogger<ChannelRuntimeTombstoneCompactor> logger)
    {
        _watermarkQueryPort = watermarkQueryPort ?? throw new ArgumentNullException(nameof(watermarkQueryPort));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        foreach (var target in _targets)
        {
            try
            {
                await CompactAsync(target, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Tombstone compaction failed for {TargetName}: actorId={ActorId}. Continuing with remaining targets.",
                    target.TargetName,
                    target.ActorId);
            }
        }
    }

    private async Task CompactAsync(ITombstoneCompactionTarget target, CancellationToken ct)
    {
        var safeVersion = await _watermarkQueryPort.GetLastSuccessfulVersionAsync(
            new ProjectionRuntimeScopeKey(target.ActorId, target.ProjectionKind, ProjectionRuntimeMode.DurableMaterialization),
            ct);
        if (!safeVersion.HasValue || safeVersion.Value <= 0)
            return;

        // Lifecycle only — the compactor does not own message delivery.
        await target.EnsureActorAsync(_actorRuntime, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(target.CreateCommand(safeVersion.Value)),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, target.ActorId),
        };

        await _actorDispatchPort.DispatchAsync(target.ActorId, envelope, ct);

        _logger.LogDebug(
            "Dispatched tombstone compaction for {TargetName}: actorId={ActorId} safeStateVersion={SafeStateVersion}",
            target.TargetName,
            target.ActorId,
            safeVersion.Value);
    }
}
