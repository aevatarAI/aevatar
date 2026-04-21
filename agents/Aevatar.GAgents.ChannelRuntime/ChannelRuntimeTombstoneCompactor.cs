using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelRuntimeTombstoneCompactor
{
    private readonly IProjectionScopeWatermarkQueryPort _watermarkQueryPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly ILogger<ChannelRuntimeTombstoneCompactor> _logger;

    public ChannelRuntimeTombstoneCompactor(
        IProjectionScopeWatermarkQueryPort watermarkQueryPort,
        IActorRuntime actorRuntime,
        ILogger<ChannelRuntimeTombstoneCompactor> logger)
    {
        _watermarkQueryPort = watermarkQueryPort ?? throw new ArgumentNullException(nameof(watermarkQueryPort));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await CompactAsync(
            ChannelBotRegistrationGAgent.WellKnownId,
            ChannelBotRegistrationProjectionPort.ProjectionKind,
            safeVersion => new ChannelBotCompactTombstonesCommand { SafeStateVersion = safeVersion },
            "channel bot registration",
            ct);

        await CompactAsync(
            DeviceRegistrationGAgent.WellKnownId,
            DeviceRegistrationProjectionPort.ProjectionKind,
            safeVersion => new DeviceCompactTombstonesCommand { SafeStateVersion = safeVersion },
            "device registration",
            ct);

        await CompactAsync(
            UserAgentCatalogGAgent.WellKnownId,
            UserAgentCatalogProjectionPort.ProjectionKind,
            safeVersion => new UserAgentCatalogCompactTombstonesCommand { SafeStateVersion = safeVersion },
            "user agent catalog",
            ct);
    }

    private async Task CompactAsync<TCommand>(
        string actorId,
        string projectionKind,
        Func<long, TCommand> commandFactory,
        string targetName,
        CancellationToken ct)
        where TCommand : IMessage
    {
        var safeVersion = await _watermarkQueryPort.GetLastSuccessfulVersionAsync(
            new ProjectionRuntimeScopeKey(actorId, projectionKind, ProjectionRuntimeMode.DurableMaterialization),
            ct);
        if (!safeVersion.HasValue || safeVersion.Value <= 0)
            return;

        var actor = await _actorRuntime.GetAsync(actorId);
        if (actor is null)
            return;

        await actor.HandleEventAsync(
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(commandFactory(safeVersion.Value)),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actor.Id },
                },
            },
            ct);

        _logger.LogDebug(
            "Dispatched tombstone compaction for {TargetName}: actorId={ActorId} safeStateVersion={SafeStateVersion}",
            targetName,
            actorId,
            safeVersion.Value);
    }
}
