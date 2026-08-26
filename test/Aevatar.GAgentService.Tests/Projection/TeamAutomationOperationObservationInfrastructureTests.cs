using System.Runtime.CompilerServices;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class TeamAutomationOperationObservationInfrastructureTests
{
    [Fact]
    public void Codec_ShouldRoundTripTypedCommittedOutcome()
    {
        var codec = new TeamAutomationOperationObservationSessionEventCodec();
        var observedAt = DateTimeOffset.Parse("2026-07-16T08:30:00+00:00");
        var outcome = new TeamAutomationOperationCommittedOutcome(
            "schedule-1",
            "operation-1",
            "idempotency-1",
            TeamAutomationOperationObservationStages.Complete,
            true,
            42,
            "",
            "",
            observedAt,
            new ScheduledInvocationAgentKeyCredentialReference(
                new SecretReference
                {
                    Ref = "vault://credential-1",
                    Purpose = "scheduled-invocation-agent-key",
                    OwnerScopeKey = "scope-1",
                },
                "key-1",
                1_800_000_000_000,
                []),
            new ScheduledInvocationAuthorizationOwner("nyxid", "personal", "user-1"),
            true,
            false,
            CredentialEffectLocator: new ScheduledCredentialEffectLocator(
                "studio-schedule-abc",
                "sec_studio_schedule_abc",
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                "schedule:schedule-1",
                new ScheduledInvocationAuthorizationOwner("nyxid", "personal", "user-1")),
            MutationDigest: "mutation-digest-1",
            ObservationRequestId: "observation-request-1",
            NewOperationCommitted: true);

        var eventType = codec.GetEventType(outcome);
        var payload = codec.Serialize(outcome);
        var serialized = TeamAutomationOperationObservedEvent.Parser.ParseFrom(payload);
        var decoded = codec.Deserialize(eventType, payload);

        codec.Channel.Should().Be("team-automation-operation-observation");
        eventType.Should().Be(TeamAutomationOperationObservedEvent.Descriptor.FullName);
        serialized.NewOperationCommitted.Should().BeTrue();
        decoded!.NewOperationCommitted.Should().BeTrue();
        decoded.Should().BeEquivalentTo(outcome);
        codec.Deserialize("different-event", codec.Serialize(outcome)).Should().BeNull();
        codec.Deserialize(eventType, ByteString.CopyFrom(new byte[] { 0x0A, 0x05 })).Should().BeNull();
    }

    [Theory]
    [InlineData(TeamAutomationOperationObservationStatus.RejectedInvalidRequest)]
    [InlineData(TeamAutomationOperationObservationStatus.RejectedConflict)]
    [InlineData(TeamAutomationOperationObservationStatus.RejectedUnauthorized)]
    [InlineData(TeamAutomationOperationObservationStatus.RejectedNotFound)]
    public void Codec_ShouldRoundTripTypedRejectedOutcome(
        TeamAutomationOperationObservationStatus status)
    {
        var codec = new TeamAutomationOperationObservationSessionEventCodec();
        var outcome = new TeamAutomationOperationCommittedOutcome(
            "schedule-1",
            "operation-1",
            "idempotency-1",
            TeamAutomationOperationObservationStages.Begin,
            OwnsEffectAttempt: false,
            StateVersion: 9,
            ErrorCode: "team_automation_operation_conflict",
            ErrorMessage: string.Empty,
            ObservedAtUtc: DateTimeOffset.Parse("2026-07-16T08:30:00+00:00"),
            PendingRevocationCredential: null,
            PendingRevocationOwner: null,
            NyxIdRevocationPending: false,
            VaultRevocationPending: false,
            ObservationRequestId: "request-1",
            Status: status);

        var decoded = codec.Deserialize(codec.GetEventType(outcome), codec.Serialize(outcome));

        decoded.Should().BeEquivalentTo(outcome);
    }

    [Fact]
    public void Codec_ShouldTreatLegacyUnspecifiedObservationAsCommitted()
    {
        var codec = new TeamAutomationOperationObservationSessionEventCodec();
        var legacy = new TeamAutomationOperationObservedEvent
        {
            ScheduleId = "schedule-1",
            OperationId = "operation-1",
            IdempotencyKey = "idempotency-1",
            Stage = TeamAutomationOperationObservationStages.Begin,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch),
        };

        var decoded = codec.Deserialize(
            TeamAutomationOperationObservedEvent.Descriptor.FullName,
            legacy.ToByteString());

        decoded!.Status.Should().Be(TeamAutomationOperationObservationStatus.Committed);
    }

    [Fact]
    public async Task Projector_ShouldPublishOnlyMatchingCommittedOperation()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new TeamAutomationOperationObservationSessionEventProjector(hub);
        var context = new TeamAutomationOperationObservationProjectionContext
        {
            RootActorId = "scheduled-dispatch:schedule-1",
            ProjectionKind = "team-automation-operation-observation",
            SessionId = "operation-1",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new TeamAutomationOperationObservedEvent
            {
                ScheduleId = "schedule-1",
                OperationId = "operation-1",
                IdempotencyKey = "idempotency-1",
                Stage = TeamAutomationOperationObservationStages.Begin,
                OwnsEffectAttempt = true,
                StateVersion = 7,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-16T08:00:00+00:00")),
            }));
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new TeamAutomationOperationObservedEvent
            {
                ScheduleId = "schedule-1",
                OperationId = "other-operation",
                IdempotencyKey = "idempotency-2",
                Stage = TeamAutomationOperationObservationStages.Begin,
                StateVersion = 8,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }));
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new StringValue { Value = "not-an-operation-outcome" }));

        hub.Published.Should().ContainSingle();
        var published = hub.Published[0];
        published.RootActorId.Should().Be("scheduled-dispatch:schedule-1");
        published.SessionId.Should().Be("operation-1");
        published.Outcome.ScheduleId.Should().Be("schedule-1");
        published.Outcome.OperationId.Should().Be("operation-1");
        published.Outcome.Stage.Should().Be(TeamAutomationOperationObservationStages.Begin);
        published.Outcome.OwnsEffectAttempt.Should().BeTrue();
        published.Outcome.StateVersion.Should().Be(7);
    }

    [Fact]
    public async Task PreparationPort_ShouldActivateBeforeAttachAndReleaseExactOperationScope()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingProjectionReleaseService<
            TeamAutomationOperationObservationRuntimeLease>();
        var port = new TeamAutomationOperationObservationScopeLeasePreparationPort(
            activation,
            release);

        var preparation = await port.PrepareAsync(
            "  scheduled-dispatch:schedule-1  ",
            "  operation-1  ");

        preparation.Should().Be(new TeamAutomationOperationObservationScopeLeasePreparation(
            "scheduled-dispatch:schedule-1",
            "operation-1"));
        activation.Requests.Should().ContainSingle();
        activation.Requests[0].Should().BeEquivalentTo(new ProjectionScopeStartRequest
        {
            RootActorId = "scheduled-dispatch:schedule-1",
            ProjectionKind = "team-automation-operation-observation",
            Mode = ProjectionRuntimeMode.SessionObservation,
            SessionId = "operation-1",
        });

        await port.ReleaseAsync(preparation!);

        release.Released.Should().ContainSingle();
        release.Released[0].ActorId.Should().Be("scheduled-dispatch:schedule-1");
        release.Released[0].OperationId.Should().Be("operation-1");
    }

    [Fact]
    public async Task ProjectionPort_ShouldAttachOnlyToPreparedExistingOperationScope()
    {
        var hub = new RecordingSessionEventHub();
        var lease = new TeamAutomationOperationObservationRuntimeLease(
            new TeamAutomationOperationObservationProjectionContext
            {
                RootActorId = "scheduled-dispatch:schedule-1",
                ProjectionKind = "team-automation-operation-observation",
                SessionId = "operation-1",
            });
        var lookup = new RecordingAttachExistingLeaseLookup { Lease = lease };
        var release = new RecordingProjectionReleaseService<
            TeamAutomationOperationObservationRuntimeLease>();
        var port = new TeamAutomationOperationObservationProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            release,
            hub,
            lookup);
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingOperationProjectionAsync(
            " scheduled-dispatch:schedule-1 ",
            " operation-1 ",
            sink);

        attachment.Should().NotBeNull();
        lookup.Requests.Should().ContainSingle();
        lookup.Requests[0].RootActorId.Should().Be("scheduled-dispatch:schedule-1");
        lookup.Requests[0].SessionId.Should().Be("operation-1");
        hub.LastSubscription.Should().Be(("scheduled-dispatch:schedule-1", "operation-1"));

        var outcome = CreateOutcome();
        await hub.SubscriptionHandler!(outcome);
        sink.Events.Should().ContainSingle().Which.Should().BeSameAs(outcome);

        await port.DetachLiveSinkAsync(attachment!.LiveSinkLease);
        await port.ReleaseActorProjectionAsync(attachment.ProjectionLease);
        release.Released.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterOperationObservationRuntime()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(ITeamAutomationOperationObservationScopeLeasePreparationPort) &&
            x.ImplementationType == typeof(TeamAutomationOperationObservationScopeLeasePreparationPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ITeamAutomationOperationObservationProjectionPort) &&
            x.ImplementationType == typeof(TeamAutomationOperationObservationProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionSessionEventCodec<TeamAutomationOperationCommittedOutcome>) &&
            x.ImplementationType == typeof(TeamAutomationOperationObservationSessionEventCodec));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionSessionEventHub<TeamAutomationOperationCommittedOutcome>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(
                IProjectionScopeActivationService<TeamAutomationOperationObservationRuntimeLease>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(
                IProjectionScopeAttachExistingLeaseLookup<TeamAutomationOperationObservationRuntimeLease>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(
                IProjectionProjector<TeamAutomationOperationObservationProjectionContext>) &&
            x.ImplementationType == typeof(TeamAutomationOperationObservationSessionEventProjector));
    }

    private static TeamAutomationOperationCommittedOutcome CreateOutcome() =>
        new(
            "schedule-1",
            "operation-1",
            "idempotency-1",
            TeamAutomationOperationObservationStages.Delete,
            true,
            10,
            "",
            "",
            DateTimeOffset.UtcNow,
            null,
            null,
            false,
            false);

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
        : IProjectionScopeActivationService<TeamAutomationOperationObservationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<TeamAutomationOperationObservationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new TeamAutomationOperationObservationRuntimeLease(
                new TeamAutomationOperationObservationProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                    SessionId = request.SessionId,
                }));
        }
    }

    private sealed class RecordingAttachExistingLeaseLookup
        : IProjectionScopeAttachExistingLeaseLookup<TeamAutomationOperationObservationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public TeamAutomationOperationObservationRuntimeLease? Lease { get; init; }

        public Task<TeamAutomationOperationObservationRuntimeLease?> TryGetAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Lease);
        }
    }

    private sealed class RecordingSessionEventHub
        : IProjectionSessionEventHub<TeamAutomationOperationCommittedOutcome>
    {
        public List<(string RootActorId, string SessionId, TeamAutomationOperationCommittedOutcome Outcome)>
            Published { get; } = [];

        public (string RootActorId, string SessionId)? LastSubscription { get; private set; }

        public Func<TeamAutomationOperationCommittedOutcome, ValueTask>? SubscriptionHandler { get; private set; }

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            TeamAutomationOperationCommittedOutcome evt,
            CancellationToken ct = default)
        {
            Published.Add((rootActorId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<TeamAutomationOperationCommittedOutcome, ValueTask> handler,
            CancellationToken ct = default)
        {
            LastSubscription = (rootActorId, sessionId);
            SubscriptionHandler = handler;
            return Task.FromResult<IAsyncDisposable>(new NoopSubscription());
        }
    }

    private sealed class RecordingEventSink : IEventSink<TeamAutomationOperationCommittedOutcome>
    {
        public List<TeamAutomationOperationCommittedOutcome> Events { get; } = [];

        public void Push(TeamAutomationOperationCommittedOutcome evt) => Events.Add(evt);

        public ValueTask PushAsync(
            TeamAutomationOperationCommittedOutcome evt,
            CancellationToken ct = default)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<TeamAutomationOperationCommittedOutcome> ReadAllAsync(
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
