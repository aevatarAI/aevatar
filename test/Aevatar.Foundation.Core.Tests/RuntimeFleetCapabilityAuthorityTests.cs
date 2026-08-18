using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Foundation.Runtime.Callbacks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RuntimeFleetCapabilityAuthorityTests
{
    [Fact]
    public async Task Reconcile_AcrossRestart_ShouldOpenRevokeAndReopenWithMonotonicEpoch()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var first = await fixture.CreateAuthorityAsync();

        await first.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        fixture.Membership.Current = fixture.CreateMembership(epoch: 2, supportsCapability: false);
        await first.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));
        await first.DeactivateAsync();

        fixture.Membership.Current = fixture.CreateMembership(epoch: 3, supportsCapability: true);
        var restarted = await fixture.CreateAuthorityAsync();
        await restarted.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        var gates = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data =>
                data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor) ||
                data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Select(static data => data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor)
                ? data.Unpack<RuntimeFleetCapabilityGateOpenedEvent>().Gate
                : data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate)
            .Where(static gate =>
                gate.Capability == RuntimeFleetCapability.WorkflowNormalizedStateWritesV1)
            .ToArray();

        gates.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked,
            RuntimeFleetCapabilityGateStatus.Open);
        gates.Select(static gate => gate.CapabilityEpoch).Should().Equal(1, 2, 3);
        gates[^1].MembershipEpoch.Should().Be(3);
    }

    [Fact]
    public async Task Reconcile_DefaultRequirements_ShouldOpenAllManagedCapabilities()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        var opened = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data => data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor))
            .Select(static data => data.Unpack<RuntimeFleetCapabilityGateOpenedEvent>().Gate)
            .ToDictionary(static gate => gate.Capability);

        opened.Keys.Should().BeEquivalentTo(new[]
        {
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
            RuntimeFleetCapability.ProjectionIncrementalGraphV1,
        });
        opened[RuntimeFleetCapability.WorkflowNormalizedStateWritesV1]
            .RequiredContractId.Should().Be(RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1);
        opened[RuntimeFleetCapability.ProjectionScopeStatusTerminalV1]
            .RequiredContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
        opened[RuntimeFleetCapability.ProjectionIncrementalGraphV1]
            .RequiredContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1);
        opened.Values.Should().OnlyContain(static gate =>
            gate.Status == RuntimeFleetCapabilityGateStatus.Open &&
            gate.CapabilityEpoch == 1);
        opened[RuntimeFleetCapability.WorkflowNormalizedStateWritesV1]
            .MinimumReaderContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion);
        opened[RuntimeFleetCapability.ProjectionScopeStatusTerminalV1]
            .MinimumReaderContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);
        opened[RuntimeFleetCapability.ProjectionIncrementalGraphV1]
            .MinimumReaderContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion);
    }

    [Fact]
    public async Task Reconcile_WithoutMatchingRuntimeAttestation_ShouldRejectWithoutCommitting()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();

        var forged = fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1");
        fixture.Attestation.Current = null;
        await FluentActions.Awaiting(() => authority.HandleEventAsync(forged))
            .Should().ThrowAsync<InvalidOperationException>();

        fixture.Attestation.Current = new RuntimeFleetReconcileDeliveryAttestation(
            "another-envelope",
            1,
            1,
            RuntimeCallbackSlotEpoch.OrleansSchedulerV2);
        await FluentActions.Awaiting(() => authority.HandleEventAsync(
                forged))
            .Should().ThrowAsync<InvalidOperationException>();

        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(0);
    }

    [Fact]
    public async Task ArbitraryBusinessPayload_ShouldNeverBypassReconcile()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();
        var envelope = Envelope(
            new StringValue { Value = "forged-open" },
            "business-actor",
            RuntimeFleetCapabilityAuthorityIdentity.ActorId);

        await authority.HandleEventAsync(envelope);
        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_DuplicateShouldBeIdempotent_AndStaleSequenceShouldReject()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();
        var accepted = fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1");

        await authority.HandleEventAsync(accepted);
        var committedVersion = await fixture.Store.GetVersionAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        await authority.HandleEventAsync(accepted.Clone());

        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(committedVersion);
        await FluentActions.Awaiting(() => authority.HandleEventAsync(
                fixture.ReconcileEnvelope(sequence: 1, token: "different-token")))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Reconcile_WhenMembershipSourceFails_ShouldRevokeOpenGate()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        fixture.Membership.Failure = new IOException("membership provider unavailable");
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var revoked = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data => data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Select(static data => data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate)
            .Where(static gate =>
                gate.Capability == RuntimeFleetCapability.WorkflowNormalizedStateWritesV1)
            .Should().ContainSingle().Subject;
        revoked.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Revoked);
        revoked.CapabilityEpoch.Should().Be(2);
        revoked.RevocationReason.Should().Be("membership-source-failed");

        var failureAttempt = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data => data.Is(RuntimeFleetReconciliationRecordedEvent.Descriptor))
            .Select(static data => data.Unpack<RuntimeFleetReconciliationRecordedEvent>())
            .Last();
        failureAttempt.ObservationOutcome.Should()
            .Be(RuntimeFleetMembershipObservationOutcome.SourceFailed);
        failureAttempt.AttemptedAt.Should().Be(Timestamp.FromDateTimeOffset(fixture.Time.GetUtcNow()));

        fixture.Membership.Failure = null;
        fixture.Membership.Current = fixture.CreateMembership(epoch: 2, supportsCapability: true);
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        var gates = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data =>
                data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor) ||
                data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Select(static data => data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor)
                ? data.Unpack<RuntimeFleetCapabilityGateOpenedEvent>().Gate
                : data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate)
            .Where(static gate =>
                gate.Capability == RuntimeFleetCapability.WorkflowNormalizedStateWritesV1)
            .ToArray();
        gates.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked,
            RuntimeFleetCapabilityGateStatus.Open);
        gates.Select(static gate => gate.CapabilityEpoch).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Reconcile_WhenMembershipChangesBeforeGateTransition_ShouldNotOpenGate()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Reads.Enqueue(
            fixture.CreateMembership(epoch: 1, supportsCapability: true));
        fixture.Membership.Reads.Enqueue(
            fixture.CreateMembership(epoch: 2, supportsCapability: true));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        var events = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .ToArray();
        events.Should().NotContain(static data =>
            data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor));
        events.Should().NotContain(static data =>
            data.Is(RuntimeFleetMembershipObservedEvent.Descriptor));
        events.Single(static data =>
                data.Is(RuntimeFleetReconciliationRecordedEvent.Descriptor))
            .Unpack<RuntimeFleetReconciliationRecordedEvent>()
            .ObservationOutcome.Should().Be(
                RuntimeFleetMembershipObservationOutcome.RegressedOrConflicted);
    }

    [Fact]
    public async Task Reconcile_WhenMembershipSourceIsCanceled_ShouldPropagateWithoutRevoking()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        var committedVersion = await fixture.Store.GetVersionAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        fixture.Membership.Failure = new OperationCanceledException("membership read canceled");

        await FluentActions.Awaiting(() => authority.HandleEventAsync(
                fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2")))
            .Should().ThrowAsync<OperationCanceledException>();

        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(committedVersion);
        (await fixture.Store.GetEventsAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Count(static data => data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Should().Be(0);
    }

    [Fact]
    public void Constructor_WhenFreshnessOptionsAreInvalid_ShouldReject()
    {
        var membership = new MutableMembershipSource();
        var scheduler = new RecordingFleetReconcileScheduleOwner();
        var attestation = new MutableFleetReconcileAttestationReader();

        var zeroTtl = () => new RuntimeFleetCapabilityAuthorityGAgent(
            membership,
            scheduler,
            attestation,
            options: new RuntimeFleetCapabilityAuthorityOptions
            {
                MaxMembershipEvidenceTtl = TimeSpan.Zero,
            });
        var negativeSkew = () => new RuntimeFleetCapabilityAuthorityGAgent(
            membership,
            scheduler,
            attestation,
            options: new RuntimeFleetCapabilityAuthorityOptions
            {
                MaxClockSkew = TimeSpan.FromMilliseconds(-1),
            });

        zeroTtl.Should().Throw<ArgumentOutOfRangeException>();
        negativeSkew.Should().Throw<ArgumentOutOfRangeException>();
    }

    private sealed class AuthorityFixture
    {
        internal readonly InMemoryEventStore Store = new();
        internal readonly MutableMembershipSource Membership = new();
        internal readonly RecordingFleetReconcileScheduleOwner Scheduler = new();
        internal readonly MutableFleetReconcileAttestationReader Attestation = new();
        internal readonly ManualTimeProvider Time = new(
            new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero));

        internal async Task<RuntimeFleetCapabilityAuthorityGAgent> CreateAuthorityAsync()
        {
            var agent = new RuntimeFleetCapabilityAuthorityGAgent(
                Membership,
                Scheduler,
                Attestation,
                Time)
            {
                Services = new ServiceCollection()
                    .AddSingleton<IActorRuntimeCallbackScheduler>(
                        new RecordingCallbackScheduler())
                    .BuildServiceProvider(),
                EventSourcingBehaviorFactory =
                    new DefaultEventSourcingBehaviorFactory<RuntimeFleetCapabilityAuthorityState>(Store),
            };
            agent.SetId(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
            await agent.ActivateAsync();
            return agent;
        }

        internal RuntimeFleetMembershipSnapshot CreateMembership(
            long epoch,
            bool supportsCapability)
        {
            var membership = new RuntimeFleetMembershipSnapshot
            {
                MembershipEpoch = epoch,
                DeploymentRevision = $"revision-{epoch}",
                ObservedAt = Timestamp.FromDateTimeOffset(Time.GetUtcNow()),
                ValidUntil = Timestamp.FromDateTimeOffset(Time.GetUtcNow().AddSeconds(30)),
            };
            var member = new RuntimeFleetMember
            {
                MemberId = "silo-a",
                Incarnation = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DeploymentRevision = membership.DeploymentRevision,
            };
            if (supportsCapability)
            {
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                    ReaderContractVersion =
                        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion,
                    ContractId = RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1,
                });
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
                    ReaderContractVersion =
                        RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion,
                    ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
                });
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                    ReaderContractVersion =
                        RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
                    ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
                });
            }
            membership.ActiveMembers.Add(member);
            membership.MembershipDigest = RuntimeFleetMembershipDigest.Compute(membership);
            return membership;
        }

        internal EventEnvelope ReconcileEnvelope(long sequence, string token)
        {
            var envelope = Envelope(
                new RuntimeFleetReconcileRequested(),
                "untrusted-publisher",
                RuntimeFleetCapabilityAuthorityIdentity.ActorId);
            envelope.Id = $"reconcile-{sequence}-{token}";
            var callback = envelope.EnsureRuntime().EnsureCallback();
            callback.CallbackId = RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId;
            callback.Generation = 1;
            callback.FireIndex = sequence;
            callback.SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2;
            Attestation.Current = new RuntimeFleetReconcileDeliveryAttestation(
                envelope.Id,
                callback.Generation,
                callback.FireIndex,
                callback.SlotEpoch);
            return envelope;
        }
    }

    private sealed class MutableMembershipSource : IRuntimeFleetMembershipSnapshotSource
    {
        internal RuntimeFleetMembershipSnapshot? Current { get; set; }

        internal Queue<RuntimeFleetMembershipSnapshot?> Reads { get; } = new();

        internal Exception? Failure { get; set; }

        public Task<RuntimeFleetMembershipSnapshot?> GetCurrentAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null)
                throw Failure;
            var membership = Reads.Count == 0 ? Current : Reads.Dequeue();
            return Task.FromResult(membership?.Clone());
        }
    }

    private sealed class RecordingFleetReconcileScheduleOwner
        : IRuntimeFleetReconcileScheduleOwner
    {
        internal int EnsureCount { get; private set; }

        public Task EnsureScheduledAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableFleetReconcileAttestationReader
        : IRuntimeFleetReconcileDeliveryAttestationReader
    {
        public RuntimeFleetReconcileDeliveryAttestation? Current { get; set; }
    }

    internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private long _generation;

        internal List<(RuntimeCallbackTimeoutRequest Request, RuntimeCallbackLease Lease)> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var lease = new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                ++_generation,
                RuntimeCallbackBackend.Dedicated)
            {
                SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
            };
            Timeouts.Add((request, lease));
            return Task.FromResult(lease);
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(
            string actorId,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class RecordingPublisher : IEventPublisher
    {
        internal List<(string TargetActorId, IMessage Message)> Sent { get; } = [];

        internal Exception? Failure { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null)
                throw Failure;
            Sent.Add((targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    internal static IServiceProvider BuildServices(IActorRuntimeCallbackScheduler scheduler) =>
        new ServiceCollection()
            .AddSingleton(scheduler)
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .BuildServiceProvider();

    internal static EventEnvelope Envelope(
        IMessage payload,
        string publisherActorId,
        string targetActorId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, targetActorId),
        };
}
