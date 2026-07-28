using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowResultDeliveryRepairObservationTests
{
    [Fact]
    public void Capability_ResolvesCommittedRegistrationState()
    {
        ChannelWorkflowResultDeliveryCapability.Resolve(Registration(credential: DeliveryReference()))
            .Should().Be(ChannelWorkflowResultDeliveryCapabilityStatus.Enabled);
        ChannelWorkflowResultDeliveryCapability.Resolve(Registration())
            .Should().Be(ChannelWorkflowResultDeliveryCapabilityStatus.RepairRequired);
        ChannelWorkflowResultDeliveryCapability.Resolve(Registration(
                repair: Repair(ChannelWorkflowResultDeliveryRepairStatus.Requested)))
            .Should().Be(ChannelWorkflowResultDeliveryCapabilityStatus.Repairing);
        ChannelWorkflowResultDeliveryCapability.Resolve(Registration(
                repair: Repair(ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared)))
            .Should().Be(ChannelWorkflowResultDeliveryCapabilityStatus.Repairing);
        ChannelWorkflowResultDeliveryCapability.Resolve(Registration(
                credential: DeliveryReference(),
                repair: Repair(ChannelWorkflowResultDeliveryRepairStatus.Failed)))
            .Should().Be(ChannelWorkflowResultDeliveryCapabilityStatus.RepairFailed);
    }

    [Fact]
    public void Capability_RequiresTypedPurposeAndOwnerScope()
    {
        var wrongPurpose = DeliveryReference();
        wrongPurpose.Purpose = CredentialSecretPurposes.ScheduledNyxApiKey;
        var wrongOwner = DeliveryReference();
        wrongOwner.OwnerScopeKey = "scope-beta";

        ChannelWorkflowResultDeliveryCapability.IsEnabled(Registration(credential: wrongPurpose))
            .Should().BeFalse();
        ChannelWorkflowResultDeliveryCapability.IsEnabled(Registration(credential: wrongOwner))
            .Should().BeFalse();
    }

    [Fact]
    public async Task OutcomeProjector_PublishesEveryMatchingCommittedRepairOutcomeOnly()
    {
        var eventHub = Substitute.For<
            IProjectionSessionEventHub<ChannelBotWorkflowResultDeliveryRepairOutcome>>();
        var projector = new ChannelWorkflowResultDeliveryRepairOutcomeProjector(eventHub);
        var context = Context("repair-alpha");

        foreach (var sample in DomainEventSamples("different-request"))
            await projector.ProjectAsync(context, CommittedEnvelope(sample), CancellationToken.None);
        foreach (var sample in DomainEventSamples("repair-alpha"))
            await projector.ProjectAsync(context, CommittedEnvelope(sample), CancellationToken.None);
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new ChannelBotInboundObservedEvent { RegistrationId = "reg-alpha" }),
            CancellationToken.None);

        var published = eventHub.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(eventHub.PublishAsync))
            .Select(call => (ChannelBotWorkflowResultDeliveryRepairOutcome)call.GetArguments()[2]!)
            .ToArray();
        published.Select(static outcome => outcome.OutcomeCase).Should().Equal(
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Requested,
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Prepared,
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Completed,
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Failed,
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Rejected);
        await eventHub.Received(5).PublishAsync(
            ChannelBotRegistrationGAgent.WellKnownId,
            "repair-alpha",
            Arg.Any<ChannelBotWorkflowResultDeliveryRepairOutcome>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OutcomeCodec_RoundTripsEveryCaseAndRejectsMismatchedPayloads()
    {
        var codec = new ChannelWorkflowResultDeliveryRepairOutcomeCodec();

        foreach (var outcome in OutcomeSamples("repair-alpha"))
        {
            var eventType = codec.GetEventType(outcome);
            codec.Deserialize(eventType, codec.Serialize(outcome)).Should().Be(outcome);
            codec.Deserialize("Unspecified", codec.Serialize(outcome)).Should().BeNull();
        }

        codec.Deserialize("Requested", ByteString.Empty).Should().BeNull();
        codec.Deserialize("Requested", ByteString.CopyFromUtf8("not-protobuf")).Should().BeNull();
    }

    [Fact]
    public async Task ObservationLease_WaitsForExpectedCaseAndReturnsRejectionImmediately()
    {
        var runtimeLease = new ChannelWorkflowResultDeliveryRepairRuntimeLease(Context("repair-alpha"));
        var activation = new RecordingActivationService(runtimeLease);
        var release = new RecordingReleaseService();
        var eventHub = new RecordingEventHub();
        var port = new ChannelWorkflowResultDeliveryRepairObservationPort(activation, release, eventHub);

        await using (var lease = await port.BindAsync(" repair-alpha "))
        {
            var preparedWait = lease.WaitAsync(
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Prepared);
            await eventHub.PublishToSubscriberAsync(OutcomeSamples("repair-alpha")[0]);
            await eventHub.PublishToSubscriberAsync(OutcomeSamples("repair-alpha")[1]);
            (await preparedWait).OutcomeCase.Should().Be(
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Prepared);
        }

        activation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProjectionScopeStartRequest
            {
                RootActorId = ChannelBotRegistrationGAgent.WellKnownId,
                ProjectionKind = ChannelWorkflowResultDeliveryRepairObservationPort.ProjectionKind,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = "repair-alpha",
            });
        eventHub.Subscription.DisposeCalls.Should().Be(1);
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(runtimeLease);

        var secondRuntimeLease = new ChannelWorkflowResultDeliveryRepairRuntimeLease(Context("repair-beta"));
        var secondHub = new RecordingEventHub();
        var secondPort = new ChannelWorkflowResultDeliveryRepairObservationPort(
            new RecordingActivationService(secondRuntimeLease),
            new RecordingReleaseService(),
            secondHub);
        await using var secondLease = await secondPort.BindAsync("repair-beta");
        var completedWait = secondLease.WaitAsync(
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Completed);
        await secondHub.PublishToSubscriberAsync(OutcomeSamples("repair-beta")[4]);
        (await completedWait).OutcomeCase.Should().Be(
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Rejected);
    }

    [Fact]
    public async Task ObservationLease_HonorsCancellationWithoutPolling()
    {
        var runtimeLease = new ChannelWorkflowResultDeliveryRepairRuntimeLease(Context("repair-alpha"));
        var port = new ChannelWorkflowResultDeliveryRepairObservationPort(
            new RecordingActivationService(runtimeLease),
            new RecordingReleaseService(),
            new RecordingEventHub());
        await using var lease = await port.BindAsync("repair-alpha");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var wait = () => lease.WaitAsync(
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Completed,
            cancellation.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void AddChannelRuntime_RegistersRepairObservationChain()
    {
        var services = new ServiceCollection();

        services.AddChannelRuntime();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionSessionEventCodec<ChannelBotWorkflowResultDeliveryRepairOutcome>) &&
            descriptor.ImplementationType == typeof(ChannelWorkflowResultDeliveryRepairOutcomeCodec));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionProjector<ChannelWorkflowResultDeliveryRepairProjectionContext>) &&
            descriptor.ImplementationType == typeof(ChannelWorkflowResultDeliveryRepairOutcomeProjector));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelWorkflowResultDeliveryRepairObservationPort) &&
            descriptor.ImplementationType == typeof(ChannelWorkflowResultDeliveryRepairObservationPort));
    }

    private static ChannelBotRegistrationEntry Registration(
        SecretReference? credential = null,
        ChannelWorkflowResultDeliveryRepairState? repair = null) =>
        new()
        {
            Id = "reg-alpha",
            Platform = "lark",
            ScopeId = "scope-alpha",
            NyxAgentApiKeyId = "key-old-alpha",
            WorkflowResultDeliveryCredential = credential,
            WorkflowResultDeliveryRepair = repair,
        };

    private static SecretReference DeliveryReference() =>
        new()
        {
            Ref = "sec-alpha",
            Purpose = CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = "scope-alpha",
            Version = 1,
        };

    private static ChannelWorkflowResultDeliveryRepairState Repair(
        ChannelWorkflowResultDeliveryRepairStatus status,
        string requestId = "repair-alpha") =>
        new()
        {
            RequestId = requestId,
            Status = status,
            ExpectedApiKeyId = "key-old-alpha",
            ExpectedConversationRouteId = "route-alpha",
            RequestedBySubjectId = "user-alpha",
            RequestedAtUnixMs = 1784563200000,
            UpdatedAtUnixMs = 1784563200000,
        };

    private static ChannelWorkflowResultDeliveryRepairProjectionContext Context(string requestId) =>
        new()
        {
            RootActorId = ChannelBotRegistrationGAgent.WellKnownId,
            ProjectionKind = ChannelWorkflowResultDeliveryRepairObservationPort.ProjectionKind,
            SessionId = requestId,
        };

    private static IMessage[] DomainEventSamples(string requestId) =>
    [
        new ChannelBotWorkflowResultDeliveryRepairRequestedEvent
        {
            RegistrationId = "reg-alpha",
            Repair = Repair(ChannelWorkflowResultDeliveryRepairStatus.Requested, requestId),
        },
        new ChannelBotWorkflowResultDeliveryRepairPreparedEvent
        {
            RegistrationId = "reg-alpha",
            Repair = Repair(ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared, requestId),
        },
        new ChannelBotWorkflowResultDeliveryRepairCompletedEvent
        {
            RegistrationId = "reg-alpha",
            RequestId = requestId,
        },
        new ChannelBotWorkflowResultDeliveryRepairFailedEvent
        {
            RegistrationId = "reg-alpha",
            Repair = Repair(ChannelWorkflowResultDeliveryRepairStatus.Failed, requestId),
        },
        new ChannelBotWorkflowResultDeliveryRepairRejectedEvent
        {
            RegistrationId = "reg-alpha",
            RequestId = requestId,
            Phase = ChannelWorkflowResultDeliveryRepairPhase.RequestAdmission,
            Reason = ChannelWorkflowResultDeliveryRepairFailureReason.RequestConflict,
        },
    ];

    private static ChannelBotWorkflowResultDeliveryRepairOutcome[] OutcomeSamples(string requestId)
    {
        var events = DomainEventSamples(requestId);
        return
        [
            new() { Requested = (ChannelBotWorkflowResultDeliveryRepairRequestedEvent)events[0] },
            new() { Prepared = (ChannelBotWorkflowResultDeliveryRepairPreparedEvent)events[1] },
            new() { Completed = (ChannelBotWorkflowResultDeliveryRepairCompletedEvent)events[2] },
            new() { Failed = (ChannelBotWorkflowResultDeliveryRepairFailedEvent)events[3] },
            new() { Rejected = (ChannelBotWorkflowResultDeliveryRepairRejectedEvent)events[4] },
        ];
    }

    private static EventEnvelope CommittedEnvelope(IMessage domainEvent)
    {
        var occurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = occurredAt,
            Route = EnvelopeRouteSemantics.CreateObserverPublication(
                ChannelBotRegistrationGAgent.WellKnownId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = 1,
                    Timestamp = occurredAt,
                    EventData = Any.Pack(domainEvent),
                },
                StateRoot = Any.Pack(new ChannelBotRegistrationStoreState()),
            }),
        };
    }

    private sealed class RecordingActivationService(
        ChannelWorkflowResultDeliveryRepairRuntimeLease lease)
        : IProjectionScopeActivationService<ChannelWorkflowResultDeliveryRepairRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<ChannelWorkflowResultDeliveryRepairRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(lease);
        }
    }

    private sealed class RecordingReleaseService
        : IProjectionScopeReleaseService<ChannelWorkflowResultDeliveryRepairRuntimeLease>
    {
        public List<ChannelWorkflowResultDeliveryRepairRuntimeLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(
            ChannelWorkflowResultDeliveryRepairRuntimeLease lease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Leases.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventHub
        : IProjectionSessionEventHub<ChannelBotWorkflowResultDeliveryRepairOutcome>
    {
        private Func<ChannelBotWorkflowResultDeliveryRepairOutcome, ValueTask>? _handler;

        public RecordingSubscription Subscription { get; } = new();

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            ChannelBotWorkflowResultDeliveryRepairOutcome evt,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<ChannelBotWorkflowResultDeliveryRepairOutcome, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _handler = handler;
            return Task.FromResult<IAsyncDisposable>(Subscription);
        }

        public async ValueTask PublishToSubscriberAsync(
            ChannelBotWorkflowResultDeliveryRepairOutcome outcome)
        {
            _handler.Should().NotBeNull();
            await _handler!(outcome);
        }
    }

    private sealed class RecordingSubscription : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
