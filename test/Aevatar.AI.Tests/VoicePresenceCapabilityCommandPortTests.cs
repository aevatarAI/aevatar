using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Voice;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class VoicePresenceCapabilityCommandPortTests
{
    [Fact]
    public async Task EnableAsync_ShouldDispatchTypedCommandEnvelopeAndReturnAcceptedReceipt()
    {
        var runtime = new StaticActorRuntime(exists: true);
        var dispatch = new RecordingDispatchPort();
        var port = new VoicePresenceCapabilityCommandPort(runtime, dispatch);

        var receipt = await port.EnableAsync(
            " role-agent-1 ",
            new VoicePresenceEnableRequested
            {
                ModuleName = " voice_presence ",
                RemoteAudioSupport = VoiceRemoteAudioSupport.Supported,
                VoiceSessionDefaults = new VoiceSessionDefaults
                {
                    SampleRateHz = 16000,
                },
            });

        receipt.ActorId.Should().Be("role-agent-1");
        receipt.ModuleName.Should().Be("voice_presence");
        receipt.Stage.Should().Be("accepted_for_dispatch");
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CorrelationId.Should().Be(receipt.CommandId);

        dispatch.Dispatches.Should().ContainSingle();
        var (actorId, envelope) = dispatch.Dispatches[0];
        actorId.Should().Be("role-agent-1");
        envelope.Id.Should().Be(receipt.CommandId);
        envelope.Propagation.CorrelationId.Should().Be(receipt.CorrelationId);
        envelope.Route.Direct.TargetActorId.Should().Be("role-agent-1");
        envelope.Route.PublisherActorId.Should().Be("voice-presence.capability");

        envelope.Payload.Is(VoicePresenceEnableRequested.Descriptor).Should().BeTrue();
        var command = envelope.Payload.Unpack<VoicePresenceEnableRequested>();
        command.ModuleName.Should().Be("voice_presence");
        command.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
        command.VoiceSessionDefaults.SampleRateHz.Should().Be(16000);
    }

    [Fact]
    public async Task EnableAsync_ShouldReturnActorNotFoundBeforeDispatch()
    {
        var dispatch = new RecordingDispatchPort();
        var port = new VoicePresenceCapabilityCommandPort(new StaticActorRuntime(exists: false), dispatch);

        var act = () => port.EnableAsync(
            "role-agent-1",
            new VoicePresenceEnableRequested { ModuleName = "voice_presence" });

        var exception = await act.Should().ThrowAsync<VoicePresenceCapabilityCommandException>();
        exception.Which.Error.Should().Be(VoicePresenceCapabilityCommandError.ActorNotFound);
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public void ApplyVoicePresenceEnabled_ShouldPersistDefaultsAndRuntimeState()
    {
        var method = typeof(RoleGAgent)
            .GetMethod("ApplyVoicePresenceEnabled", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ApplyVoicePresenceEnabled not found.");

        var next = InvokePrivateStatic<RoleGAgentState>(
            method,
            new RoleGAgentState(),
            new VoicePresenceEnabledEvent
            {
                ModuleName = " voice_presence ",
                VoiceSessionDefaults = new VoiceSessionDefaults
                {
                    Voice = "alloy",
                    SampleRateHz = 16000,
                },
                RuntimeState = new VoicePresenceRuntimeState
                {
                    Initialized = true,
                    RemoteAudioSupport = VoiceRemoteAudioSupport.Supported,
                    PcmSampleRateHz = 16000,
                },
            });

        next.VoiceSessionDefaults.Should().ContainKey("voice_presence");
        next.VoiceSessionDefaults["voice_presence"].Voice.Should().Be("alloy");
        next.VoicePresence.Should().ContainKey("voice_presence");
        next.VoicePresence["voice_presence"].Initialized.Should().BeTrue();
        next.VoicePresence["voice_presence"].RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
        next.VoicePresence["voice_presence"].PcmSampleRateHz.Should().Be(16000);
    }

    private static T InvokePrivateStatic<T>(MethodInfo method, params object[] args) =>
        (T)(method.Invoke(null, args) ?? throw new InvalidOperationException($"{method.Name} returned null."));

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class StaticActorRuntime(bool exists) : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(exists ? new NoopActor(id) : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(exists);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
