using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.Voice;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Core.Tests.Voice;

public sealed class VoicePresenceCapabilityCommandPortTests
{
    [Fact]
    public async Task EnableAsync_ShouldDispatchTypedVoicePresenceCommandAndReturnAcceptedReceipt()
    {
        var dispatchPort = new RecordingDispatchPort();
        var port = new VoicePresenceCapabilityCommandPort(dispatchPort);

        var receipt = await port.EnableAsync(" role-voice ", new VoicePresenceEnableRequested
        {
            ModuleName = " voice_presence ",
            RemoteAudioSupport = VoiceRemoteAudioSupport.Unspecified,
            SessionDefaults = new VoiceSessionDefaults
            {
                Voice = "verse",
                SampleRateHz = 16000,
            },
        });

        dispatchPort.Calls.Should().ContainSingle();
        var call = dispatchPort.Calls[0];
        call.ActorId.Should().Be("role-voice");
        call.Envelope.Id.Should().NotBeNullOrWhiteSpace();
        call.Envelope.Payload.Is(VoicePresenceEnableRequested.Descriptor).Should().BeTrue();
        call.Envelope.Propagation.CorrelationId.Should().Be(call.Envelope.Id);
        call.Envelope.Timestamp.Should().NotBeNull();
        call.Envelope.Route.PublisherActorId.Should().Be("voice-presence.admin");
        call.Envelope.Route.Direct.Should().NotBeNull();
        call.Envelope.Route.Direct.TargetActorId.Should().Be("role-voice");

        var payload = call.Envelope.Payload.Unpack<VoicePresenceEnableRequested>();
        payload.ModuleName.Should().Be("voice_presence");
        payload.PcmSampleRateHz.Should().Be(VoicePresenceEnableRequests.DefaultPcmSampleRateHz);
        payload.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
        payload.SessionDefaults.Voice.Should().Be("verse");

        receipt.ActorId.Should().Be("role-voice");
        receipt.ModuleName.Should().Be("voice_presence");
        receipt.CommandId.Should().Be(call.Envelope.Id);
        receipt.CorrelationId.Should().Be(call.Envelope.Id);
        receipt.AcceptedAtUtc.Should().BeAfter(DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }
}
