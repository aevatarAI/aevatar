using System.Runtime.CompilerServices;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class NyxIdAuthorizationCatalogRefreshObservationInfrastructureTests
{
    [Theory]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Started)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Failed)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded)]
    public void Codec_ShouldRoundTripTypedCommittedOutcome(
        NyxIdAuthorizationCatalogRefreshOutcomeStatus status)
    {
        var codec = new NyxIdAuthorizationCatalogRefreshObservationSessionEventCodec();
        var outcome = new NyxIdAuthorizationCatalogRefreshCommittedOutcome(
            "refresh-alpha",
            status,
            42,
            status == NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed
                ? string.Empty
                : "stable_failure_code",
            DateTimeOffset.Parse("2026-07-21T09:00:00Z"));

        var eventType = codec.GetEventType(outcome);
        var decoded = codec.Deserialize(eventType, codec.Serialize(outcome));

        codec.Channel.Should().Be("nyxid-authorization-catalog-refresh-observation");
        eventType.Should().Be(NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor.FullName);
        decoded.Should().BeEquivalentTo(outcome);
        codec.Deserialize("different-event", codec.Serialize(outcome)).Should().BeNull();
        codec.Deserialize(eventType, ByteString.CopyFrom(new byte[] { 0x0A, 0x05 })).Should().BeNull();
    }

    [Fact]
    public async Task Projector_ShouldPublishOnlyMatchingCommittedRefreshOutcome()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdAuthorizationCatalogRefreshObservationSessionEventProjector(hub);
        var context = new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
        {
            RootActorId = "nyxid-authorization-catalog:owner-alpha",
            ProjectionKind = "nyxid-authorization-catalog-refresh-observation",
            SessionId = "refresh-alpha",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new NyxIdAuthorizationCatalogRefreshOutcomeEvent
            {
                RefreshId = "refresh-alpha",
                Status = NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed,
                StateVersion = 7,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.Parse("2026-07-21T09:00:00Z")),
            }));
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new NyxIdAuthorizationCatalogRefreshOutcomeEvent
            {
                RefreshId = "refresh-other",
                Status = NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed,
                StateVersion = 8,
                FailureCode = "provider_unavailable",
                ObservedAtUtc = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.Parse("2026-07-21T09:00:01Z")),
            }));
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new StringValue { Value = "not-a-refresh-outcome" }));

        hub.Published.Should().ContainSingle();
        var published = hub.Published[0];
        published.RootActorId.Should().Be("nyxid-authorization-catalog:owner-alpha");
        published.SessionId.Should().Be("refresh-alpha");
        published.Outcome.RefreshId.Should().Be("refresh-alpha");
        published.Outcome.Status.Should().Be(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed);
        published.Outcome.StateVersion.Should().Be(7);
    }

    [Fact]
    public async Task PreparationPort_ShouldActivateAndReleaseExactRefreshScope()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>();
        var port = new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort(
            activation,
            release);

        var preparation = await port.PrepareAsync(
            "  nyxid-authorization-catalog:owner-alpha  ",
            "  refresh-alpha  ");

        preparation.Should().Be(new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation(
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha"));
        activation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProjectionScopeStartRequest
            {
                RootActorId = "nyxid-authorization-catalog:owner-alpha",
                ProjectionKind = "nyxid-authorization-catalog-refresh-observation",
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = "refresh-alpha",
            });

        await port.ReleaseAsync(preparation!);

        release.Released.Should().ContainSingle();
        release.Released[0].ActorId.Should().Be("nyxid-authorization-catalog:owner-alpha");
        release.Released[0].RefreshId.Should().Be("refresh-alpha");
    }

    [Fact]
    public async Task PreparationPort_ShouldPropagateCallerCancellation()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>();
        var port = new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort(
            activation,
            release);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => port.PrepareAsync(
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha",
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProjectionPort_ShouldAttachOnlyToPreparedExistingRefreshScope()
    {
        var hub = new RecordingSessionEventHub();
        var lease = new NyxIdAuthorizationCatalogRefreshObservationRuntimeLease(
            new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
            {
                RootActorId = "nyxid-authorization-catalog:owner-alpha",
                ProjectionKind = "nyxid-authorization-catalog-refresh-observation",
                SessionId = "refresh-alpha",
            });
        var lookup = new RecordingAttachExistingLeaseLookup { Lease = lease };
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>();
        var port = new NyxIdAuthorizationCatalogRefreshObservationProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            release,
            hub,
            lookup);
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingRefreshProjectionAsync(
            " nyxid-authorization-catalog:owner-alpha ",
            " refresh-alpha ",
            sink);

        attachment.Should().NotBeNull();
        lookup.Requests.Should().ContainSingle();
        lookup.Requests[0].RootActorId.Should().Be("nyxid-authorization-catalog:owner-alpha");
        lookup.Requests[0].SessionId.Should().Be("refresh-alpha");
        hub.LastSubscription.Should().Be((
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha"));

        var outcome = CreateOutcome();
        await hub.SubscriptionHandler!(outcome);
        sink.Events.Should().ContainSingle().Which.Should().BeSameAs(outcome);

        await port.DetachLiveSinkAsync(attachment!.LiveSinkLease);
        await port.ReleaseActorProjectionAsync(attachment.ProjectionLease);
        release.Released.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterCatalogRefreshObservationRuntime()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(
                INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort) &&
            x.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(INyxIdAuthorizationCatalogRefreshObservationProjectionPort) &&
            x.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(
                IProjectionSessionEventCodec<NyxIdAuthorizationCatalogRefreshCommittedOutcome>) &&
            x.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationSessionEventCodec));
        services.Should().Contain(x =>
            x.ServiceType == typeof(
                IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionScopeActivationService<
                NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionScopeAttachExistingLeaseLookup<
                NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionProjector<
                NyxIdAuthorizationCatalogRefreshObservationProjectionContext>) &&
            x.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationSessionEventProjector));
    }

    private static NyxIdAuthorizationCatalogRefreshCommittedOutcome CreateOutcome() =>
        new(
            "refresh-alpha",
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed,
            10,
            string.Empty,
            DateTimeOffset.Parse("2026-07-21T09:00:00Z"));

    private static EventEnvelope CommittedEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = 1,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            }),
        };

    private sealed class RecordingActivationService
        : IProjectionScopeActivationService<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new NyxIdAuthorizationCatalogRefreshObservationRuntimeLease(
                new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                    SessionId = request.SessionId,
                }));
        }
    }

    private sealed class RecordingAttachExistingLeaseLookup
        : IProjectionScopeAttachExistingLeaseLookup<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public NyxIdAuthorizationCatalogRefreshObservationRuntimeLease? Lease { get; init; }

        public Task<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease?> TryGetAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Lease);
        }
    }

    private sealed class RecordingSessionEventHub
        : IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome>
    {
        public List<(
            string RootActorId,
            string SessionId,
            NyxIdAuthorizationCatalogRefreshCommittedOutcome Outcome)> Published { get; } = [];

        public (string RootActorId, string SessionId)? LastSubscription { get; private set; }

        public Func<NyxIdAuthorizationCatalogRefreshCommittedOutcome, ValueTask>?
            SubscriptionHandler { get; private set; }

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            NyxIdAuthorizationCatalogRefreshCommittedOutcome evt,
            CancellationToken ct = default)
        {
            Published.Add((rootActorId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<NyxIdAuthorizationCatalogRefreshCommittedOutcome, ValueTask> handler,
            CancellationToken ct = default)
        {
            LastSubscription = (rootActorId, sessionId);
            SubscriptionHandler = handler;
            return Task.FromResult<IAsyncDisposable>(new NoopSubscription());
        }
    }

    private sealed class RecordingEventSink
        : IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome>
    {
        public List<NyxIdAuthorizationCatalogRefreshCommittedOutcome> Events { get; } = [];

        public void Push(NyxIdAuthorizationCatalogRefreshCommittedOutcome evt) => Events.Add(evt);

        public ValueTask PushAsync(
            NyxIdAuthorizationCatalogRefreshCommittedOutcome evt,
            CancellationToken ct = default)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<NyxIdAuthorizationCatalogRefreshCommittedOutcome> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopSubscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
