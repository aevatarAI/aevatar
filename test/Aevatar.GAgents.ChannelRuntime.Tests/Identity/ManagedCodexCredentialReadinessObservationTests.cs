using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class ManagedCodexCredentialReadinessObservationTests
{
    [Fact]
    public async Task BindAsync_WhenCommittedStateArrives_PublishesAuthoritativeSnapshot()
    {
        var operations = new List<string>();
        var activation = new RecordingActivationService(operations);
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub(operations);
        var port = new ManagedCodexCredentialReadinessObservationPort(activation, release, hub);
        var owner = Owner("user-a");
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);

        await using var lease = await port.BindAsync(owner);

        activation.LastRequest.Should().NotBeNull();
        activation.LastRequest!.RootActorId.Should().Be(actorId);
        activation.LastRequest.ProjectionKind.Should().Be(
            ManagedCodexCredentialReadinessObservationPort.ProjectionKind);
        activation.LastRequest.Mode.Should().Be(ProjectionRuntimeMode.SessionObservation);
        activation.LastRequest.SessionId.Should().HaveLength(32);
        hub.HasSubscription(actorId, activation.LastRequest.SessionId).Should().BeTrue();

        operations.Add("dispatch");
        var published = Snapshot(
            owner,
            "key-a",
            "us-sandbox-a",
            "us-llm-a",
            stateVersion: 4);
        await hub.PublishAsync(
            actorId,
            activation.LastRequest.SessionId,
            published);
        published.Credential.ApiKeyId = "mutated-after-publish";

        var observed = await ReadOneAsync(lease.ReadAllAsync());

        observed.Should().NotBeSameAs(published);
        observed.Credential.ApiKeyId.Should().Be("key-a");
        observed.Credential.ChronoSandboxUserServiceId.Should().Be("us-sandbox-a");
        observed.Credential.ChronoLlmUserServiceId.Should().Be("us-llm-a");
        observed.PendingRevocations.Should().ContainSingle()
            .Which.ApiKeyId.Should().Be("key-old-key-a");
        observed.StateVersion.Should().Be(4);
        observed.LastEventId.Should().Be("event-4");
        operations.Should().Equal("activate", "subscribe", "dispatch", "publish");
    }

    [Theory]
    [InlineData(
        ReadinessTransition.Provisioned,
        ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed)]
    [InlineData(
        ReadinessTransition.Rotated,
        ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed)]
    [InlineData(
        ReadinessTransition.PolicyReconciled,
        ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed)]
    [InlineData(
        ReadinessTransition.StructurallyConfirmed,
        ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed)]
    [InlineData(
        ReadinessTransition.RemoteValidationConfirmed,
        ManagedCodexCredentialReadinessEvidence.RemoteValidated)]
    public async Task Projector_WhenReadinessTransitionIsCommitted_PublishesAuthoritativeSnapshot(
        ReadinessTransition transition,
        ManagedCodexCredentialReadinessEvidence expectedEvidence)
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ManagedCodexCredentialReadinessProjector(hub);
        var owner = Owner("user-a");
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var descriptor = Descriptor(
            owner,
            "key-a",
            "us-sandbox-a",
            "us-llm-a");

        await projector.ProjectAsync(
            Context("session-a", actorId),
            CommittedEnvelope(
                descriptor,
                ReadinessEvent(transition, descriptor),
                version: 7,
                eventId: "event-7"));

        hub.Published.Should().ContainSingle();
        var published = hub.Published[0];
        published.RootActorId.Should().Be(actorId);
        published.SessionId.Should().Be("session-a");
        published.Event.Credential.ApiKeyId.Should().Be("key-a");
        published.Event.Credential.ChronoSandboxUserServiceId.Should().Be("us-sandbox-a");
        published.Event.Credential.ChronoLlmUserServiceId.Should().Be("us-llm-a");
        published.Event.PendingRevocations.Should().ContainSingle()
            .Which.ApiKeyId.Should().Be("key-old-key-a");
        published.Event.StateVersion.Should().Be(7);
        published.Event.LastEventId.Should().Be("event-7");
        published.Event.ReadinessEvidence.Should().Be(expectedEvidence);
    }

    [Fact]
    public async Task Projector_WhenCleanupProgressIsCommitted_DoesNotPublish()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ManagedCodexCredentialReadinessProjector(hub);
        var owner = Owner("user-a");
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);

        await projector.ProjectAsync(
            Context("session-a", actorId),
            CommittedEnvelope(
                Descriptor(owner, "key-a", "us-sandbox-a", "us-llm-a"),
                new ManagedCodexCredentialCleanupTrackCompletedEvent
                {
                    Owner = owner.Clone(),
                    ApiKeyId = "key-old-key-a",
                    Track = ManagedCodexCredentialCleanupTrack.NyxId,
                    CompletedAt = Timestamp.FromDateTimeOffset(
                        DateTimeOffset.Parse("2026-04-29T10:07:00Z")),
                },
                version: 7,
                eventId: "event-cleanup-progress",
                includePendingCleanup: false));

        hub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_WhenDuplicateCleanupLeavesStateUnchanged_DoesNotPublish()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ManagedCodexCredentialReadinessProjector(hub);
        var owner = Owner("user-a");
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);

        await projector.ProjectAsync(
            Context("session-a", actorId),
            CommittedEnvelope(
                Descriptor(owner, "key-a", "us-sandbox-a", "us-llm-a"),
                new ManagedCodexCredentialCleanupQueuedEvent
                {
                    Owner = owner.Clone(),
                    Cleanup = new ManagedCodexCredentialCleanup
                    {
                        ApiKeyId = "key-old-key-a",
                        SecretRef = "secret-old-key-a",
                        NyxIdPending = true,
                    },
                },
                version: 7,
                eventId: "event-cleanup-duplicate"));

        hub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_WhenEnvelopeIsNotCommittedState_DoesNotPublish()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ManagedCodexCredentialReadinessProjector(hub);
        var owner = Owner("user-a");
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);

        await projector.ProjectAsync(
            Context("session-a", actorId),
            new EventEnvelope
            {
                Payload = Any.Pack(new ManagedCodexCredentialState
                {
                    Credential = Descriptor(owner, "key-a", "us-sandbox-a", "us-llm-a"),
                }),
            });

        hub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task BindAsync_TwoSubscribersReceiveIndependentSessionSnapshots()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var port = new ManagedCodexCredentialReadinessObservationPort(activation, release, hub);
        var owner = Owner("user-a");
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);

        await using var first = await port.BindAsync(owner);
        await using var second = await port.BindAsync(owner);

        activation.Requests.Should().HaveCount(2);
        var firstSessionId = activation.Requests[0].SessionId;
        var secondSessionId = activation.Requests[1].SessionId;
        firstSessionId.Should().NotBe(secondSessionId);

        await hub.PublishAsync(
            actorId,
            firstSessionId,
            Snapshot(owner, "key-first", "sandbox-first", "llm-first", stateVersion: 1));
        await hub.PublishAsync(
            actorId,
            secondSessionId,
            Snapshot(owner, "key-second", "sandbox-second", "llm-second", stateVersion: 2));

        var firstObserved = await ReadOneAsync(first.ReadAllAsync());
        var secondObserved = await ReadOneAsync(second.ReadAllAsync());

        firstObserved.Credential.ApiKeyId.Should().Be("key-first");
        firstObserved.StateVersion.Should().Be(1);
        secondObserved.Credential.ApiKeyId.Should().Be("key-second");
        secondObserved.StateVersion.Should().Be(2);
    }

    [Fact]
    public async Task BindAsync_WhenChannelIsFull_PreservesBackpressureUntilReaderAdvances()
    {
        var activation = new RecordingActivationService();
        var hub = new RecordingSessionEventHub();
        var port = new ManagedCodexCredentialReadinessObservationPort(
            activation,
            new RecordingReleaseService(),
            hub);
        var owner = Owner("user-a");
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);

        await using var lease = await port.BindAsync(owner);
        var sessionId = activation.LastRequest!.SessionId;
        for (var version = 1; version <= 16; version++)
        {
            await hub.PublishAsync(
                actorId,
                sessionId,
                Snapshot(owner, $"key-{version}", $"sandbox-{version}", $"llm-{version}", version));
        }

        var blockedPublish = hub.PublishAsync(
            actorId,
            sessionId,
            Snapshot(owner, "key-17", "sandbox-17", "llm-17", stateVersion: 17));

        blockedPublish.IsCompleted.Should().BeFalse();
        (await ReadOneAsync(lease.ReadAllAsync())).StateVersion.Should().Be(1);
        await blockedPublish;
    }

    [Fact]
    public async Task DisposeAsync_WhenWriterIsBlocked_CompletesPublishAndReleasesWithoutCallbackFailure()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var port = new ManagedCodexCredentialReadinessObservationPort(activation, release, hub);
        var owner = Owner("user-a");
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var lease = await port.BindAsync(owner);
        var sessionId = activation.LastRequest!.SessionId;
        for (var version = 1; version <= 16; version++)
        {
            await hub.PublishAsync(
                actorId,
                sessionId,
                Snapshot(owner, $"key-{version}", $"sandbox-{version}", $"llm-{version}", version));
        }

        var blockedPublish = hub.PublishAsync(
            actorId,
            sessionId,
            Snapshot(owner, "key-17", "sandbox-17", "llm-17", stateVersion: 17));
        blockedPublish.IsCompleted.Should().BeFalse();

        var disposal = lease.DisposeAsync().AsTask();

        await Task.WhenAll(blockedPublish, disposal);
        hub.Subscriptions.Should().ContainSingle()
            .Which.DisposeCalls.Should().Be(1);
        release.Calls.Should().ContainSingle();
        release.Calls[0].CancellationToken.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_CompletesObservationAndReleasesRuntimeLeaseOnceWithoutCallerCancellation()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var port = new ManagedCodexCredentialReadinessObservationPort(activation, release, hub);
        using var callerCancellation = new CancellationTokenSource();
        var lease = await port.BindAsync(Owner("user-a"), callerCancellation.Token);

        callerCancellation.Cancel();
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        (await ReadAllAsync(lease.ReadAllAsync())).Should().BeEmpty();
        hub.Subscriptions.Should().ContainSingle()
            .Which.DisposeCalls.Should().Be(1);
        release.Calls.Should().ContainSingle();
        release.Calls[0].Lease.Should().BeSameAs(activation.Leases.Single());
        release.Calls[0].CancellationToken.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_WhenSubscriptionDisposalFails_StillReleasesRuntimeLease()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub(throwOnDispose: true);
        var port = new ManagedCodexCredentialReadinessObservationPort(activation, release, hub);
        var lease = await port.BindAsync(Owner("user-a"));

        Func<Task> dispose = async () => await lease.DisposeAsync();

        await dispose.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("subscription disposal failed");
        hub.Subscriptions.Should().ContainSingle()
            .Which.DisposeCalls.Should().Be(1);
        release.Calls.Should().ContainSingle();
        release.Calls[0].CancellationToken.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task BindAsync_WhenSubscriptionFails_ReleasesActivatedRuntimeLeaseOnceWithoutCallerCancellation()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub(throwOnSubscribe: true);
        var port = new ManagedCodexCredentialReadinessObservationPort(activation, release, hub);

        var bind = () => port.BindAsync(Owner("user-a"));

        await bind.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("subscription failed");
        activation.Leases.Should().ContainSingle();
        hub.Subscriptions.Should().BeEmpty();
        release.Calls.Should().ContainSingle();
        release.Calls[0].Lease.Should().BeSameAs(activation.Leases.Single());
        release.Calls[0].CancellationToken.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_WhenCalledConcurrently_JoinsTheSameTeardownCompletion()
    {
        var disposalStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisposal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub(
            disposalStarted: disposalStarted,
            disposalRelease: allowDisposal.Task);
        var port = new ManagedCodexCredentialReadinessObservationPort(activation, release, hub);
        var lease = await port.BindAsync(Owner("user-a"));

        var firstDisposal = lease.DisposeAsync().AsTask();
        await disposalStarted.Task;
        var secondDisposal = lease.DisposeAsync().AsTask();
        var secondCompletedBeforeRelease = secondDisposal.IsCompleted;

        allowDisposal.TrySetResult(true);
        await Task.WhenAll(firstDisposal, secondDisposal);

        secondCompletedBeforeRelease.Should().BeFalse();
        hub.Subscriptions.Should().ContainSingle()
            .Which.DisposeCalls.Should().Be(1);
        release.Calls.Should().ContainSingle();
        release.Calls[0].CancellationToken.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public void SnapshotCodec_UsesStableChannelAndConstantEventType()
    {
        var codec = new ManagedCodexCredentialSnapshotCodec();
        var snapshot = Snapshot(
            Owner("user-a"),
            "key-a",
            "us-sandbox-a",
            "us-llm-a",
            stateVersion: 4);

        codec.Channel.Should().Be("managed-codex-credential-readiness");
        codec.GetEventType(snapshot).Should().Be("snapshot");
        codec.Deserialize("snapshot", codec.Serialize(snapshot)).Should().Be(snapshot);
        codec.Deserialize("other", codec.Serialize(snapshot)).Should().BeNull();
        codec.Deserialize("snapshot", ByteString.Empty).Should().BeNull();
        codec.Deserialize("snapshot", ByteString.CopyFromUtf8("not-protobuf")).Should().BeNull();
    }

    [Fact]
    public void AddChannelIdentity_RegistersManagedCodexReadinessObservationChain()
    {
        var services = new ServiceCollection();

        services.AddChannelIdentity();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionSessionEventCodec<ManagedCodexCredentialSnapshot>) &&
            descriptor.ImplementationType == typeof(ManagedCodexCredentialSnapshotCodec));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionSessionEventHub<ManagedCodexCredentialSnapshot>) &&
            descriptor.ImplementationType == typeof(
                Aevatar.CQRS.Projection.Core.Streaming.ProjectionSessionEventHub<
                    ManagedCodexCredentialSnapshot>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionProjector<ManagedCodexCredentialReadinessProjectionContext>) &&
            descriptor.ImplementationType == typeof(ManagedCodexCredentialReadinessProjector));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IManagedCodexCredentialReadinessObservationPort) &&
            descriptor.ImplementationType == typeof(ManagedCodexCredentialReadinessObservationPort));
    }

    private static ExternalSubjectRef Owner(string externalUserId) =>
        new()
        {
            Platform = "nyxid",
            Tenant = "tenant-a",
            ExternalUserId = externalUserId,
        };

    private static ManagedCodexCredentialDescriptor Descriptor(
        ExternalSubjectRef owner,
        string apiKeyId,
        string chronoSandboxUserServiceId,
        string chronoLlmUserServiceId) =>
        new()
        {
            Owner = owner.Clone(),
            ApiKeyId = apiKeyId,
            SecretReference = new SecretReference
            {
                Ref = $"secret-{apiKeyId}",
                Purpose = "managed.codex-invocation-agent-key",
                OwnerScopeKey = ManagedCodexCredentialActorIdentity.From(owner),
                Version = 1,
            },
            ChronoSandboxUserServiceId = chronoSandboxUserServiceId,
            ChronoSandboxServiceSlug = "chrono-sandbox",
            ChronoLlmUserServiceId = chronoLlmUserServiceId,
            Status = ManagedCodexCredentialStatus.Active,
        };

    private static ManagedCodexCredentialSnapshot Snapshot(
        ExternalSubjectRef owner,
        string apiKeyId,
        string chronoSandboxUserServiceId,
        string chronoLlmUserServiceId,
        long stateVersion)
    {
        var snapshot = new ManagedCodexCredentialSnapshot
        {
            Credential = Descriptor(
                owner,
                apiKeyId,
                chronoSandboxUserServiceId,
                chronoLlmUserServiceId),
            StateVersion = stateVersion,
            LastEventId = $"event-{stateVersion}",
            ReadinessEvidence =
                ManagedCodexCredentialReadinessEvidence.RemoteValidated,
        };
        snapshot.PendingRevocations.Add(new ManagedCodexCredentialCleanup
        {
            ApiKeyId = $"key-old-{apiKeyId}",
            SecretRef = $"secret-old-{apiKeyId}",
            NyxIdPending = true,
        });
        return snapshot;
    }

    private static ManagedCodexCredentialReadinessProjectionContext Context(
        string sessionId,
        string actorId) =>
        new()
        {
            SessionId = sessionId,
            RootActorId = actorId,
            ProjectionKind = ManagedCodexCredentialReadinessObservationPort.ProjectionKind,
        };

    private static EventEnvelope CommittedEnvelope(
        ManagedCodexCredentialDescriptor credential,
        IMessage committedEvent,
        long version,
        string eventId,
        bool includePendingCleanup = true)
    {
        var state = new ManagedCodexCredentialState
        {
            Credential = credential.Clone(),
        };
        if (includePendingCleanup)
        {
            state.PendingRevocations.Add(new ManagedCodexCredentialCleanup
            {
                ApiKeyId = $"key-old-{credential.ApiKeyId}",
                SecretRef = $"secret-old-{credential.ApiKeyId}",
                NyxIdPending = true,
            });
        }

        var envelope = TestEnvelopeBuilder.BuildCommittedEnvelope(state, version, eventId);
        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        published.StateEvent.EventType = committedEvent.Descriptor.FullName;
        published.StateEvent.EventData = Any.Pack(committedEvent);
        envelope.Payload = Any.Pack(published);
        return envelope;
    }

    private static IMessage ReadinessEvent(
        ReadinessTransition transition,
        ManagedCodexCredentialDescriptor credential) =>
        transition switch
        {
            ReadinessTransition.Provisioned =>
                new ManagedCodexCredentialProvisionedEvent
                {
                    Credential = credential.Clone(),
                },
            ReadinessTransition.Rotated =>
                new ManagedCodexCredentialRotatedEvent
                {
                    PreviousApiKeyId = "key-old",
                    Credential = credential.Clone(),
                },
            ReadinessTransition.PolicyReconciled =>
                new ManagedCodexCredentialPolicyReconciledEvent
                {
                    ApiKeyId = credential.ApiKeyId,
                    Credential = credential.Clone(),
                },
            ReadinessTransition.StructurallyConfirmed =>
                new ManagedCodexCredentialReadinessConfirmedEvent
                {
                    ApiKeyId = credential.ApiKeyId,
                    VerifiedAt = Timestamp.FromDateTimeOffset(
                        DateTimeOffset.Parse("2026-04-29T10:07:00Z")),
                    ReadinessEvidence =
                        ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
                },
            ReadinessTransition.RemoteValidationConfirmed =>
                new ManagedCodexCredentialReadinessConfirmedEvent
                {
                    ApiKeyId = credential.ApiKeyId,
                    VerifiedAt = Timestamp.FromDateTimeOffset(
                        DateTimeOffset.Parse("2026-04-29T10:07:00Z")),
                    ReadinessEvidence =
                        ManagedCodexCredentialReadinessEvidence.RemoteValidated,
                },
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

    public enum ReadinessTransition
    {
        Provisioned,
        Rotated,
        PolicyReconciled,
        StructurallyConfirmed,
        RemoteValidationConfirmed,
    }

    private static async Task<T> ReadOneAsync<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
            return item;
        throw new InvalidOperationException("Observation completed without an item.");
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }

    private sealed class RecordingActivationService(List<string>? operations = null)
        : IProjectionScopeActivationService<ManagedCodexCredentialReadinessRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];
        public List<ManagedCodexCredentialReadinessRuntimeLease> Leases { get; } = [];
        public ProjectionScopeStartRequest? LastRequest => Requests.LastOrDefault();

        public Task<ManagedCodexCredentialReadinessRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations?.Add("activate");
            Requests.Add(request);
            var lease = new ManagedCodexCredentialReadinessRuntimeLease(
                new ManagedCodexCredentialReadinessProjectionContext
                {
                    SessionId = request.SessionId,
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                });
            Leases.Add(lease);
            return Task.FromResult(lease);
        }
    }

    private sealed class RecordingReleaseService
        : IProjectionScopeReleaseService<ManagedCodexCredentialReadinessRuntimeLease>
    {
        public List<ReleaseCall> Calls { get; } = [];

        public Task ReleaseIfIdleAsync(
            ManagedCodexCredentialReadinessRuntimeLease lease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new ReleaseCall(lease, ct));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionEventHub(
        List<string>? operations = null,
        bool throwOnDispose = false,
        bool throwOnSubscribe = false,
        TaskCompletionSource<bool>? disposalStarted = null,
        Task? disposalRelease = null)
        : IProjectionSessionEventHub<ManagedCodexCredentialSnapshot>
    {
        private readonly Dictionary<
            (string RootActorId, string SessionId),
            Func<ManagedCodexCredentialSnapshot, ValueTask>> _handlers = [];

        public List<PublishedEvent> Published { get; } = [];
        public List<RecordingSubscription> Subscriptions { get; } = [];

        public bool HasSubscription(string rootActorId, string sessionId) =>
            _handlers.ContainsKey((rootActorId, sessionId));

        public async Task PublishAsync(
            string rootActorId,
            string sessionId,
            ManagedCodexCredentialSnapshot evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations?.Add("publish");
            Published.Add(new PublishedEvent(rootActorId, sessionId, evt.Clone()));
            if (_handlers.TryGetValue((rootActorId, sessionId), out var handler))
                await handler(evt);
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<ManagedCodexCredentialSnapshot, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations?.Add("subscribe");
            if (throwOnSubscribe)
            {
                return Task.FromException<IAsyncDisposable>(
                    new InvalidOperationException("subscription failed"));
            }

            _handlers.Add((rootActorId, sessionId), handler);
            var subscription = new RecordingSubscription(
                () => _handlers.Remove((rootActorId, sessionId)),
                throwOnDispose,
                disposalStarted,
                disposalRelease);
            Subscriptions.Add(subscription);
            return Task.FromResult<IAsyncDisposable>(subscription);
        }
    }

    private sealed class RecordingSubscription(
        Action onDispose,
        bool throwOnDispose,
        TaskCompletionSource<bool>? disposalStarted,
        Task? disposalRelease) : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            onDispose();
            disposalStarted?.TrySetResult(true);
            if (disposalRelease is not null)
                await disposalRelease;
            if (throwOnDispose)
                throw new InvalidOperationException("subscription disposal failed");
        }
    }

    private sealed record PublishedEvent(
        string RootActorId,
        string SessionId,
        ManagedCodexCredentialSnapshot Event);

    private sealed record ReleaseCall(
        ManagedCodexCredentialReadinessRuntimeLease Lease,
        CancellationToken CancellationToken);
}
