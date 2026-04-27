using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ChannelRuntimeTombstoneCompactor
{
    private readonly IProjectionScopeWatermarkQueryPort _watermarkQueryPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly IEnumerable<ITombstoneCompactionTarget> _targets;
    private readonly ILogger<ChannelRuntimeTombstoneCompactor> _logger;

    public ChannelRuntimeTombstoneCompactor(
        IProjectionScopeWatermarkQueryPort watermarkQueryPort,
        IActorRuntime actorRuntime,
        IEnumerable<ITombstoneCompactionTarget> targets,
        ILogger<ChannelRuntimeTombstoneCompactor> logger)
    {
        _watermarkQueryPort = watermarkQueryPort ?? throw new ArgumentNullException(nameof(watermarkQueryPort));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        foreach (var target in _targets)
        {
            await CompactAsync(target, ct);
        }
    }

    private async Task CompactAsync(ITombstoneCompactionTarget target, CancellationToken ct)
    {
        var safeVersion = await _watermarkQueryPort.GetLastSuccessfulVersionAsync(
            new ProjectionRuntimeScopeKey(target.ActorId, target.ProjectionKind, ProjectionRuntimeMode.DurableMaterialization),
            ct);
        if (!safeVersion.HasValue || safeVersion.Value <= 0)
            return;

        var actor = await _actorRuntime.GetAsync(target.ActorId);
        if (actor is null)
            return;

        await actor.HandleEventAsync(
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(target.CreateCommand(safeVersion.Value)),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actor.Id },
                },
            },
            ct);

        _logger.LogDebug(
            "Dispatched tombstone compaction for {TargetName}: actorId={ActorId} safeStateVersion={SafeStateVersion}",
            target.TargetName,
            target.ActorId,
            safeVersion.Value);
    }
}
