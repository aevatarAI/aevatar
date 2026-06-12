using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Core.Voice;

public sealed class VoicePresenceCapabilityCommandPort : IVoicePresenceCapabilityCommandPort
{
    private const string PublisherActorId = "voice-presence.admin";
    private readonly IActorDispatchPort _dispatchPort;

    public VoicePresenceCapabilityCommandPort(IActorDispatchPort dispatchPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<VoicePresenceCapabilityEnableReceipt> EnableAsync(
        string actorId,
        VoicePresenceEnableRequested request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(request);

        var normalizedActorId = actorId.Trim();
        var normalizedRequest = VoicePresenceEnableRequests.Normalize(request);
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(normalizedRequest),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, normalizedActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };

        var admission = await _dispatchPort.DispatchAsync(normalizedActorId, envelope, ct);
        return new VoicePresenceCapabilityEnableReceipt(
            admission.ActorId,
            normalizedRequest.ModuleName,
            admission.CommandId,
            admission.CorrelationId,
            admission.AckedAt);
    }
}
