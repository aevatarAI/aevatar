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
    public async Task ExecuteAsync_should_self_heal_missing_capability_then_acquire()
    {
        // Auto-provisioned default agent: the capability event is committed to the grain, but the
        // read-model row starts "missing" (projection lag). The session must self-heal via the recovery
        // port, re-read, and acquire — not 404 — so attach works without a manual voice-presence/enable.
        var healed = CreateCapability(
            "agent-1",
            "voice_presence_openai",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var queryPort = new HealableCapabilityQueryPort();
        var recovery = new RecordingCapabilityRecoveryPort(
            result: true, onHeal: () => queryPort.Heal(healed));
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(queryPort, leasePort, capabilityRecoveryPort: recovery);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask);

        recovery.ActorIds.ShouldHaveSingleItem().ShouldBe("agent-1"); // self-heal fired once for the actor
        queryPort.Lookups.ShouldBeGreaterThanOrEqualTo(2);            // initial null + re-read after heal
        result.Succeeded.ShouldBeTrue();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.None);
        leasePort.AcquireRequests.ShouldHaveSingleItem().ActorId.ShouldBe("agent-1");
    }

    [Fact]
    public async Task ExecuteAsync_should_still_return_not_found_when_recovery_cannot_rematerialize()
    {
        // Genuinely-unprovisioned actor: re-materialize finds no committed events, the row stays null, so
        // the existing NotFound (→ 404) fires unchanged. Proves the self-heal does not mask a real
        // no-capability condition.
        var queryPort = new HealableCapabilityQueryPort();
        var recovery = new RecordingCapabilityRecoveryPort(result: false);
        var session = CreateSession(queryPort, capabilityRecoveryPort: recovery);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask);

        recovery.ActorIds.ShouldHaveSingleItem(); // attempted self-heal once...
        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.NotFound); // ...still fails closed
    }

    [Fact]
    public async Task ExecuteAsync_should_auto_enable_never_enabled_default_agent_then_acquire()
    {
        // Zero-config first connect: the default voice agent was NEVER enabled, so the capability row is
        // genuinely missing and the re-projection self-heal finds nothing to re-materialize (recovery →
        // false). The session must AUTO-PROVISION it — commit an enable via the auto-enable port — then
        // re-read and acquire, so /ws/voice "just works" on first connect without a manual
        // voice-presence/enable. This is the requirement the projection self-heal alone cannot satisfy.
        var enabled = CreateCapability(
            "nyxid-chat-caller-1",
            "voice_presence_openai",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var queryPort = new HealableCapabilityQueryPort();
        var recovery = new RecordingCapabilityRecoveryPort(result: false); // nothing committed to re-project
        var autoEnable = new RecordingCapabilityAutoEnablePort(
            result: true, onEnable: () => queryPort.Heal(enabled));
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(
            queryPort,
            leasePort,
            capabilityRecoveryPort: recovery,
            capabilityAutoEnablePort: autoEnable);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("nyxid-chat-caller-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask);

        recovery.ActorIds.ShouldHaveSingleItem(); // re-projection self-heal tried first...
        var enable = autoEnable.Enables.ShouldHaveSingleItem(); // ...then auto-enable provisioned the agent
        enable.ActorId.ShouldBe("nyxid-chat-caller-1");
        enable.ModuleName.ShouldBe("voice_presence_openai");
        result.Succeeded.ShouldBeTrue();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.None);
        leasePort.AcquireRequests.ShouldHaveSingleItem().ActorId.ShouldBe("nyxid-chat-caller-1");
    }

    [Fact]
    public async Task ExecuteAsync_should_return_capability_not_ready_when_auto_enable_commits_but_does_not_materialize()
    {
        // Auto-enable committed the enable, but the capability read model has not caught up within the
        // bounded re-read window (enable is read-model-asynchronous). That is a retryable materialization
        // lag, not a genuinely missing capability — surface the typed retryable 503 (CapabilityNotReady) so
        // the client re-attaches, NOT a permanent 404.
        var queryPort = new HealableCapabilityQueryPort(); // stays null even after the enable
        var recovery = new RecordingCapabilityRecoveryPort(result: false);
        var autoEnable = new RecordingCapabilityAutoEnablePort(result: true);
        var leasePort = new RecordingLeasePort();
        var session = CreateSession(
            queryPort,
            leasePort,
            capabilityRecoveryPort: recovery,
            capabilityAutoEnablePort: autoEnable);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("nyxid-chat-caller-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask);

        autoEnable.Enables.ShouldHaveSingleItem();
        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.CapabilityNotReady);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_should_still_return_not_found_when_auto_enable_cannot_provision()
    {
        // The actor genuinely cannot be provisioned (unknown/non-existent actor → auto-enable no-ops and
        // returns false). The existing NotFound (→ 404) must still fire; auto-enable must not mask a real
        // no-actor condition.
        var queryPort = new HealableCapabilityQueryPort();
        var recovery = new RecordingCapabilityRecoveryPort(result: false);
        var autoEnable = new RecordingCapabilityAutoEnablePort(result: false);
        var session = CreateSession(
            queryPort,
            capabilityRecoveryPort: recovery,
            capabilityAutoEnablePort: autoEnable);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("nyxid-chat-caller-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask);

        autoEnable.Enables.ShouldHaveSingleItem(); // attempted to provision once...
        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.NotFound); // ...still fails closed
    }

    [Fact]
    public async Task ExecuteAsync_should_not_invoke_auto_enable_when_capability_is_fresh()
    {
        // Happy path: a fresh projection returns a snapshot on the first read, so neither the recovery port
        // nor the auto-enable port is touched — zero added latency / no spurious enable on the common case.
        var capability = CreateCapability(
            "nyxid-chat-caller-1",
            "voice_presence_openai",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var autoEnable = new RecordingCapabilityAutoEnablePort(result: true);
        var session = CreateSession(
            new FakeCapabilityQueryPort(capability),
            capabilityAutoEnablePort: autoEnable);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("nyxid-chat-caller-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask);

        autoEnable.Enables.ShouldBeEmpty(); // never invoked on the fresh path
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_should_not_invoke_recovery_when_capability_is_fresh()
    {
        // Happy path: a fresh projection returns a snapshot on the first read, so the recovery port is
        // never touched — zero added latency / behavior on the common case.
        var capability = CreateCapability(
            "agent-1",
            "voice_presence_openai",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var recovery = new RecordingCapabilityRecoveryPort(result: true);
        var session = CreateSession(new FakeCapabilityQueryPort(capability), capabilityRecoveryPort: recovery);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask);

        recovery.ActorIds.ShouldBeEmpty(); // recovery never invoked on the fresh path
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_should_return_capability_not_ready_when_lease_observation_times_out()
    {
        // The grain accepted the lease, but its capability read-model projection never reflected the
        // committed lease within the observation budget, so AcquireAsync throws TimeoutException. The
        // session must surface a typed, retryable CapabilityNotReady (→ 503 voice_capability_not_ready)
        // instead of letting the timeout bubble out of the ingress as an opaque empty-body 500.
        var capability = CreateCapability(
            "agent-1",
            "voice_presence_openai",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new TimeoutLeasePort();
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1", "voice_presence_openai"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(VoiceRealtimeSessionStartError.CapabilityNotReady);
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

        var toolContext = CreateToolContext("voice-tool:lease-ref-1");

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest(
                "agent-1",
                "voice_presence_openai",
                ToolContext: toolContext),
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
        result.Receipt.LeaseHandle.LeaseEpoch.ShouldBe(7);
        result.Receipt.AttachOutcome.ShouldBe(VoiceRealtimeAttachOutcome.NewSession);
        acceptedCallbacks.ShouldHaveSingleItem().SessionId.ShouldBe(result.Receipt.SessionId);
        var acquireRequest = leasePort.AcquireRequests.ShouldHaveSingleItem();
        acquireRequest.ModuleName.ShouldBe("voice_presence_openai");
        acquireRequest.ToolContext.ShouldNotBeSameAs(toolContext);
        acquireRequest.ToolContext!.CredentialRef.ShouldBe("voice-tool:lease-ref-1");
        result.Receipt.LeaseHandle.ToolContext.ShouldNotBeSameAs(toolContext);
        result.Receipt.LeaseHandle.ToolContext!.CredentialRef.ShouldBe("voice-tool:lease-ref-1");
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
    public async Task ExecuteAsync_attach_should_acquire_when_projection_shows_attached_with_no_active_session()
    {
        // Degenerate/corrupt projection: TransportAttached=true but no ActiveSessionId. There is no owner
        // to evict, so the takeover must NOT fail-close — it falls through and lets the grain arbitrate
        // the new lease. A stale projection flag must never permanently block Start (the 409 bug).
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            transportAttached: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var mediaPort = new RecordingMediaStreamPort(supportsRemoteAudio: true);
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort, mediaPort);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeTrue();
        leasePort.AcquireRequests.ShouldHaveSingleItem();
        mediaPort.DetachedHandles.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_attach_should_take_over_existing_session_even_when_projection_never_clears()
    {
        // A prior session left the transport attached (close raced the lease release, or the lease went
        // stale). The capability projection is STALE — it still shows attached on every read and never
        // clears (the Garnet write-skip / version-gap case). A fresh attach must EVICT the old session
        // and acquire a new lease; it must NOT re-read the projection and fail-close on the stale value
        // (the Start-immediately-closes / persistent-409 bug). The grain — not the projection — is the
        // authority for the new lease.
        var attached = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "old-session",
            activeTransportLeaseId: "old-transport",
            transportAttached: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var mediaPort = new RecordingMediaStreamPort(supportsRemoteAudio: true);
        var session = CreateSession(
            new FakeCapabilityQueryPort(attached),
            leasePort,
            mediaPort);

        var result = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);

        result.Succeeded.ShouldBeTrue();
        mediaPort.DetachedHandles.ShouldHaveSingleItem().SessionId.ShouldBe("old-session");
        leasePort.AcquireRequests.ShouldHaveSingleItem();
        result.Receipt.ShouldNotBeNull();
        result.Receipt.AttachOutcome.ShouldBe(VoiceRealtimeAttachOutcome.Restarted);
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
        result.Receipt.LeaseHandle.LeaseEpoch.ShouldBe(7);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_should_take_over_live_active_lease_before_transport_materializes()
    {
        // A live lease that has not yet materialized a transport still counts as "attached" for the
        // gate, so a fresh attach must take it over (evict + acquire), not fail-close. A subsequent
        // Detach for that session is still accepted without acquiring a new lease.
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "session-1",
            activeTransportLeaseId: "transport-1",
            transportAttached: false,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var mediaPort = new RecordingMediaStreamPort(supportsRemoteAudio: true);
        var session = CreateSession(new FakeCapabilityQueryPort(capability), leasePort, mediaPort);

        var attachResult = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest("agent-1"),
            static (_, _) => ValueTask.CompletedTask);
        var detachResult = await session.ExecuteAsync(
            new VoiceRealtimeSessionRequest(
                "agent-1",
                "voice_presence",
                VoiceRealtimeSessionPurpose.Detach),
            static (_, _) => ValueTask.CompletedTask);

        attachResult.Succeeded.ShouldBeTrue();
        mediaPort.DetachedHandles.ShouldHaveSingleItem().SessionId.ShouldBe("session-1");
        leasePort.AcquireRequests.ShouldHaveSingleItem();
        attachResult.Receipt.ShouldNotBeNull();
        attachResult.Receipt.AttachOutcome.ShouldBe(VoiceRealtimeAttachOutcome.Restarted);
        detachResult.Succeeded.ShouldBeTrue();
        detachResult.Receipt.ShouldNotBeNull();
        detachResult.Receipt.SessionId.ShouldBe("session-1");
        detachResult.Receipt.LeaseHandle.ActiveTransportLeaseId.ShouldBe("transport-1");
        detachResult.Receipt.LeaseHandle.LeaseEpoch.ShouldBe(7);
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
        (await port.TryCancelResponseAsync("transport-1", CancellationToken.None)).ShouldBeTrue();
        (await port.TrySendInputImageAsync(
            "transport-1",
            new VoiceInputImage
            {
                MediaType = "image/jpeg",
                Data = ByteString.CopyFrom([8, 9]),
            },
            CancellationToken.None)).ShouldBeTrue();
        (await port.TrySendToolResultAsync(
            "transport-1",
            "call-1",
            """{"ok":true}""",
            CancellationToken.None)).ShouldBeTrue();
        (await port.TryInjectEventAsync(
            "transport-1",
            new VoiceConversationEventInjection
            {
                EnvelopeId = "external-1",
                PublisherActorId = "external-agent",
                EventType = StringValue.Descriptor.FullName,
                PayloadJson = "\"door\"",
            },
            CancellationToken.None)).ShouldBeTrue();
        (await port.TryCancelResponseAsync("missing-transport", CancellationToken.None)).ShouldBeFalse();
        await port.DetachAsync(handle, transport, CancellationToken.None);

        lifetimeCompleted.ShouldNotBeNull();
        lifetimeCompleted.TransportLeaseId.ShouldBe("transport-1");
        attachmentPort.AttachedHandles.ShouldHaveSingleItem().ShouldBe(handle);
        providerSession.AudioFrames.ShouldHaveSingleItem().ShouldBe(new byte[] { 1, 2, 3, 4 });
        providerSession.CancelCalls.ShouldBe(1);
        providerSession.InputImages.ShouldHaveSingleItem().Data.ToByteArray().ShouldBe([8, 9]);
        providerSession.ToolResults.ShouldHaveSingleItem().ShouldBe(("call-1", """{"ok":true}"""));
        providerSession.InjectedEvents.ShouldHaveSingleItem().EnvelopeId.ShouldBe("external-1");
        transport.SentAudio.ShouldHaveSingleItem().ShouldBe(new byte[] { 9, 8, 7 });

        var signals = dispatchPort.Dispatches
            .Select(static dispatch => dispatch.Envelope.Payload.Unpack<VoiceModuleSignal>())
            .ToList();
        signals.Count.ShouldBe(3);
        signals.ShouldContain(static signal =>
            signal.SignalCase == VoiceModuleSignal.SignalOneofCase.TransportControlFrameReceived &&
            signal.TransportControlFrameReceived.LeaseEpoch == 7);
        signals.ShouldContain(signal =>
            signal.SignalCase == VoiceModuleSignal.SignalOneofCase.InputImageReceived &&
            signal.InputImageReceived.TransportLeaseId == "transport-1" &&
            signal.InputImageReceived.LeaseEpoch == 7 &&
            signal.InputImageReceived.InputImage.MediaType == "image/png" &&
            signal.InputImageReceived.InputImage.Data.ToByteArray().SequenceEqual(new byte[] { 5, 6, 7 }));
        signals.ShouldContain(static signal =>
            signal.SignalCase == VoiceModuleSignal.SignalOneofCase.ProviderEventReceived &&
            signal.ProviderEventReceived.LeaseEpoch == 7);
    }

    [Fact]
    public async Task VoiceVolatileMediaStreamPort_should_dispatch_lease_renewal_while_relay_is_attached()
    {
        var now = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = new ControllableTimeProvider(now);
        var leasePort = new RecordingLeasePort();
        var attachmentPort = new RecordingAttachmentPort();
        var dispatchPort = new RecordingDispatchPort();
        var providerSession = new RecordingRelayProviderSession();
        var port = new VoiceVolatileMediaStreamPort(
            attachmentPort,
            leasePort,
            [CreateRelayRegistration(providerSession)],
            new ServiceCollection().BuildServiceProvider(),
            dispatchPort,
            timeProvider: timeProvider);
        var handle = CreateLeaseHandle(now.AddMinutes(5), activeTransportLeaseId: "transport-1");
        var transport = new PassiveVoiceTransport();

        await port.AttachAsync(handle, transport, CancellationToken.None);
        timeProvider.Advance(ActorOwnedVoiceRealtimeSession.DefaultLeaseTtl / 2);

        var signal = await dispatchPort.TakeSignalAsync<VoiceTransportLeaseRenewRequested>();

        signal.SessionId.ShouldBe("lease-1");
        signal.OwnerId.ShouldBe("host-1");
        signal.TransportLeaseId.ShouldBe("transport-1");
        signal.LeaseEpoch.ShouldBe(7);
        signal.RenewExpiresAt.ToDateTimeOffset().ShouldBe(now.AddMinutes(7.5));

        await port.DetachAsync(handle, transport, CancellationToken.None);
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("completed")]
    public async Task VoiceVolatileMediaStreamPort_should_stop_lease_renewal_after_relay_is_removed(
        string removalPath)
    {
        var now = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = new ControllableTimeProvider(now);
        var leasePort = new RecordingLeasePort();
        var attachmentPort = new RecordingAttachmentPort();
        var dispatchPort = new RecordingDispatchPort();
        var providerSession = new RecordingRelayProviderSession();
        var port = new VoiceVolatileMediaStreamPort(
            attachmentPort,
            leasePort,
            [CreateRelayRegistration(providerSession)],
            new ServiceCollection().BuildServiceProvider(),
            dispatchPort,
            timeProvider: timeProvider);
        var handle = CreateLeaseHandle(now.AddMinutes(5), activeTransportLeaseId: "transport-1");
        var transport = new PassiveVoiceTransport();

        await port.AttachAsync(handle, transport, CancellationToken.None);
        if (removalPath == "detached")
        {
            await port.DetachAsync(handle, transport, CancellationToken.None);
        }
        else
        {
            await port.CompleteTransportLifetimeAsync(handle, null, "host_transport_completed");
        }

        timeProvider.Advance(ActorOwnedVoiceRealtimeSession.DefaultLeaseTtl / 2);

        dispatchPort.Dispatches
            .Select(static dispatch => dispatch.Envelope.Payload.Unpack<VoiceModuleSignal>())
            .ShouldNotContain(static signal =>
                signal.SignalCase == VoiceModuleSignal.SignalOneofCase.TransportLeaseRenewRequested);
    }

    [Fact]
    public async Task VoiceVolatileMediaStreamPort_should_bind_tool_credential_to_transport_lease_and_evict_on_detach()
    {
        var leasePort = new RecordingLeasePort();
        var attachmentPort = new RecordingAttachmentPort();
        var dispatchPort = new RecordingDispatchPort();
        var providerSession = new RecordingRelayProviderSession();
        var credentialPort = new VoiceVolatileToolCredentialPort();
        var issued = await credentialPort.IssueAsync(new VoiceToolCredentialIssueRequest(
            " caller-token ",
            DateTimeOffset.UtcNow.AddMinutes(5)));
        var port = new VoiceVolatileMediaStreamPort(
            attachmentPort,
            leasePort,
            [CreateRelayRegistration(providerSession)],
            new ServiceCollection().BuildServiceProvider(),
            dispatchPort,
            credentialPort);
        var handle = CreateLeaseHandle(activeTransportLeaseId: "transport-1") with
        {
            ToolContext = CreateToolContext(issued!.CredentialRef),
        };
        var transport = new PassiveVoiceTransport();

        (await ((Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider)credentialPort)
            .ResolveAsync(issued.CredentialRef)).ShouldBeNull();

        await port.AttachAsync(handle, transport, issued.TransportBinding, CancellationToken.None);

        (await ((Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider)credentialPort)
            .ResolveAsync(issued.CredentialRef)).ShouldBe("caller-token");

        await port.DetachAsync(handle, transport, CancellationToken.None);

        (await ((Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider)credentialPort)
            .ResolveAsync(issued.CredentialRef)).ShouldBeNull();
    }

    [Theory]
    [InlineData("missing-binding")]
    [InlineData("mismatched-binding")]
    [InlineData("bind-failed")]
    public async Task VoiceVolatileMediaStreamPort_should_fail_closed_when_tool_credential_binding_is_unavailable(
        string failureCase)
    {
        var leasePort = new RecordingLeasePort();
        var attachmentPort = new RecordingAttachmentPort();
        var dispatchPort = new RecordingDispatchPort();
        var providerSession = new RecordingRelayProviderSession();
        var credentialPort = new RecordingToolCredentialPort(bindResult: failureCase != "bind-failed");
        var port = new VoiceVolatileMediaStreamPort(
            attachmentPort,
            leasePort,
            [CreateRelayRegistration(providerSession)],
            new ServiceCollection().BuildServiceProvider(),
            dispatchPort,
            credentialPort);
        var handle = CreateLeaseHandle(activeTransportLeaseId: "transport-1") with
        {
            ToolContext = CreateToolContext("voice-tool:expected"),
        };
        var binding = failureCase switch
        {
            "missing-binding" => null,
            "mismatched-binding" => new VoiceToolCredentialTransportBinding(
                "voice-tool:other",
                "caller-token",
                DateTimeOffset.UtcNow.AddMinutes(5)),
            "bind-failed" => new VoiceToolCredentialTransportBinding(
                "voice-tool:expected",
                "caller-token",
                DateTimeOffset.UtcNow.AddMinutes(5)),
            _ => throw new ArgumentOutOfRangeException(nameof(failureCase), failureCase, null),
        };

        var ex = await Should.ThrowAsync<VoiceVolatileToolCredentialUnavailableException>(
            () => port.AttachAsync(handle, new PassiveVoiceTransport(), binding, CancellationToken.None));

        ex.Message.ShouldBe(VoiceVolatileToolCredentialUnavailableException.Reason);
        attachmentPort.AttachedHandles.ShouldHaveSingleItem().ActiveTransportLeaseId.ShouldBe("transport-1");
        attachmentPort.DetachedHandles.ShouldHaveSingleItem().ActiveTransportLeaseId.ShouldBe("transport-1");
        leasePort.ReleaseRequests.ShouldHaveSingleItem().Handle.ActiveTransportLeaseId.ShouldBe("transport-1");
        providerSession.AudioFrames.ShouldBeEmpty();
        dispatchPort.Dispatches.ShouldBeEmpty();
        credentialPort.BindRequests.Count.ShouldBe(failureCase == "bind-failed" ? 1 : 0);
        credentialPort.ReleaseTransportLeaseIds.ShouldHaveSingleItem().ShouldBe("transport-1");
    }

    [Fact]
    public async Task VoicePresenceTransportAttachmentPort_should_dispatch_attach_signal_and_return_active_transport_lease_handle()
    {
        var dispatchPort = new RecordingDispatchPort();
        var observationPort = new RecordingLeaseObservationPort();
        var port = new VoicePresenceTransportAttachmentPort(dispatchPort, observationPort);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var handle = CreateLeaseHandle(expiresAt);

        var attached = await port.AttachAsync(handle, new PassiveVoiceTransport(), CancellationToken.None);

        attached.ActiveTransportLeaseId.ShouldNotBeNullOrWhiteSpace();
        attached.LeaseEpoch.ShouldBe(7);
        attached.ObservedStateVersion.ShouldBe(11);
        observationPort.AttachRequests.ShouldHaveSingleItem().TransportLeaseId.ShouldBe(attached.ActiveTransportLeaseId);
        dispatchPort.Dispatches.ShouldHaveSingleItem().ActorId.ShouldBe("agent-1");
        var signal = dispatchPort.Dispatches[0].Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportAttachRequested);
        signal.TransportAttachRequested.SessionId.ShouldBe("lease-1");
        signal.TransportAttachRequested.OwnerId.ShouldBe("host-1");
        signal.TransportAttachRequested.TransportLeaseId.ShouldBe(attached.ActiveTransportLeaseId);
        signal.TransportAttachRequested.LeaseExpiresAt.ToDateTimeOffset().ShouldBe(expiresAt.ToUniversalTime());
        signal.TransportAttachRequested.LeaseEpoch.ShouldBe(7);
    }

    [Fact]
    public async Task VoicePresenceTransportAttachmentPort_should_dispatch_detach_signal_for_active_transport_lease()
    {
        var dispatchPort = new RecordingDispatchPort();
        var port = new VoicePresenceTransportAttachmentPort(dispatchPort, new RecordingLeaseObservationPort());
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
        signal.TransportDetachRequested.LeaseEpoch.ShouldBe(7);
    }

    [Fact]
    public async Task VoicePresenceTransportAttachmentPort_should_not_dispatch_detach_without_active_transport_lease()
    {
        var dispatchPort = new RecordingDispatchPort();
        var port = new VoicePresenceTransportAttachmentPort(dispatchPort, new RecordingLeaseObservationPort());

        await port.DetachAsync(CreateLeaseHandle(), null, CancellationToken.None);

        dispatchPort.Dispatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task VoicePresenceSessionLeasePort_should_dispatch_typed_lease_signal_and_return_accepted_handle()
    {
        var dispatchPort = new RecordingDispatchPort();
        var observationPort = new RecordingLeaseObservationPort();
        var leasePort = new VoicePresenceSessionLeasePort(dispatchPort, observationPort);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var toolContext = CreateToolContext("voice-tool:dispatch-ref-1");

        var handle = await leasePort.AcquireAsync(new VoicePresenceSessionLeaseRequest(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            expiresAt,
            7,
            VoiceRemoteAudioSupport.LocalOnly,
            ToolContext: toolContext));

        handle.SessionId.ShouldBe("lease-1");
        handle.ObservedStateVersion.ShouldBe(8);
        handle.ExpiresAtUtc.ShouldBe(expiresAt.ToUniversalTime());
        handle.LeaseEpoch.ShouldBe(7);
        handle.ToolContext.ShouldNotBeSameAs(toolContext);
        handle.ToolContext!.CredentialRef.ShouldBe("voice-tool:dispatch-ref-1");
        observationPort.SessionLeaseRequests.ShouldHaveSingleItem().ObservedStateVersion.ShouldBe(7);
        dispatchPort.Dispatches.ShouldHaveSingleItem().ActorId.ShouldBe("agent-1");
        var signal = dispatchPort.Dispatches[0].Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.SessionLeaseRequested);
        signal.SessionLeaseRequested.SessionId.ShouldBe("lease-1");
        signal.SessionLeaseRequested.ToolContext.ShouldNotBeSameAs(toolContext);
        signal.SessionLeaseRequested.ToolContext.CredentialRef.ShouldBe("voice-tool:dispatch-ref-1");
    }

    [Fact]
    public async Task VoicePresenceSessionLeasePort_should_dispatch_typed_release_signal()
    {
        var dispatchPort = new RecordingDispatchPort();
        var leasePort = new VoicePresenceSessionLeasePort(dispatchPort, new RecordingLeaseObservationPort());
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
        var leasePort = new VoicePresenceSessionLeasePort(dispatchPort, new RecordingLeaseObservationPort());
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var handle = CreateLeaseHandle(expiresAt, activeTransportLeaseId: "transport-1");

        await leasePort.CompleteTransportLifetimeAsync(handle, "transport-1", "test-complete");

        var signal = dispatchPort.Dispatches.ShouldHaveSingleItem().Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportLifetimeCompleted);
        signal.TransportLifetimeCompleted.SessionId.ShouldBe("lease-1");
        signal.TransportLifetimeCompleted.TransportLeaseId.ShouldBe("transport-1");
        signal.TransportLifetimeCompleted.Reason.ShouldBe("test-complete");
        signal.TransportLifetimeCompleted.LeaseExpiresAt.ToDateTimeOffset().ShouldBe(expiresAt.ToUniversalTime());
        signal.TransportLifetimeCompleted.LeaseEpoch.ShouldBe(7);
    }

    [Fact]
    public async Task VoicePresenceLeaseObservationPort_should_observe_positive_session_epoch()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var snapshot = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "lease-1",
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported) with
        {
            StateVersion = 8,
            LeaseExpiresAt = expiresAt,
            LeaseEpoch = 7,
            ActiveLeaseOwnerId = "host-1",
        };
        var port = new VoicePresenceLeaseObservationPort(new FakeCapabilityQueryPort(snapshot));

        var observed = await port.ObserveSessionLeaseAsync(new VoicePresenceSessionLeaseRequest(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            expiresAt,
            7,
            VoiceRemoteAudioSupport.LocalOnly));

        observed.LeaseEpoch.ShouldBe(7);
        observed.StateVersion.ShouldBe(8);
    }

    [Fact]
    public async Task VoicePresenceLeaseObservationPort_should_fail_closed_when_session_epoch_is_not_positive()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var snapshot = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "lease-1",
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported) with
        {
            StateVersion = 8,
            LeaseExpiresAt = expiresAt,
            LeaseEpoch = 0,
            ActiveLeaseOwnerId = "host-1",
        };
        var port = new VoicePresenceLeaseObservationPort(
            new FakeCapabilityQueryPort(snapshot),
            TimeProvider.System,
            TimeSpan.Zero,
            TimeSpan.Zero);

        await Should.ThrowAsync<TimeoutException>(() => port.ObserveSessionLeaseAsync(new VoicePresenceSessionLeaseRequest(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            expiresAt,
            7,
            VoiceRemoteAudioSupport.LocalOnly)));
    }

    [Fact]
    public async Task VoicePresenceLeaseObservationPort_should_observe_attach_with_exact_epoch()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var snapshot = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "lease-1",
            activeTransportLeaseId: "transport-1",
            transportAttached: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported) with
        {
            StateVersion = 11,
            LeaseExpiresAt = expiresAt,
            LeaseEpoch = 7,
            ActiveLeaseOwnerId = "host-1",
        };
        var port = new VoicePresenceLeaseObservationPort(new FakeCapabilityQueryPort(snapshot));

        var observed = await port.ObserveTransportAttachAsync(
            CreateLeaseHandle(expiresAt),
            "transport-1");

        observed.LeaseEpoch.ShouldBe(7);
        observed.ActiveTransportLeaseId.ShouldBe("transport-1");
    }

    [Fact]
    public async Task VoicePresenceLeaseObservationPort_should_fail_closed_when_attach_epoch_is_stale()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var snapshot = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "lease-1",
            activeTransportLeaseId: "transport-1",
            transportAttached: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported) with
        {
            StateVersion = 11,
            LeaseExpiresAt = expiresAt,
            LeaseEpoch = 8,
            ActiveLeaseOwnerId = "host-1",
        };
        var port = new VoicePresenceLeaseObservationPort(
            new FakeCapabilityQueryPort(snapshot),
            TimeProvider.System,
            TimeSpan.Zero,
            TimeSpan.Zero);

        await Should.ThrowAsync<TimeoutException>(() => port.ObserveTransportAttachAsync(
            CreateLeaseHandle(expiresAt),
            "transport-1"));
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
            LeaseEpoch = 7,
            ActiveLeaseOwnerId = "host-1",
        };
        var queryPort = new VoicePresenceCapabilityQueryPort(new FakeCapabilityReader(readModel));

        var snapshot = await queryPort.GetAsync("agent-1", null);

        snapshot.ShouldNotBeNull();
        snapshot.ActorId.ShouldBe("agent-1");
        snapshot.ModuleName.ShouldBe("voice_presence");
        snapshot.StateVersion.ShouldBe(7);
        snapshot.Initialized.ShouldBeTrue();
        snapshot.PcmSampleRateHz.ShouldBe(24000);
        snapshot.LeaseEpoch.ShouldBe(7);
        snapshot.ActiveLeaseOwnerId.ShouldBe("host-1");
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
                ActiveLeaseOwnerId = "host-1",
                LeaseEpoch = 7,
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
        readModel.ActiveLeaseOwnerId.ShouldBe("host-1");
        readModel.LeaseEpoch.ShouldBe(7);
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
            ActiveLeaseOwnerId = "host-1",
            LeaseEpoch = 7,
        });

        snapshot.UpdatedAt.ShouldBe(DateTimeOffset.MinValue);
        snapshot.PcmSampleRateHz.ShouldBe(24000);
        snapshot.ActiveSessionId.ShouldBeNull();
        snapshot.ActiveTransportLeaseId.ShouldBe("transport-1");
        snapshot.ActiveLeaseOwnerId.ShouldBe("host-1");
        snapshot.LeaseEpoch.ShouldBe(7);
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
                    ActiveLeaseOwnerId = "host-1",
                    LeaseEpoch = 7,
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
        document.ActiveLeaseOwnerId.ShouldBe("host-1");
        document.LeaseEpoch.ShouldBe(7);
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
        IVoiceVolatileMediaStreamPort? mediaPort = null,
        IVoicePresenceCapabilityProjectionRecoveryPort? capabilityRecoveryPort = null,
        IVoicePresenceCapabilityAutoEnablePort? capabilityAutoEnablePort = null) =>
        new(
            queryPort,
            leasePort ?? new RecordingLeasePort(),
            mediaPort ?? new RecordingMediaStreamPort(supportsRemoteAudio: true),
            capabilityRecoveryPort,
            capabilityAutoEnablePort);

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
            activeTransportLeaseId,
            7);

    private static VoiceToolExecutionContext CreateToolContext(string credentialRef) =>
        new()
        {
            CredentialRef = credentialRef,
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            CallerScopeId = "caller-scope-1",
            OwnerSubject = "owner-subject-1",
        };

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
                        handle.LeaseEpoch,
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
            activeTransportLeaseId,
            7,
            "host-1");

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

    // A capability query port whose read-model row is "missing" (returns null) until a recovery
    // re-materializes it via Heal(...), modelling idle-grain / projection-lag staleness.
    private sealed class HealableCapabilityQueryPort : IVoicePresenceCapabilityQueryPort
    {
        private VoicePresenceCapabilitySnapshot? _snapshot;

        public int Lookups { get; private set; }

        public void Heal(VoicePresenceCapabilitySnapshot snapshot) => _snapshot = snapshot;

        public Task<VoicePresenceCapabilitySnapshot?> GetAsync(
            string actorId,
            string? moduleName,
            CancellationToken ct = default)
        {
            _ = (actorId, moduleName, ct);
            Lookups++;
            return Task.FromResult(_snapshot);
        }
    }

    // Recovery port double. result controls TryRematerializeAsync's return; onHeal lets a test simulate
    // the projection row re-materializing (so a subsequent lookup succeeds).
    private sealed class RecordingCapabilityRecoveryPort(bool result = false, Action? onHeal = null)
        : IVoicePresenceCapabilityProjectionRecoveryPort
    {
        public List<string> ActorIds { get; } = [];

        public Task<bool> TryRematerializeAsync(string actorId, CancellationToken ct = default)
        {
            _ = ct;
            ActorIds.Add(actorId);
            onHeal?.Invoke();
            return Task.FromResult(result);
        }
    }

    // Auto-enable port double for the zero-config provisioning path. result controls
    // TryAutoEnableAsync's return; onEnable lets a test simulate the enable materializing the capability
    // (so a subsequent lookup succeeds), and records the (actorId, moduleName) it was asked to enable.
    private sealed class RecordingCapabilityAutoEnablePort(bool result = false, Action? onEnable = null)
        : IVoicePresenceCapabilityAutoEnablePort
    {
        public List<(string ActorId, string? ModuleName)> Enables { get; } = [];

        public Task<bool> TryAutoEnableAsync(string actorId, string? moduleName, CancellationToken ct = default)
        {
            _ = ct;
            Enables.Add((actorId, moduleName));
            onEnable?.Invoke();
            return Task.FromResult(result);
        }
    }

    // A lease port that models the lease-observation timeout: the grain accepted the lease, but the
    // capability projection never reflected it within the budget, so AcquireAsync surfaces TimeoutException.
    private sealed class TimeoutLeasePort : IVoicePresenceSessionLeasePort
    {
        public Task<VoicePresenceSessionLeaseHandle> AcquireAsync(
            VoicePresenceSessionLeaseRequest request,
            CancellationToken ct = default)
        {
            _ = (request, ct);
            throw new TimeoutException("Voice presence session lease was not observed.");
        }

        public Task ReleaseAsync(
            VoicePresenceSessionLeaseHandle handle,
            string reason,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task CompleteTransportLifetimeAsync(
            VoicePresenceSessionLeaseHandle handle,
            string transportLeaseId,
            string reason,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingLeaseObservationPort : IVoicePresenceLeaseObservationPort
    {
        public List<VoicePresenceSessionLeaseRequest> SessionLeaseRequests { get; } = [];

        public List<(VoicePresenceSessionLeaseHandle Handle, string TransportLeaseId)> AttachRequests { get; } = [];

        public Task<VoicePresenceCapabilitySnapshot> ObserveSessionLeaseAsync(
            VoicePresenceSessionLeaseRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            SessionLeaseRequests.Add(request);
            return Task.FromResult(new VoicePresenceCapabilitySnapshot(
                request.ActorId,
                request.ModuleName,
                request.ObservedStateVersion + 1,
                "event-observed",
                DateTimeOffset.UtcNow,
                true,
                false,
                24000,
                request.SessionId,
                request.ExpiresAtUtc,
                VoiceRemoteAudioSupport.Supported,
                null,
                7,
                request.OwnerId));
        }

        public Task<VoicePresenceCapabilitySnapshot> ObserveTransportAttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            string transportLeaseId,
            CancellationToken ct = default)
        {
            _ = ct;
            AttachRequests.Add((handle, transportLeaseId));
            return Task.FromResult(new VoicePresenceCapabilitySnapshot(
                handle.ActorId,
                handle.ModuleName,
                handle.ObservedStateVersion + 1,
                "event-attached",
                DateTimeOffset.UtcNow,
                true,
                true,
                24000,
                handle.SessionId,
                handle.ExpiresAtUtc,
                handle.RemoteAudioSupport,
                transportLeaseId,
                handle.LeaseEpoch,
                handle.OwnerId));
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
                request.ObservedRemoteAudioSupport,
                null,
                7,
                request.ToolContext?.Clone()));
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

        public List<VoicePresenceSessionLeaseHandle> DetachedHandles { get; } = [];

        public Task<bool> TryCancelResponseAsync(
            string transportLeaseId,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TrySendInputImageAsync(
            string transportLeaseId,
            VoiceInputImage inputImage,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TrySendToolResultAsync(
            string transportLeaseId,
            string callId,
            string resultJson,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TryInjectEventAsync(
            string transportLeaseId,
            VoiceConversationEventInjection injection,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default) =>
            AttachAsync(handle, transport, null, ct);

        public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            VoiceToolCredentialTransportBinding? toolCredentialBinding,
            CancellationToken ct = default)
        {
            _ = toolCredentialBinding;
            return Task.FromResult<VoiceTransportLifetimeCompleted?>(null);
        }

        public Task DetachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport? expectedTransport,
            CancellationToken ct = default)
        {
            DetachedHandles.Add(handle);
            return Task.CompletedTask;
        }

        public Task CompleteTransportLifetimeAsync(
            VoicePresenceSessionLeaseHandle handle,
            VoiceTransportLifetimeCompleted? completed,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingToolCredentialPort(bool bindResult) : IVoiceVolatileToolCredentialPort
    {
        public List<(VoiceToolCredentialTransportBinding Binding, string TransportLeaseId)> BindRequests { get; } = [];

        public List<string> ReleaseTransportLeaseIds { get; } = [];

        public Task<VoiceToolCredentialIssueResult?> IssueAsync(
            VoiceToolCredentialIssueRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<VoiceToolCredentialIssueResult?>(null);
        }

        public Task ReleaseAsync(string credentialRef, CancellationToken ct = default)
        {
            _ = credentialRef;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> BindTransportLeaseAsync(
            VoiceToolCredentialTransportBinding credentialBinding,
            string transportLeaseId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BindRequests.Add((credentialBinding, transportLeaseId));
            return Task.FromResult(bindResult);
        }

        public Task ReleaseTransportLeaseAsync(
            string transportLeaseId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReleaseTransportLeaseIds.Add(transportLeaseId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        private readonly object _gate = new();
        private readonly List<ISignalWaiter> _waiters = [];

        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            lock (_gate)
            {
                Dispatches.Add((actorId, envelope));
                var signal = envelope.Payload.Unpack<VoiceModuleSignal>();
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].TrySet(signal))
                        _waiters.RemoveAt(i);
                }
            }

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public Task<T> TakeSignalAsync<T>()
            where T : IMessage
        {
            lock (_gate)
            {
                foreach (var dispatch in Dispatches)
                {
                    var signal = dispatch.Envelope.Payload.Unpack<VoiceModuleSignal>();
                    if (TryGetSignal(signal, out T? payload))
                        return Task.FromResult(payload!);
                }

                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(new SignalWaiter<T>(completion));
                return completion.Task;
            }
        }

        private static bool TryGetSignal<T>(VoiceModuleSignal signal, out T? payload)
            where T : IMessage
        {
            if (typeof(T) == typeof(VoiceTransportLeaseRenewRequested) &&
                signal.SignalCase == VoiceModuleSignal.SignalOneofCase.TransportLeaseRenewRequested)
            {
                payload = (T)(object)signal.TransportLeaseRenewRequested;
                return true;
            }

            payload = default;
            return false;
        }

        private interface ISignalWaiter
        {
            bool TrySet(VoiceModuleSignal signal);
        }

        private sealed class SignalWaiter<T>(TaskCompletionSource<T> completion) : ISignalWaiter
            where T : IMessage
        {
            public bool TrySet(VoiceModuleSignal signal)
            {
                if (!TryGetSignal(signal, out T? payload))
                    return false;

                completion.TrySetResult(payload!);
                return true;
            }
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

    private sealed class ControllableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan delta)
        {
            ManualTimer[] timers;
            lock (_gate)
            {
                _utcNow = _utcNow.Add(delta);
                timers = _timers.ToArray();
            }

            foreach (var timer in timers)
                timer.FireIfDue();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            timer.FireIfDue();
            return timer;
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ControllableTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private readonly object _gate = new();
            private TimeSpan _period = period;
            private DateTimeOffset? _dueAt = ResolveDueAt(owner.GetUtcNow(), dueTime);
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return false;

                    _period = period;
                    _dueAt = ResolveDueAt(owner.GetUtcNow(), dueTime);
                }

                FireIfDue();
                return true;
            }

            public void FireIfDue()
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_disposed ||
                            !_dueAt.HasValue ||
                            _dueAt.Value > owner.GetUtcNow())
                        {
                            return;
                        }

                        _dueAt = _period == Timeout.InfiniteTimeSpan
                            ? null
                            : ResolveDueAt(owner.GetUtcNow(), _period);
                    }

                    callback(state);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                }

                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            private static DateTimeOffset? ResolveDueAt(DateTimeOffset now, TimeSpan dueTime) =>
                dueTime == Timeout.InfiniteTimeSpan ? null : now.Add(dueTime);
        }
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
        public int CancelCalls { get; private set; }
        public List<VoiceInputImage> InputImages { get; } = [];
        public List<(string CallId, string ResultJson)> ToolResults { get; } = [];
        public List<VoiceConversationEventInjection> InjectedEvents { get; } = [];

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

        public override Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InputImages.Add(inputImage.Clone());
            return Task.CompletedTask;
        }

        public Task EmitAudioAsync(byte[] pcm16, CancellationToken ct) =>
            _audioSink?.Invoke(
                _sessionKey,
                new VoiceProviderAudioFrame(pcm16, 24000, "response-1"),
                ct) ?? Task.CompletedTask;

        public Task EmitEventAsync(VoiceProviderEvent providerEvent, CancellationToken ct) =>
            _eventSink?.Invoke(_sessionKey, providerEvent, ct) ?? Task.CompletedTask;

        public override Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ToolResults.Add((callId, resultJson));
            return Task.CompletedTask;
        }

        public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InjectedEvents.Add(injection.Clone());
            return Task.CompletedTask;
        }

        public override Task CancelResponseAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CancelCalls++;
            return Task.CompletedTask;
        }

        public override Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct) =>
            Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
