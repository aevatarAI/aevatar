using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Projection;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System.Runtime.CompilerServices;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoiceRealtimeSessionTests
{
    [Fact]
    public async Task ExecuteAsync_should_return_not_found_when_capability_readmodel_is_missing()
    {
        var session = CreateSession(new FakeCapabilityQueryPort());

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.NotFound);
    }

    [Fact]
    public async Task ExecuteAsync_should_accept_supported_capability_and_acquire_typed_lease()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence_openai",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var acceptedCallbacks = new List<VoiceRealtimeSessionAccepted>();
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask,
            (accepted, _) =>
            {
                acceptedCallbacks.Add(accepted);
                return ValueTask.CompletedTask;
            });

        result.Succeeded.ShouldBeTrue();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.None);
        result.Completed.ShouldBeTrue();
        result.Completion.ShouldBe(VoiceRealtimeSessionCompletion.Accepted);
        result.Receipt.ShouldNotBeNull();
        result.Receipt.ActorId.ShouldBe("agent-1");
        result.Receipt.ModuleName.ShouldBe("voice_presence_openai");
        result.Receipt.PcmSampleRateHz.ShouldBe(16000);
        result.Receipt.ObservedStateVersion.ShouldBe(5);
        acceptedCallbacks.ShouldHaveSingleItem().SessionId.ShouldBe(result.Receipt.SessionId);
        leasePort.AcquireRequests.ShouldHaveSingleItem().ModuleName.ShouldBe("voice_presence_openai");
    }

    [Theory]
    [InlineData(VoiceRemoteAudioSupport.Unspecified)]
    [InlineData(VoiceRemoteAudioSupport.LocalOnly)]
    public async Task ExecuteAsync_should_return_unsupported_before_lease_when_remote_audio_is_not_supported(
        VoiceRemoteAudioSupport remoteAudioSupport)
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            remoteAudioSupport: remoteAudioSupport);
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.Unsupported);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_should_return_unsupported_before_lease_when_media_port_is_fail_closed()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(
            new FakeCapabilityQueryPort(capability),
            leasePort,
            new RecordingMediaStreamPort(supportsRemoteAudio: false));

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.Unsupported);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_should_return_not_initialized_before_lease_when_capability_is_not_initialized()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: false,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.NotInitialized);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_should_return_transport_attached_for_attach_when_active_transport_exists()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            transportAttached: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.TransportAlreadyAttached);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_should_accept_detach_when_active_transport_exists_without_acquiring_new_lease()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "session-1",
            activeTransportLeaseId: "transport-1",
            transportAttached: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest(
                "agent-1",
                "voice_presence",
                VoiceRealtimeSessionPurpose.Detach),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeTrue();
        result.Receipt.ShouldNotBeNull();
        result.Receipt.SessionId.ShouldBe("session-1");
        result.Receipt.LeaseHandle.ActiveTransportLeaseId.ShouldBe("transport-1");
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_should_treat_live_active_lease_as_attached_before_transport_materializes()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "session-1",
            activeTransportLeaseId: "transport-1",
            transportAttached: false,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort);

        var attachResult = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);
        var detachResult = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest(
                "agent-1",
                "voice_presence",
                VoiceRealtimeSessionPurpose.Detach),
            static (_, _) => ValueTask.CompletedTask);

        attachResult.Succeeded.ShouldBeFalse();
        attachResult.Error.ShouldBe(VoiceRealtimeSessionStartError.TransportAlreadyAttached);
        detachResult.Succeeded.ShouldBeTrue();
        detachResult.Receipt.ShouldNotBeNull();
        detachResult.Receipt.SessionId.ShouldBe("session-1");
        detachResult.Receipt.LeaseHandle.ActiveTransportLeaseId.ShouldBe("transport-1");
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task VoiceVolatileMediaStreamPort_should_cleanup_attachment_when_registration_has_no_media_connector()
    {
        var leasePort = new RecordingLeasePort();
        var attachmentPort = new RecordingAttachmentPort();
        var port = new VoiceVolatileMediaStreamPort(
            attachmentPort,
            leasePort,
            [new VoicePresenceModuleRegistration(["voice_presence"], _ => throw new InvalidOperationException())],
            new ServiceCollection().BuildServiceProvider(),
            new RecordingDispatchPort());
        var handle = CreateLeaseHandle(activeTransportLeaseId: "transport-1");

        port.SupportsRemoteAudio.ShouldBeTrue();
        var ex = await Should.ThrowAsync<VoiceVolatileMediaStreamUnavailableException>(
            () => port.AttachAsync(handle, new PassiveVoiceTransport(), CancellationToken.None));
        ex.Message.ShouldBe(VoiceVolatileMediaStreamUnavailableException.Reason);

        attachmentPort.DetachedHandles.ShouldHaveSingleItem().ShouldBe(handle);
        leasePort.ReleaseRequests.ShouldHaveSingleItem().Handle.ShouldBe(handle);
    }

    [Fact]
    public async Task VoiceVolatileMediaStreamPort_should_publish_lifetime_completed_through_lease_port()
    {
        var leasePort = new RecordingLeasePort();
        var port = new VoiceVolatileMediaStreamPort(
            new RecordingAttachmentPort(),
            leasePort,
            [new VoicePresenceModuleRegistration(["voice_presence"], _ => throw new InvalidOperationException())],
            new ServiceCollection().BuildServiceProvider(),
            new RecordingDispatchPort());
        var handle = CreateLeaseHandle(activeTransportLeaseId: "transport-1");

        await port.CompleteTransportLifetimeAsync(handle, null, "host_transport_completed");

        var completion = leasePort.LifetimeCompletions.ShouldHaveSingleItem();
        completion.Handle.ShouldBe(handle);
        completion.TransportLeaseId.ShouldBe("transport-1");
        completion.Reason.ShouldBe("host_transport_completed");
    }

    [Fact]
    public async Task VoiceVolatileMediaStreamPort_should_prefer_completed_transport_lease_id()
    {
        var leasePort = new RecordingLeasePort();
        var port = new VoiceVolatileMediaStreamPort(
            new RecordingAttachmentPort(),
            leasePort,
            [new VoicePresenceModuleRegistration(["voice_presence"], _ => throw new InvalidOperationException())],
            new ServiceCollection().BuildServiceProvider(),
            new RecordingDispatchPort());
        var handle = CreateLeaseHandle(activeTransportLeaseId: "handle-transport");

        await port.CompleteTransportLifetimeAsync(
            handle,
            new VoiceTransportLifetimeCompleted
            {
                TransportLeaseId = "completed-transport",
            },
            "host_transport_completed");

        var completion = leasePort.LifetimeCompletions.ShouldHaveSingleItem();
        completion.Handle.ShouldBe(handle);
        completion.TransportLeaseId.ShouldBe("completed-transport");
        completion.Reason.ShouldBe("host_transport_completed");
    }

    [Fact]
    public async Task VoiceVolatileMediaStreamPort_should_ignore_lifetime_completion_without_transport_lease_id()
    {
        var leasePort = new RecordingLeasePort();
        var port = new VoiceVolatileMediaStreamPort(
            new RecordingAttachmentPort(),
            leasePort,
            [new VoicePresenceModuleRegistration(["voice_presence"], _ => throw new InvalidOperationException())],
            new ServiceCollection().BuildServiceProvider(),
            new RecordingDispatchPort());
        var handle = CreateLeaseHandle();

        await port.CompleteTransportLifetimeAsync(
            handle,
            new VoiceTransportLifetimeCompleted(),
            "host_transport_completed");

        leasePort.LifetimeCompletions.ShouldBeEmpty();
    }

    [Fact]
    public async Task VoiceVolatileMediaStreamPort_should_relay_raw_audio_and_dispatch_control_and_input_image_events()
    {
        var leasePort = new RecordingLeasePort();
        var attachmentPort = new RecordingAttachmentPort();
        var dispatchPort = new RecordingDispatchPort();
        var providerSession = new RecordingRelayProviderSession();
        var port = new VoiceVolatileMediaStreamPort(
            attachmentPort,
            leasePort,
            [CreateRelayRegistration(providerSession)],
            new ServiceCollection().BuildServiceProvider(),
            dispatchPort);
        var handle = CreateLeaseHandle(activeTransportLeaseId: "transport-1");
        var transport = new ScriptedVoiceTransport(
            VoiceTransportFrame.Audio(new byte[] { 1, 2, 3, 4 }),
            VoiceTransportFrame.ControlFrame(new VoiceControlFrame
            {
                DrainAcknowledged = new VoiceDrainAcknowledged
                {
                    ResponseId = 5,
                    PlayoutSequence = 9,
                },
            }),
            VoiceTransportFrame.InputImageFrame(new VoiceInputImage
            {
                MediaType = "image/png",
                Data = ByteString.CopyFrom([5, 6, 7]),
            }));

        var lifetimeCompleted = await port.AttachAsync(handle, transport, CancellationToken.None);
        await transport.FramesDrained.Task;
        await providerSession.EmitAudioAsync(new byte[] { 9, 8, 7 }, CancellationToken.None);
        await providerSession.EmitEventAsync(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        }, CancellationToken.None);
        await port.DetachAsync(handle, transport, CancellationToken.None);

        lifetimeCompleted.ShouldNotBeNull();
        lifetimeCompleted.TransportLeaseId.ShouldBe("transport-1");
        attachmentPort.AttachedHandles.ShouldHaveSingleItem().ShouldBe(handle);
        providerSession.AudioFrames.ShouldHaveSingleItem().ShouldBe(new byte[] { 1, 2, 3, 4 });
        transport.SentAudio.ShouldHaveSingleItem().ShouldBe(new byte[] { 9, 8, 7 });

        var signals = dispatchPort.Dispatches
            .Select(static dispatch => dispatch.Envelope.Payload.Unpack<VoiceModuleSignal>())
            .ToList();
        signals.Count.ShouldBe(3);
        signals.ShouldContain(static signal =>
            signal.SignalCase == VoiceModuleSignal.SignalOneofCase.TransportControlFrameReceived);
        signals.ShouldContain(signal =>
            signal.SignalCase == VoiceModuleSignal.SignalOneofCase.InputImageReceived &&
            signal.InputImageReceived.TransportLeaseId == "transport-1" &&
            signal.InputImageReceived.InputImage.MediaType == "image/png" &&
            signal.InputImageReceived.InputImage.Data.ToByteArray().SequenceEqual(new byte[] { 5, 6, 7 }));
        signals.ShouldContain(static signal =>
            signal.SignalCase == VoiceModuleSignal.SignalOneofCase.ProviderEventReceived);
    }

    [Fact]
    public async Task VoicePresenceTransportAttachmentPort_should_dispatch_attach_signal_and_return_active_transport_lease_handle()
    {
        var dispatchPort = new RecordingDispatchPort();
        var port = new VoicePresenceTransportAttachmentPort(dispatchPort);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var handle = CreateLeaseHandle(expiresAt);

        var attached = await port.AttachAsync(handle, new PassiveVoiceTransport(), CancellationToken.None);

        attached.ActiveTransportLeaseId.ShouldNotBeNullOrWhiteSpace();
        dispatchPort.Dispatches.ShouldHaveSingleItem().ActorId.ShouldBe("agent-1");
        var signal = dispatchPort.Dispatches[0].Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportAttachRequested);
        signal.TransportAttachRequested.SessionId.ShouldBe("lease-1");
        signal.TransportAttachRequested.OwnerId.ShouldBe("host-1");
        signal.TransportAttachRequested.TransportLeaseId.ShouldBe(attached.ActiveTransportLeaseId);
        signal.TransportAttachRequested.LeaseExpiresAt.ToDateTimeOffset().ShouldBe(expiresAt.ToUniversalTime());
    }

    [Fact]
    public async Task VoicePresenceTransportAttachmentPort_should_dispatch_detach_signal_for_active_transport_lease()
    {
        var dispatchPort = new RecordingDispatchPort();
        var port = new VoicePresenceTransportAttachmentPort(dispatchPort);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var handle = CreateLeaseHandle(expiresAt, activeTransportLeaseId: "transport-1");

        await port.DetachAsync(handle, new PassiveVoiceTransport(), CancellationToken.None);

        dispatchPort.Dispatches.ShouldHaveSingleItem().ActorId.ShouldBe("agent-1");
        var signal = dispatchPort.Dispatches[0].Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportDetachRequested);
        signal.TransportDetachRequested.SessionId.ShouldBe("lease-1");
        signal.TransportDetachRequested.OwnerId.ShouldBe("host-1");
        signal.TransportDetachRequested.TransportLeaseId.ShouldBe("transport-1");
        signal.TransportDetachRequested.Reason.ShouldBe("host_transport_detached");
        signal.TransportDetachRequested.LeaseExpiresAt.ToDateTimeOffset().ShouldBe(expiresAt.ToUniversalTime());
    }

    [Fact]
    public async Task VoicePresenceTransportAttachmentPort_should_not_dispatch_detach_without_active_transport_lease()
    {
        var dispatchPort = new RecordingDispatchPort();
        var port = new VoicePresenceTransportAttachmentPort(dispatchPort);

        await port.DetachAsync(CreateLeaseHandle(), null, CancellationToken.None);

        dispatchPort.Dispatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task VoicePresenceSessionLeasePort_should_dispatch_typed_lease_signal_and_return_accepted_handle()
    {
        var dispatchPort = new RecordingDispatchPort();
        var leasePort = new VoicePresenceSessionLeasePort(dispatchPort);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var handle = await leasePort.AcquireAsync(new VoicePresenceSessionLeaseRequest(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            expiresAt,
            7,
            VoiceRemoteAudioSupport.LocalOnly));

        handle.SessionId.ShouldBe("lease-1");
        handle.ObservedStateVersion.ShouldBe(7);
        handle.ExpiresAtUtc.ShouldBe(expiresAt.ToUniversalTime());
        dispatchPort.Dispatches.ShouldHaveSingleItem().ActorId.ShouldBe("agent-1");
        var signal = dispatchPort.Dispatches[0].Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.SessionLeaseRequested);
        signal.SessionLeaseRequested.SessionId.ShouldBe("lease-1");
    }

    [Fact]
    public async Task VoicePresenceSessionLeasePort_should_dispatch_typed_release_signal()
    {
        var dispatchPort = new RecordingDispatchPort();
        var leasePort = new VoicePresenceSessionLeasePort(dispatchPort);
        var handle = CreateLeaseHandle();

        await leasePort.ReleaseAsync(handle, "test-release");

        var signal = dispatchPort.Dispatches.ShouldHaveSingleItem().Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.SessionLeaseReleased);
        signal.SessionLeaseReleased.SessionId.ShouldBe("lease-1");
        signal.SessionLeaseReleased.Reason.ShouldBe("test-release");
    }

    [Fact]
    public async Task VoicePresenceSessionLeasePort_should_dispatch_typed_lifetime_completed_signal()
    {
        var dispatchPort = new RecordingDispatchPort();
        var leasePort = new VoicePresenceSessionLeasePort(dispatchPort);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var handle = CreateLeaseHandle(expiresAt, activeTransportLeaseId: "transport-1");

        await leasePort.CompleteTransportLifetimeAsync(handle, "transport-1", "test-complete");

        var signal = dispatchPort.Dispatches.ShouldHaveSingleItem().Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportLifetimeCompleted);
        signal.TransportLifetimeCompleted.SessionId.ShouldBe("lease-1");
        signal.TransportLifetimeCompleted.TransportLeaseId.ShouldBe("transport-1");
        signal.TransportLifetimeCompleted.Reason.ShouldBe("test-complete");
        signal.TransportLifetimeCompleted.LeaseExpiresAt.ToDateTimeOffset().ShouldBe(expiresAt.ToUniversalTime());
    }

    [Fact]
    public async Task VoicePresenceCapabilityQueryPort_should_read_actor_scoped_capability_readmodel()
    {
        var readModel = new VoicePresenceCapabilityReadModel
        {
            Id = "agent-1:voice_presence",
            ActorId = "agent-1",
            ModuleName = "voice_presence",
            StateVersion = 7,
            LastEventId = "event-7",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Initialized = true,
            PcmSampleRateHz = 24000,
            RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly,
        };
        var queryPort = new VoicePresenceCapabilityQueryPort(new FakeCapabilityReader(readModel));

        var snapshot = await queryPort.GetAsync("agent-1", null);

        snapshot.ShouldNotBeNull();
        snapshot.ActorId.ShouldBe("agent-1");
        snapshot.ModuleName.ShouldBe("voice_presence");
        snapshot.StateVersion.ShouldBe(7);
        snapshot.Initialized.ShouldBeTrue();
        snapshot.PcmSampleRateHz.ShouldBe(24000);
    }

    [Fact]
    public void VoicePresenceCapabilityReadModelMapper_should_apply_runtime_state_defaults()
    {
        var updatedAt = DateTimeOffset.Now;
        var readModel = VoicePresenceCapabilityReadModelMapper.FromRuntimeState(
            " agent-1 ",
            " voice_presence_openai ",
            new VoicePresenceRuntimeState
            {
                Initialized = true,
                LeaseExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(3)),
                ActiveTransportLeaseId = "transport-1",
            },
            8,
            null!,
            updatedAt);

        readModel.Id.ShouldBe("agent-1:voice_presence_openai");
        readModel.ActorId.ShouldBe("agent-1");
        readModel.ModuleName.ShouldBe("voice_presence_openai");
        readModel.StateVersion.ShouldBe(8);
        readModel.LastEventId.ShouldBeEmpty();
        readModel.UpdatedAt.ToDateTimeOffset().ShouldBe(updatedAt.ToUniversalTime());
        readModel.PcmSampleRateHz.ShouldBe(24000);
        readModel.ActiveSessionId.ShouldBeEmpty();
        readModel.ActiveTransportLeaseId.ShouldBe("transport-1");
        readModel.RemoteAudioSupport.ShouldBe(VoiceRemoteAudioSupport.LocalOnly);
    }

    [Fact]
    public void VoicePresenceCapabilityReadModelMapper_should_apply_snapshot_defaults()
    {
        var snapshot = VoicePresenceCapabilityReadModelMapper.ToSnapshot(new VoicePresenceCapabilityReadModel
        {
            ActorId = "agent-1",
            ModuleName = "voice_presence",
            StateVersion = 3,
            LastEventId = "event-3",
            ActiveSessionId = " ",
            ActiveTransportLeaseId = "transport-1",
        });

        snapshot.UpdatedAt.ShouldBe(DateTimeOffset.MinValue);
        snapshot.PcmSampleRateHz.ShouldBe(24000);
        snapshot.ActiveSessionId.ShouldBeNull();
        snapshot.ActiveTransportLeaseId.ShouldBe("transport-1");
        snapshot.LeaseExpiresAt.ShouldBeNull();
        snapshot.RemoteAudioSupport.ShouldBe(VoiceRemoteAudioSupport.LocalOnly);
    }

    [Fact]
    public async Task VoicePresenceCapabilityReadModelProjector_should_upsert_committed_runtime_state()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var updatedAt = new DateTimeOffset(2026, 5, 23, 4, 0, 52, 288, TimeSpan.Zero);
        var projector = new VoicePresenceCapabilityReadModelProjector(
            dispatcher,
            new FixedProjectionClock(updatedAt));
        var envelope = WrapCommitted(
            new VoicePresenceRuntimeStateChangedEvent
            {
                ModuleName = "voice_presence",
                State = new VoicePresenceRuntimeState
                {
                    Initialized = true,
                    PcmSampleRateHz = 16000,
                    ActiveSessionId = "lease-1",
                    ActiveTransportLeaseId = "transport-1",
                    RemoteAudioSupport = VoiceRemoteAudioSupport.Supported,
                },
            },
            version: 9,
            eventId: "evt-9",
            observedAt: updatedAt);

        await projector.ProjectAsync(
            new VoicePresenceCapabilityMaterializationContext
            {
                RootActorId = "agent-1",
                ProjectionKind = VoicePresenceProjectionKinds.CapabilityMaterialization,
            },
            envelope);

        var document = dispatcher.Upserts.ShouldHaveSingleItem();
        document.Id.ShouldBe("agent-1:voice_presence");
        document.StateVersion.ShouldBe(9);
        document.LastEventId.ShouldBe("evt-9");
        document.UpdatedAt.ToDateTimeOffset().ToUnixTimeMilliseconds()
            .ShouldBe(updatedAt.ToUnixTimeMilliseconds());
        document.Initialized.ShouldBeTrue();
        document.PcmSampleRateHz.ShouldBe(16000);
        document.ActiveSessionId.ShouldBe("lease-1");
        document.ActiveTransportLeaseId.ShouldBe("transport-1");
        document.RemoteAudioSupport.ShouldBe(VoiceRemoteAudioSupport.Supported);
    }

    [Fact]
    public async Task VoicePresenceCapabilityReadModelProjector_should_ignore_unrelated_committed_events()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new VoicePresenceCapabilityReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(
            new VoicePresenceCapabilityMaterializationContext
            {
                RootActorId = "agent-1",
                ProjectionKind = VoicePresenceProjectionKinds.CapabilityMaterialization,
            },
            WrapCommitted(new VoiceResponseStarted()));

        dispatcher.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public void VoicePresenceCommittedStateProjectionActivationPlanProvider_should_plan_capability_materialization()
    {
        var provider = new VoicePresenceCommittedStateProjectionActivationPlanProvider();
        var envelope = WrapCommitted(new VoicePresenceRuntimeStateChangedEvent
        {
            ModuleName = "voice_presence",
            State = new VoicePresenceRuntimeState(),
        });
        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();

        var plan = provider.GetPlans(new CommittedStatePublicationContext
        {
            ActorId = "agent-1",
            ActorType = typeof(object),
            Published = published,
            SourceEnvelope = envelope,
        }).ShouldHaveSingleItem();

        plan.LeaseType.ShouldBe(typeof(VoicePresenceCapabilityMaterializationRuntimeLease));
        plan.StartRequest.RootActorId.ShouldBe("agent-1");
        plan.StartRequest.ProjectionKind.ShouldBe(VoicePresenceProjectionKinds.CapabilityMaterialization);
        plan.StartRequest.Mode.ShouldBe(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void Voice_host_shell_source_should_be_deleted()
    {
        var repoRoot = FindRepoRoot();

        File.Exists(Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Hosting/VoicePresenceSession.cs"))
            .ShouldBeFalse();
        File.Exists(Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Hosting/IVoicePresenceSessionResolver.cs"))
            .ShouldBeFalse();
        File.Exists(Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Hosting/VoicePresenceSessionResolution.cs"))
            .ShouldBeFalse();
        File.Exists(Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Hosting/UnavailableVoicePresenceTransportAttachmentPort.cs"))
            .ShouldBeFalse();
        File.Exists(Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Hosting/FailClosedVoiceVolatileMediaStreamPort.cs"))
            .ShouldBeFalse();
        File.Exists(Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Hosting/NoOpVoicePresenceTransportAttachmentPort.cs"))
            .ShouldBeFalse();
    }

    private static ActorOwnedVoiceRealtimeSession CreateSession(
        IVoicePresenceCapabilityQueryPort queryPort,
        IVoicePresenceSessionLeasePort? leasePort = null,
        IVoiceVolatileMediaStreamPort? mediaPort = null) =>
        new(
            queryPort,
            leasePort ?? new RecordingLeasePort(),
            mediaPort ?? new RecordingMediaStreamPort(supportsRemoteAudio: true));

    private static VoicePresenceSessionLeaseHandle CreateLeaseHandle(
        DateTimeOffset? expiresAt = null,
        string? activeTransportLeaseId = null) =>
        new(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            10,
            expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5),
            VoiceRemoteAudioSupport.LocalOnly,
            activeTransportLeaseId);

    private static VoicePresenceModuleRegistration CreateRelayRegistration(
        RecordingRelayProviderSession providerSession) =>
        new(
            ["voice_presence"],
            _ => throw new InvalidOperationException(),
            (_, handle, eventSink, audioSink, _) =>
            {
                providerSession.Connect(
                    new VoiceProviderSessionKey(
                        handle.SessionId,
                        handle.OwnerId,
                        handle.ActiveTransportLeaseId ?? string.Empty,
                        0,
                        Timestamp.FromDateTimeOffset(handle.ExpiresAtUtc.ToUniversalTime()),
                        handle.ActorId,
                        handle.ModuleName),
                    eventSink,
                    audioSink);
                return Task.FromResult<RealtimeVoiceProviderSession>(providerSession);
            });

    private static VoicePresenceCapabilitySnapshot CreateCapability(
        string actorId,
        string moduleName,
        bool initialized,
        string? activeSessionId = null,
        string? activeTransportLeaseId = null,
        bool transportAttached = false,
        VoiceRemoteAudioSupport remoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly) =>
        new(
            actorId,
            moduleName,
            5,
            "event-5",
            DateTimeOffset.UtcNow,
            initialized,
            transportAttached,
            16000,
            activeSessionId,
            DateTimeOffset.UtcNow.AddMinutes(5),
            remoteAudioSupport,
            activeTransportLeaseId);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FakeCapabilityQueryPort(params VoicePresenceCapabilitySnapshot[] snapshots)
        : IVoicePresenceCapabilityQueryPort
    {
        public Task<VoicePresenceCapabilitySnapshot?> GetAsync(
            string actorId,
            string? moduleName,
            CancellationToken ct = default)
        {
            var resolvedModuleName = string.IsNullOrWhiteSpace(moduleName)
                ? "voice_presence"
                : moduleName.Trim();
            return Task.FromResult(snapshots.FirstOrDefault(snapshot =>
                string.Equals(snapshot.ActorId, actorId, StringComparison.Ordinal) &&
                string.Equals(snapshot.ModuleName, resolvedModuleName, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private sealed class RecordingLeasePort : IVoicePresenceSessionLeasePort
    {
        public List<VoicePresenceSessionLeaseRequest> AcquireRequests { get; } = [];

        public List<(VoicePresenceSessionLeaseHandle Handle, string Reason)> ReleaseRequests { get; } = [];

        public List<(VoicePresenceSessionLeaseHandle Handle, string TransportLeaseId, string Reason)> LifetimeCompletions { get; } = [];

        public Task<VoicePresenceSessionLeaseHandle> AcquireAsync(
            VoicePresenceSessionLeaseRequest request,
            CancellationToken ct = default)
        {
            AcquireRequests.Add(request);
            return Task.FromResult(new VoicePresenceSessionLeaseHandle(
                request.ActorId,
                request.ModuleName,
                request.SessionId,
                request.OwnerId,
                request.ObservedStateVersion,
                request.ExpiresAtUtc,
                request.ObservedRemoteAudioSupport));
        }

        public Task ReleaseAsync(
            VoicePresenceSessionLeaseHandle handle,
            string reason,
            CancellationToken ct = default)
        {
            ReleaseRequests.Add((handle, reason));
            return Task.CompletedTask;
        }

        public Task CompleteTransportLifetimeAsync(
            VoicePresenceSessionLeaseHandle handle,
            string transportLeaseId,
            string reason,
            CancellationToken ct = default)
        {
            LifetimeCompletions.Add((handle, transportLeaseId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAttachmentPort : IVoicePresenceTransportAttachmentPort
    {
        public List<VoicePresenceSessionLeaseHandle> AttachedHandles { get; } = [];

        public List<VoicePresenceSessionLeaseHandle> DetachedHandles { get; } = [];

        public Task<VoicePresenceSessionLeaseHandle> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default)
        {
            AttachedHandles.Add(handle);
            return Task.FromResult(handle);
        }

        public Task DetachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport? expectedTransport,
            CancellationToken ct = default)
        {
            DetachedHandles.Add(handle);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMediaStreamPort(bool supportsRemoteAudio) : IVoiceVolatileMediaStreamPort
    {
        public bool SupportsRemoteAudio { get; } = supportsRemoteAudio;

        public Task<bool> TrySendToolResultAsync(
            string transportLeaseId,
            string callId,
            string resultJson,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default) =>
            Task.FromResult<VoiceTransportLifetimeCompleted?>(null);

        public Task DetachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport? expectedTransport,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CompleteTransportLifetimeAsync(
            VoicePresenceSessionLeaseHandle handle,
            VoiceTransportLifetimeCompleted? completed,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingWriteDispatcher : IProjectionWriteDispatcher<VoicePresenceCapabilityReadModel>
    {
        public List<VoicePresenceCapabilityReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            VoicePresenceCapabilityReadModel readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        long version = 1,
        string eventId = "evt-1",
        DateTimeOffset? observedAt = null)
    {
        var timestamp = Timestamp.FromDateTimeOffset(observedAt ?? DateTimeOffset.UtcNow);
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = timestamp.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("agent-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = timestamp.Clone(),
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(new VoicePresenceRuntimeState()),
            }),
        };
    }

    private sealed class FakeCapabilityReader(VoicePresenceCapabilityReadModel? readModel)
        : IProjectionDocumentReader<VoicePresenceCapabilityReadModel, string>
    {
        public Task<VoicePresenceCapabilityReadModel?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(readModel?.Id == key ? readModel : null);

        public Task<ProjectionDocumentQueryResult<VoicePresenceCapabilityReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<VoicePresenceCapabilityReadModel>.Empty);
    }

    private sealed class PassiveVoiceTransport : IVoiceTransport
    {
        public IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(CancellationToken ct) =>
            AsyncEnumerable.Empty<VoiceTransportFrame>();

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedVoiceTransport(params VoiceTransportFrame[] frames) : IVoiceTransport
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FramesDrained { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> SentAudio { get; } = [];

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentAudio.Add(pcm16.ToArray());
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var frame in frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return frame;
            }

            FramesDrained.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }

        public ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRelayProviderSession : RealtimeVoiceProviderSession
    {
        private VoiceProviderSessionKey _sessionKey = new(string.Empty, string.Empty, string.Empty, 0);
        private Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task>? _eventSink;
        private Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task>? _audioSink;

        public List<byte[]> AudioFrames { get; } = [];

        public void Connect(
            VoiceProviderSessionKey sessionKey,
            Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
            Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink)
        {
            _sessionKey = sessionKey;
            _eventSink = eventSink;
            _audioSink = audioSink;
        }

        public override Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AudioFrames.Add(pcm16.ToArray());
            return Task.CompletedTask;
        }

        public override Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct) =>
            Task.CompletedTask;

        public Task EmitAudioAsync(byte[] pcm16, CancellationToken ct) =>
            _audioSink?.Invoke(
                _sessionKey,
                new VoiceProviderAudioFrame(pcm16, 24000, "response-1"),
                ct) ?? Task.CompletedTask;

        public Task EmitEventAsync(VoiceProviderEvent providerEvent, CancellationToken ct) =>
            _eventSink?.Invoke(_sessionKey, providerEvent, ct) ?? Task.CompletedTask;

        public override Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) =>
            Task.CompletedTask;

        public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) =>
            Task.CompletedTask;

        public override Task CancelResponseAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct) =>
            Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
