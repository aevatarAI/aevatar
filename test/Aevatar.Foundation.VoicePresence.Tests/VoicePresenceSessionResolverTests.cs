using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Projection;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceSessionResolverTests
{
    [Fact]
    public async Task ResolveAsync_should_return_null_when_capability_readmodel_is_missing()
    {
        var resolver = new ActorOwnedVoicePresenceSessionResolver(
            new FakeCapabilityQueryPort(),
            new RecordingLeasePort(),
            new RecordingAttachmentPort());

        var resolution = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));

        resolution.Kind.ShouldBe(VoicePresenceSessionResolutionKind.PreflightFailed);
        resolution.PreflightFailure.ShouldBe(VoicePresencePreflightFailureKind.NotFound);
    }

    [Fact]
    public async Task ResolveAsync_should_create_pending_attach_session_from_supported_capability_and_typed_lease()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence_openai",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var queryPort = new FakeCapabilityQueryPort(capability);
        var leasePort = new RecordingLeasePort();
        var attachmentPort = new RecordingAttachmentPort();
        var resolver = new ActorOwnedVoicePresenceSessionResolver(queryPort, leasePort, attachmentPort);

        var resolution = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence_openai"));

        resolution.Kind.ShouldBe(VoicePresenceSessionResolutionKind.LeaseAcceptedPendingAttach);
        resolution.ObservedStateVersion.ShouldBe(5);
        var session = resolution.Session;
        session.ShouldNotBeNull();
        session.Module.ShouldBeNull();
        session.SelfEventDispatcher.ShouldBeNull();
        session.PcmSampleRateHz.ShouldBe(16000);
        session.IsInitialized.ShouldBeTrue();
        session.IsTransportAttached.ShouldBeFalse();
        leasePort.AcquireRequests.ShouldHaveSingleItem().ModuleName.ShouldBe("voice_presence_openai");

        var transport = new PassiveVoiceTransport();
        await session.AttachTransportAsync(transport);
        await session.DetachTransportAsync(transport);

        attachmentPort.AttachedHandles.ShouldHaveSingleItem().ModuleName.ShouldBe("voice_presence_openai");
        attachmentPort.DetachedHandles.ShouldHaveSingleItem().SessionId.ShouldBe(session.LeaseHandle!.SessionId);
        leasePort.ReleaseRequests.ShouldHaveSingleItem().Handle.SessionId.ShouldBe(session.LeaseHandle.SessionId);
    }

    [Theory]
    [InlineData(VoiceRemoteAudioSupport.Unspecified)]
    [InlineData(VoiceRemoteAudioSupport.LocalOnly)]
    public async Task ResolveAsync_should_return_unsupported_before_lease_when_remote_audio_is_not_supported(
        VoiceRemoteAudioSupport remoteAudioSupport)
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            remoteAudioSupport: remoteAudioSupport);
        var leasePort = new RecordingLeasePort();
        var resolver = new ActorOwnedVoicePresenceSessionResolver(
            new FakeCapabilityQueryPort(capability),
            leasePort,
            new RecordingAttachmentPort());

        var resolution = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));

        resolution.Kind.ShouldBe(VoicePresenceSessionResolutionKind.Unsupported);
        resolution.ObservedStateVersion.ShouldBe(5);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_should_return_unsupported_before_lease_when_default_attachment_port_is_unavailable()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var resolver = new ActorOwnedVoicePresenceSessionResolver(
            new FakeCapabilityQueryPort(capability),
            leasePort,
            new UnavailableVoicePresenceTransportAttachmentPort());

        var resolution = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));

        resolution.Kind.ShouldBe(VoicePresenceSessionResolutionKind.Unsupported);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_should_return_preflight_failed_before_lease_when_capability_is_not_initialized()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: false,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var resolver = new ActorOwnedVoicePresenceSessionResolver(
            new FakeCapabilityQueryPort(capability),
            leasePort,
            new RecordingAttachmentPort());

        var resolution = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));

        resolution.Kind.ShouldBe(VoicePresenceSessionResolutionKind.PreflightFailed);
        resolution.PreflightFailure.ShouldBe(VoicePresencePreflightFailureKind.NotInitialized);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_should_return_preflight_failed_before_lease_when_transport_is_already_attached()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            transportAttached: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var resolver = new ActorOwnedVoicePresenceSessionResolver(
            new FakeCapabilityQueryPort(capability),
            leasePort,
            new RecordingAttachmentPort());

        var resolution = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));

        resolution.Kind.ShouldBe(VoicePresenceSessionResolutionKind.PreflightFailed);
        resolution.PreflightFailure.ShouldBe(VoicePresencePreflightFailureKind.TransportAlreadyAttached);
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_should_return_attached_detach_session_when_transport_is_already_attached_for_detach()
    {
        var capability = CreateCapability(
            "agent-1",
            "voice_presence",
            initialized: true,
            activeSessionId: "session-1",
            transportAttached: true,
            remoteAudioSupport: VoiceRemoteAudioSupport.Supported);
        var leasePort = new RecordingLeasePort();
        var attachmentPort = new RecordingAttachmentPort();
        var resolver = new ActorOwnedVoicePresenceSessionResolver(
            new FakeCapabilityQueryPort(capability),
            leasePort,
            attachmentPort);

        var resolution = await resolver.ResolveAsync(new VoicePresenceSessionRequest(
            "agent-1",
            "voice_presence",
            VoicePresenceSessionRequestPurpose.Detach));

        resolution.Kind.ShouldBe(VoicePresenceSessionResolutionKind.LeaseAcceptedAttached);
        resolution.ObservedStateVersion.ShouldBe(5);
        var session = resolution.Session;
        session.ShouldNotBeNull();
        session.IsTransportAttached.ShouldBeTrue();

        await session.DetachTransportAsync();

        attachmentPort.DetachedHandles.ShouldHaveSingleItem().SessionId.ShouldBe("session-1");
        leasePort.ReleaseRequests.ShouldHaveSingleItem().Handle.SessionId.ShouldBe("session-1");
        leasePort.AcquireRequests.ShouldBeEmpty();
    }

    [Fact]
    public void VoicePresenceSessionResolution_should_model_four_typed_branches()
    {
        var session = new VoicePresenceSession(
            isInitialized: static () => true,
            isTransportAttached: static () => false,
            attachTransportAsync: static (_, _) => Task.CompletedTask,
            detachTransportAsync: static (_, _) => Task.CompletedTask);

        VoicePresenceSessionResolution.Unsupported().Kind.ShouldBe(VoicePresenceSessionResolutionKind.Unsupported);
        VoicePresenceSessionResolution.PreflightFailed(VoicePresencePreflightFailureKind.NotFound)
            .Kind.ShouldBe(VoicePresenceSessionResolutionKind.PreflightFailed);
        VoicePresenceSessionResolution.LeaseAcceptedPendingAttach(session)
            .Kind.ShouldBe(VoicePresenceSessionResolutionKind.LeaseAcceptedPendingAttach);
        VoicePresenceSessionResolution.LeaseAcceptedAttached(session)
            .Kind.ShouldBe(VoicePresenceSessionResolutionKind.LeaseAcceptedAttached);
    }

    [Fact]
    public async Task UnavailableVoicePresenceTransportAttachmentPort_should_throw_on_attach_and_allow_detach()
    {
        var port = new UnavailableVoicePresenceTransportAttachmentPort();
        var handle = new VoicePresenceSessionLeaseHandle(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            7,
            DateTimeOffset.UtcNow.AddMinutes(5),
            VoiceRemoteAudioSupport.LocalOnly);

        await Should.ThrowAsync<VoiceRemoteAudioTransportUnavailableException>(
            () => port.AttachAsync(handle, new PassiveVoiceTransport(), CancellationToken.None));
        await port.DetachAsync(handle, new PassiveVoiceTransport(), CancellationToken.None);
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
        var handle = new VoicePresenceSessionLeaseHandle(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            10,
            DateTimeOffset.UtcNow.AddMinutes(5),
            VoiceRemoteAudioSupport.LocalOnly);

        await leasePort.ReleaseAsync(handle, "test-release");

        var signal = dispatchPort.Dispatches.ShouldHaveSingleItem().Envelope.Payload.Unpack<VoiceModuleSignal>();
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.SessionLeaseReleased);
        signal.SessionLeaseReleased.SessionId.ShouldBe("lease-1");
        signal.SessionLeaseReleased.Reason.ShouldBe("test-release");
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
        });

        snapshot.UpdatedAt.ShouldBe(DateTimeOffset.MinValue);
        snapshot.PcmSampleRateHz.ShouldBe(24000);
        snapshot.ActiveSessionId.ShouldBeNull();
        snapshot.LeaseExpiresAt.ShouldBeNull();
        snapshot.RemoteAudioSupport.ShouldBe(VoiceRemoteAudioSupport.LocalOnly);
    }

    [Fact]
    public async Task VoicePresenceCapabilityReadModelProjector_should_upsert_committed_runtime_state()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var updatedAt = DateTimeOffset.UtcNow;
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
                    RemoteAudioSupport = VoiceRemoteAudioSupport.Supported,
                },
            },
            version: 9,
            eventId: "evt-9");

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
            WrapCommitted(new VoiceAudioReceived()));

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

        var plan = provider.GetPlans(new Aevatar.Foundation.Core.EventSourcing.CommittedStatePublicationContext
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
    public void ActorOwnedResolver_source_should_not_inspect_runtime_object_shape()
    {
        var repoRoot = FindRepoRoot();
        var oldResolverPath = Path.Combine(
            repoRoot,
            "src/Aevatar.Foundation.VoicePresence/Hosting/InProcessActorVoicePresenceSessionResolver.cs");
        File.Exists(oldResolverPath).ShouldBeFalse();

        var resolverPath = Path.Combine(
            repoRoot,
            "src/Aevatar.Foundation.VoicePresence/Hosting/ActorOwnedVoicePresenceSessionResolver.cs");
        var source = File.ReadAllText(resolverPath);
        source.ShouldNotContain("IActorRuntime");
        source.ShouldNotContain("actorRuntime.GetAsync");
        source.ShouldNotContain(".Agent");
        source.ShouldNotContain("actor.Agent");
        source.ShouldNotContain("IEventModuleContainer");
        source.ShouldNotContain("GetModules()");
    }

    [Fact]
    public void ActorOwnedSession_source_should_not_bind_post_lease_predicates_to_preflight_snapshot()
    {
        var repoRoot = FindRepoRoot();
        var sessionPath = Path.Combine(
            repoRoot,
            "src/Aevatar.Foundation.VoicePresence/Hosting/VoicePresenceSession.cs");
        var source = File.ReadAllText(sessionPath);

        source.ShouldNotContain("_isInitialized = () => capability.Initialized");
        source.ShouldNotContain("_isTransportAttached = () => capability.TransportAttached");
    }

    private static VoicePresenceCapabilitySnapshot CreateCapability(
        string actorId,
        string moduleName,
        bool initialized,
        string? activeSessionId = null,
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
            remoteAudioSupport);

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
    }

    private sealed class RecordingAttachmentPort : IVoicePresenceTransportAttachmentPort
    {
        public List<VoicePresenceSessionLeaseHandle> AttachedHandles { get; } = [];

        public List<VoicePresenceSessionLeaseHandle> DetachedHandles { get; } = [];

        public Task AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default)
        {
            AttachedHandles.Add(handle);
            return Task.CompletedTask;
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

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.CompletedTask;
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
        string eventId = "evt-1") =>
        new()
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("agent-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(new VoicePresenceRuntimeState()),
            }),
        };

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
}
