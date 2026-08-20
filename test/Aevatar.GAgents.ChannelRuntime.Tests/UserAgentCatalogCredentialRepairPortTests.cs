using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogCredentialRepairPortTests
{
    [Fact]
    public async Task RepairMissingSecretReferenceAsync_BindsBeforeDispatchAndReturnsCommittedRepair()
    {
        var fixture = new Fixture(actorExists: true);

        var result = await fixture.Port.RepairMissingSecretReferenceAsync(
            " agent-1 ",
            " key-1 ",
            CompleteReference(),
            " key-1 ",
            " restore exact durable reference ",
            " admin-1 ",
            1234);

        result.RequestId.Should().NotBeNullOrWhiteSpace();
        result.Admission.Accepted.Should().BeTrue();
        result.Admission.CommandId.Should().Be("repair-command-1");
        result.Outcome.OutcomeCase.Should().Be(
            UserAgentCatalogCredentialRepairOutcome.OutcomeOneofCase.Repaired);
        result.Outcome.Repaired.RequestId.Should().Be(result.RequestId);
        fixture.ObservationBoundBeforeDispatch.Should().BeTrue();
        fixture.DispatchedCommand.Should().NotBeNull();
        fixture.DispatchedCommand!.RequestId.Should().Be(result.RequestId);
        fixture.DispatchedCommand.AgentId.Should().Be("agent-1");
        fixture.DispatchedCommand.ApiKeyId.Should().Be("key-1");
        fixture.DispatchedCommand.SecretSubjectId.Should().Be("key-1");
        fixture.DispatchedCommand.RepairReason.Should().Be("restore exact durable reference");
        fixture.DispatchedCommand.RequestedBySubjectId.Should().Be("admin-1");
        fixture.DispatchedCommand.RepairRequestedAtUnixMs.Should().Be(1234);
        fixture.DispatchedCommand.SecretReference.Should().BeEquivalentTo(CompleteReference());
        fixture.ObservationLease.WaitCalls.Should().Be(1);
    }

    [Fact]
    public async Task RepairMissingSecretReferenceAsync_ReturnsCommittedRejection()
    {
        var fixture = new Fixture(actorExists: true)
        {
            RejectReason = UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict,
        };

        var result = await fixture.Port.RepairMissingSecretReferenceAsync(
            "agent-1",
            "key-1",
            CompleteReference(),
            "key-1",
            "restore exact durable reference",
            "admin-1",
            1234);

        result.Outcome.OutcomeCase.Should().Be(
            UserAgentCatalogCredentialRepairOutcome.OutcomeOneofCase.Rejected);
        result.Outcome.Rejected.RequestId.Should().Be(result.RequestId);
        result.Outcome.Rejected.Reason.Should().Be(
            UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict);
    }

    [Fact]
    public async Task RepairMissingSecretReferenceAsync_CreatesWellKnownActorBeforeObservationAndDispatchWhenMissing()
    {
        var fixture = new Fixture(actorExists: false);

        await fixture.Port.RepairMissingSecretReferenceAsync(
            "agent-1",
            "key-1",
            CompleteReference(),
            "key-1",
            "restore exact durable reference",
            "admin-1",
            1234);

        await fixture.Runtime.Received(1).CreateAsync<UserAgentCatalogGAgent>(
            UserAgentCatalogGAgent.WellKnownId,
            Arg.Any<CancellationToken>());
        fixture.DispatchedCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task OutcomeProjector_PublishesOnlyCorrelationBoundCommittedRepair()
    {
        var eventHub = Substitute.For<IProjectionSessionEventHub<UserAgentCatalogCredentialRepairOutcome>>();
        var projector = new UserAgentCatalogCredentialRepairOutcomeProjector(eventHub);
        var context = new UserAgentCatalogCredentialRepairProjectionContext
        {
            RootActorId = UserAgentCatalogGAgent.WellKnownId,
            ProjectionKind = UserAgentCatalogCredentialRepairObservationPort.ProjectionKind,
            SessionId = "repair-request-1",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new UserAgentCatalogCredentialRevocationRepairedEvent
            {
                RequestId = "different-request",
                AgentId = "agent-1",
                ApiKeyId = "key-1",
            }),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new UserAgentCatalogCredentialRevocationRepairedEvent
            {
                RequestId = "repair-request-1",
                AgentId = "agent-1",
                ApiKeyId = "key-1",
            }),
            CancellationToken.None);

        await eventHub.Received(1).PublishAsync(
            UserAgentCatalogGAgent.WellKnownId,
            "repair-request-1",
            Arg.Is<UserAgentCatalogCredentialRepairOutcome>(outcome =>
                outcome.OutcomeCase == UserAgentCatalogCredentialRepairOutcome.OutcomeOneofCase.Repaired &&
                outcome.Repaired.RequestId == "repair-request-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutcomeProjector_PublishesOnlyCorrelationBoundCommittedRejection()
    {
        var eventHub = Substitute.For<IProjectionSessionEventHub<UserAgentCatalogCredentialRepairOutcome>>();
        var projector = new UserAgentCatalogCredentialRepairOutcomeProjector(eventHub);
        var context = new UserAgentCatalogCredentialRepairProjectionContext
        {
            RootActorId = UserAgentCatalogGAgent.WellKnownId,
            ProjectionKind = UserAgentCatalogCredentialRepairObservationPort.ProjectionKind,
            SessionId = "repair-request-1",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new UserAgentCatalogCredentialRevocationRepairRejectedEvent
            {
                RequestId = "different-request",
                Reason = UserAgentCatalogCredentialRevocationRepairRejectionReason.NotBlocked,
            }),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new UserAgentCatalogTombstonedEvent { AgentId = "agent-1" }),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new UserAgentCatalogCredentialRevocationRepairRejectedEvent
            {
                RequestId = "repair-request-1",
                AgentId = "agent-1",
                ApiKeyId = "key-1",
                Reason = UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict,
            }),
            CancellationToken.None);

        await eventHub.Received(1).PublishAsync(
            UserAgentCatalogGAgent.WellKnownId,
            "repair-request-1",
            Arg.Is<UserAgentCatalogCredentialRepairOutcome>(outcome =>
                outcome.OutcomeCase == UserAgentCatalogCredentialRepairOutcome.OutcomeOneofCase.Rejected &&
                outcome.Rejected.RequestId == "repair-request-1" &&
                outcome.Rejected.Reason ==
                    UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OutcomeCodec_RoundTripsTypedOutcomes(bool repaired)
    {
        var codec = new UserAgentCatalogCredentialRepairOutcomeCodec();
        var outcome = repaired
            ? new UserAgentCatalogCredentialRepairOutcome
            {
                Repaired = new UserAgentCatalogCredentialRevocationRepairedEvent
                {
                    RequestId = "repair-request-1",
                    AgentId = "agent-1",
                },
            }
            : new UserAgentCatalogCredentialRepairOutcome
            {
                Rejected = new UserAgentCatalogCredentialRevocationRepairRejectedEvent
                {
                    RequestId = "repair-request-1",
                    Reason = UserAgentCatalogCredentialRevocationRepairRejectionReason.NotBlocked,
                },
            };

        var eventType = codec.GetEventType(outcome);
        var payload = codec.Serialize(outcome);
        var roundTripped = codec.Deserialize(eventType, payload);

        roundTripped.Should().BeEquivalentTo(outcome);
    }

    [Fact]
    public void OutcomeCodec_RejectsMismatchedEmptyAndMalformedPayloads()
    {
        var codec = new UserAgentCatalogCredentialRepairOutcomeCodec();
        var outcome = new UserAgentCatalogCredentialRepairOutcome
        {
            Repaired = new UserAgentCatalogCredentialRevocationRepairedEvent
            {
                RequestId = "repair-request-1",
            },
        };

        codec.Deserialize("Rejected", codec.Serialize(outcome)).Should().BeNull();
        codec.Deserialize("Repaired", ByteString.Empty).Should().BeNull();
        codec.Deserialize("Repaired", ByteString.CopyFromUtf8("not-protobuf")).Should().BeNull();
    }

    [Fact]
    public void OutcomeCodec_RejectsNullEvents()
    {
        var codec = new UserAgentCatalogCredentialRepairOutcomeCodec();

        var getEventType = () => codec.GetEventType(null!);
        var serialize = () => codec.Serialize(null!);

        getEventType.Should().Throw<ArgumentNullException>();
        serialize.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddScheduledAgents_ResolvesCredentialRepairObservationChain()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IActorRuntime>());
        services.AddSingleton(Substitute.For<IActorDispatchPort>());
        services.AddSingleton(Substitute.For<IStreamProvider>());
        services.AddSingleton(Substitute.For<IStreamForwardingRegistry>());
        services.AddSingleton(Substitute.For<IStreamForwardingBindingAuthority>());
        services.AddScheduledAgents();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProjectionSessionEventCodec<UserAgentCatalogCredentialRepairOutcome>>()
            .Should().BeOfType<UserAgentCatalogCredentialRepairOutcomeCodec>();
        provider.GetServices<IProjectionProjector<UserAgentCatalogCredentialRepairProjectionContext>>()
            .Should().ContainSingle()
            .Which.Should().BeOfType<UserAgentCatalogCredentialRepairOutcomeProjector>();
        provider.GetRequiredService<IUserAgentCatalogCredentialRepairObservationPort>()
            .Should().BeOfType<UserAgentCatalogCredentialRepairObservationPort>();
        provider.GetRequiredService<IUserAgentCatalogCredentialRepairPort>()
            .Should().BeOfType<UserAgentCatalogCredentialRepairPort>();
    }

    [Fact]
    public async Task ObservationPort_ActivatesAndSubscribesBeforeCompletingTypedOutcome()
    {
        var context = new UserAgentCatalogCredentialRepairProjectionContext
        {
            RootActorId = UserAgentCatalogGAgent.WellKnownId,
            ProjectionKind = UserAgentCatalogCredentialRepairObservationPort.ProjectionKind,
            SessionId = "repair-request-1",
        };
        var runtimeLease = new UserAgentCatalogCredentialRepairRuntimeLease(context);
        var activation = new RecordingActivationService(runtimeLease);
        var release = new RecordingReleaseService();
        var eventHub = new RecordingSessionEventHub();
        var observationPort = new UserAgentCatalogCredentialRepairObservationPort(
            activation,
            release,
            eventHub);

        await using (var observation = await observationPort.BindAsync("repair-request-1"))
        {
            eventHub.Handler.Should().NotBeNull();
            var wait = observation.WaitAsync();
            await eventHub.Handler!(new UserAgentCatalogCredentialRepairOutcome
            {
                Repaired = new UserAgentCatalogCredentialRevocationRepairedEvent
                {
                    RequestId = "repair-request-1",
                },
            });

            var outcome = await wait;
            outcome.Repaired.RequestId.Should().Be("repair-request-1");
        }

        activation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProjectionScopeStartRequest
            {
                RootActorId = UserAgentCatalogGAgent.WellKnownId,
                ProjectionKind = UserAgentCatalogCredentialRepairObservationPort.ProjectionKind,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = "repair-request-1",
            });
        eventHub.RootActorId.Should().Be(UserAgentCatalogGAgent.WellKnownId);
        eventHub.SessionId.Should().Be("repair-request-1");
        eventHub.Subscription.DisposeCalls.Should().Be(1);
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(runtimeLease);
    }

    [Fact]
    public async Task ObservationLease_ReleasesProjectionLeaseWhenSubscriptionDisposalFails()
    {
        var context = new UserAgentCatalogCredentialRepairProjectionContext
        {
            RootActorId = UserAgentCatalogGAgent.WellKnownId,
            ProjectionKind = UserAgentCatalogCredentialRepairObservationPort.ProjectionKind,
            SessionId = "repair-request-1",
        };
        var runtimeLease = new UserAgentCatalogCredentialRepairRuntimeLease(context);
        var activation = new RecordingActivationService(runtimeLease);
        var release = new RecordingReleaseService();
        var eventHub = new RecordingSessionEventHub(throwOnDispose: true);
        var observationPort = new UserAgentCatalogCredentialRepairObservationPort(
            activation,
            release,
            eventHub);
        var observation = await observationPort.BindAsync("repair-request-1");

        Func<Task> dispose = async () => await observation.DisposeAsync();

        await dispose.Should().ThrowAsync<InvalidOperationException>();
        eventHub.Subscription.DisposeCalls.Should().Be(1);
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(runtimeLease);
    }

    [Fact]
    public async Task ObservationPort_WhenSubscriptionFails_ReleasesActivatedProjectionLeaseOnce()
    {
        var context = new UserAgentCatalogCredentialRepairProjectionContext
        {
            RootActorId = UserAgentCatalogGAgent.WellKnownId,
            ProjectionKind = UserAgentCatalogCredentialRepairObservationPort.ProjectionKind,
            SessionId = "repair-request-1",
        };
        var runtimeLease = new UserAgentCatalogCredentialRepairRuntimeLease(context);
        var activation = new RecordingActivationService(runtimeLease);
        var release = new RecordingReleaseService();
        var eventHub = new RecordingSessionEventHub(throwOnSubscribe: true);
        var observationPort = new UserAgentCatalogCredentialRepairObservationPort(
            activation,
            release,
            eventHub);

        var bind = () => observationPort.BindAsync("repair-request-1");

        await bind.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("subscription failed");
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(runtimeLease);
        eventHub.Subscription.DisposeCalls.Should().Be(0);
    }

    private static SecretReference CompleteReference() => new()
    {
        Ref = "secret-1",
        Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
        OwnerScopeKey = "scheduled-agent:key-1",
        Version = 1,
        Fingerprint = "sha256:test",
    };

    private static EventEnvelope CommittedEnvelope(Google.Protobuf.IMessage domainEvent)
    {
        var occurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = occurredAt,
            Route = EnvelopeRouteSemantics.CreateObserverPublication(UserAgentCatalogGAgent.WellKnownId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = 1,
                    Timestamp = occurredAt,
                    EventData = Any.Pack(domainEvent),
                },
                StateRoot = Any.Pack(new UserAgentCatalogState()),
            }),
        };
    }

    private sealed class RecordingActivationService(UserAgentCatalogCredentialRepairRuntimeLease lease)
        : IProjectionScopeActivationService<UserAgentCatalogCredentialRepairRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<UserAgentCatalogCredentialRepairRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(lease);
        }
    }

    private sealed class RecordingReleaseService
        : IProjectionScopeReleaseService<UserAgentCatalogCredentialRepairRuntimeLease>
    {
        public List<UserAgentCatalogCredentialRepairRuntimeLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(
            UserAgentCatalogCredentialRepairRuntimeLease lease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Leases.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionEventHub(
        bool throwOnDispose = false,
        bool throwOnSubscribe = false)
        : IProjectionSessionEventHub<UserAgentCatalogCredentialRepairOutcome>
    {
        public string? RootActorId { get; private set; }
        public string? SessionId { get; private set; }
        public Func<UserAgentCatalogCredentialRepairOutcome, ValueTask>? Handler { get; private set; }
        public RecordingSubscription Subscription { get; } = new(throwOnDispose);

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            UserAgentCatalogCredentialRepairOutcome evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<UserAgentCatalogCredentialRepairOutcome, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (throwOnSubscribe)
            {
                return Task.FromException<IAsyncDisposable>(
                    new InvalidOperationException("subscription failed"));
            }

            RootActorId = rootActorId;
            SessionId = sessionId;
            Handler = handler;
            return Task.FromResult<IAsyncDisposable>(Subscription);
        }
    }

    private sealed class RecordingSubscription(bool throwOnDispose) : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return throwOnDispose
                ? ValueTask.FromException(new InvalidOperationException("subscription disposal failed"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        private string _requestId = string.Empty;

        public Fixture(bool actorExists)
        {
            var actor = Substitute.For<IActor>();
            Runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
                .Returns(actorExists ? actor : null);
            Runtime.CreateAsync<UserAgentCatalogGAgent>(
                    UserAgentCatalogGAgent.WellKnownId,
                    Arg.Any<CancellationToken>())
                .Returns(actor);
            ObservationLease = new RecordingObservationLease(this);
            Observation = new RecordingObservationPort(this);
            Dispatch.DispatchAsync(
                    UserAgentCatalogGAgent.WellKnownId,
                    Arg.Any<EventEnvelope>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    ObservationBoundBeforeDispatch = ObservationBound;
                    var envelope = call.ArgAt<EventEnvelope>(1);
                    DispatchedCommand = envelope.Payload.Unpack<UserAgentCatalogRepairCredentialRevocationCommand>();
                    return new DispatchAdmission(
                        true,
                        "repair-command-1",
                        DateTimeOffset.UtcNow,
                        UserAgentCatalogGAgent.WellKnownId,
                        "repair-command-1");
                });

            Port = new UserAgentCatalogCredentialRepairPort(Runtime, Dispatch, Observation);
        }

        public UserAgentCatalogCredentialRevocationRepairRejectionReason? RejectReason { get; init; }
        public IActorRuntime Runtime { get; } = Substitute.For<IActorRuntime>();
        public IActorDispatchPort Dispatch { get; } = Substitute.For<IActorDispatchPort>();
        public RecordingObservationPort Observation { get; }
        public RecordingObservationLease ObservationLease { get; }
        public UserAgentCatalogCredentialRepairPort Port { get; }
        public UserAgentCatalogRepairCredentialRevocationCommand? DispatchedCommand { get; private set; }
        public bool ObservationBound { get; private set; }
        public bool ObservationBoundBeforeDispatch { get; private set; }

        private UserAgentCatalogCredentialRepairOutcome BuildOutcome() =>
            RejectReason.HasValue
                ? new UserAgentCatalogCredentialRepairOutcome
                {
                    Rejected = new UserAgentCatalogCredentialRevocationRepairRejectedEvent
                    {
                        RequestId = _requestId,
                        AgentId = "agent-1",
                        ApiKeyId = "key-1",
                        Reason = RejectReason.Value,
                    },
                }
                : new UserAgentCatalogCredentialRepairOutcome
                {
                    Repaired = new UserAgentCatalogCredentialRevocationRepairedEvent
                    {
                        RequestId = _requestId,
                        AgentId = "agent-1",
                        ApiKeyId = "key-1",
                    },
                };

        public sealed class RecordingObservationPort(Fixture owner)
            : IUserAgentCatalogCredentialRepairObservationPort
        {
            public Task<IUserAgentCatalogCredentialRepairObservationLease> BindAsync(
                string requestId,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                owner._requestId = requestId;
                owner.ObservationBound = true;
                return Task.FromResult<IUserAgentCatalogCredentialRepairObservationLease>(
                    owner.ObservationLease);
            }
        }

        public sealed class RecordingObservationLease(Fixture owner)
            : IUserAgentCatalogCredentialRepairObservationLease
        {
            public int WaitCalls { get; private set; }

            public Task<UserAgentCatalogCredentialRepairOutcome> WaitAsync(
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                WaitCalls++;
                return Task.FromResult(owner.BuildOutcome());
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
