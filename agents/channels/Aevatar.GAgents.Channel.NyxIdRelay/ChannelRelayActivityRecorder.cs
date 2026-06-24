using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Records, best-effort, that a channel bot received a verified inbound via the relay.
/// This is the activation marker that lets the /channels read model report
/// active/pending status for bots whose live status NyxID won't serve cross-account
/// (its channel-bot API is owner-scoped). Maps the relay api-key id → its registration
/// and signals the registration store actor (a fire-and-forget signal, not an RPC).
/// </summary>
public interface IChannelRelayActivityRecorder
{
    Task RecordInboundAsync(string nyxAgentApiKeyId, CancellationToken ct = default);
}

public sealed class ChannelRelayActivityRecorder : IChannelRelayActivityRecorder
{
    private const string PublisherActorId = "channel-runtime.relay-activity";

    private readonly IChannelBotRegistrationQueryByNyxIdentityPort _registrationQuery;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<ChannelRelayActivityRecorder> _logger;

    public ChannelRelayActivityRecorder(
        IChannelBotRegistrationQueryByNyxIdentityPort registrationQuery,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ILogger<ChannelRelayActivityRecorder> logger)
    {
        _registrationQuery = registrationQuery ?? throw new ArgumentNullException(nameof(registrationQuery));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordInboundAsync(string nyxAgentApiKeyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nyxAgentApiKeyId))
            return;

        var registration = await _registrationQuery.GetByNyxAgentApiKeyIdAsync(nyxAgentApiKeyId.Trim(), ct);
        if (registration is null || registration.Tombstoned || string.IsNullOrWhiteSpace(registration.Id))
            return;

        // Already activated → no signal needed. The store actor would no-op anyway, but
        // skipping the dispatch keeps steady-state inbound traffic off the single store actor.
        if (registration.LastInboundAtUtc is not null)
            return;

        var actor = await _actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(ChannelBotRegistrationGAgent.WellKnownId, ct);
        if (actor is null)
            return;

        var observedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = observedAt,
            Payload = Any.Pack(new ChannelBotRecordInboundCommand
            {
                RegistrationId = registration.Id,
                ObservedAtUtc = observedAt,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, ChannelBotRegistrationGAgent.WellKnownId),
        };

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
        _logger.LogInformation("Signaled channel bot activation from relay inbound: registration={RegistrationId}", registration.Id);
    }
}
