using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Abstractions.TypeSystem;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeRelayLifecycleTests
{
    private const string RootActorId = "root-actor";

    [Fact]
    public async Task DurableActiveScope_Deactivation_ShouldRetainRelay()
    {
        var (agent, stream) = CreateAgent(
            "projection-scope-durable-deactivate",
            ProjectionRuntimeMode.DurableMaterialization);

        await agent.DeactivateForTestAsync();

        stream.RemovedRelayTargetIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionScope_Deactivation_ShouldRemoveRelay()
    {
        var (agent, stream) = CreateAgent(
            "projection-scope-session-deactivate",
            ProjectionRuntimeMode.SessionObservation);

        await agent.DeactivateForTestAsync();

        stream.RemovedRelayTargetIds.Should().Equal("projection-scope-session-deactivate");
    }

    [Fact]
    public async Task DurableActiveScope_ExplicitRelease_ShouldRemoveRelay()
    {
        var (agent, stream) = CreateAgent(
            "projection-scope-durable-release",
            ProjectionRuntimeMode.DurableMaterialization);

        await agent.HandleReleaseAsync(new ReleaseProjectionScopeCommand());

        stream.RemovedRelayTargetIds.Should().Equal("projection-scope-durable-release");
        agent.State.Released.Should().BeTrue();
    }

    [Fact]
    public async Task DurableReleasedScope_ReplayedAfterReleaseCommit_ShouldRetryRelayRemovalWithoutAnotherCommit()
    {
        var (agent, stream) = CreateAgent(
            "projection-scope-release-recovery",
            ProjectionRuntimeMode.DurableMaterialization);
        var eventSourcing = (LifecycleEventSourcing)agent.EventSourcing!;
        agent.State.Released = true;
        agent.State.ObservationAttached = false;
        agent.State.ReleasedAtObservedVersion = 7;

        await agent.HandleReleaseAsync(new ReleaseProjectionScopeCommand());

        stream.RemovedRelayTargetIds.Should().Equal("projection-scope-release-recovery");
        eventSourcing.PersistedEvents.OfType<ProjectionScopeReleasedEvent>().Should().BeEmpty();
        agent.State.ReleasedAtObservedVersion.Should().Be(7);
    }

    [Fact]
    public async Task LegacyActiveScope_Ensure_ShouldPersistOneGenerationMigrationAndPublishExactRelay()
    {
        var (agent, stream) = CreateAgent(
            "projection-scope-legacy",
            ProjectionRuntimeMode.DurableMaterialization);
        var eventSourcing = (LifecycleEventSourcing)agent.EventSourcing!;

        await agent.HandleEnsureAsync(new EnsureProjectionScopeCommand { RootActorId = RootActorId });
        await agent.HandleEnsureAsync(new EnsureProjectionScopeCommand { RootActorId = RootActorId });

        agent.State.ActivationGeneration.Should().Be(1);
        eventSourcing.PersistedEvents.OfType<ProjectionScopeActivationGenerationMigratedEvent>()
            .Should().ContainSingle();
        var relay = stream.UpsertedRelays.Should().HaveCount(2).And.Subject.Last();
        relay.TargetActorKind.Should().Be("projection.test-scope");
        relay.ActivationGeneration.Should().Be(1);
    }

    [Fact]
    public async Task ReleasedScope_Ensure_ShouldIncrementActivationGeneration()
    {
        var (agent, stream) = CreateAgent(
            "projection-scope-restarted",
            ProjectionRuntimeMode.DurableMaterialization);
        agent.State.Released = true;
        agent.State.ActivationGeneration = 3;

        await agent.HandleEnsureAsync(new EnsureProjectionScopeCommand
        {
            RootActorId = RootActorId,
            ProjectionKind = "test-kind",
            Mode = ProjectionScopeMode.DurableMaterialization,
        });

        agent.State.ActivationGeneration.Should().Be(4);
        stream.UpsertedRelays.Should().ContainSingle()
            .Which.ActivationGeneration.Should().Be(4);
    }

    [Fact]
    public async Task DurableReleasedScope_Activation_ShouldRemoveStaleRelay()
    {
        var (agent, stream) = CreateAgent(
            "projection-scope-release-reactivation",
            ProjectionRuntimeMode.DurableMaterialization);
        agent.State.Released = true;

        await agent.ActivateForTestAsync();

        stream.RemovedRelayTargetIds.Should().Equal("projection-scope-release-reactivation");
        stream.UpsertedRelays.Should().BeEmpty();
    }

    [Fact]
    public async Task InactiveScope_Activation_ShouldRemoveStaleRelay()
    {
        var (agent, stream) = CreateAgent(
            "projection-scope-inactive-reactivation",
            ProjectionRuntimeMode.DurableMaterialization);
        agent.State.Active = false;
        agent.State.ObservationAttached = false;

        await agent.ActivateForTestAsync();

        stream.RemovedRelayTargetIds.Should().Equal("projection-scope-inactive-reactivation");
        stream.UpsertedRelays.Should().BeEmpty();
    }

    private static (LifecycleScopeAgent Agent, RecordingStream Stream) CreateAgent(
        string scopeId,
        ProjectionRuntimeMode runtimeMode)
    {
        var stream = new RecordingStream(RootActorId);
        var agent = new LifecycleScopeAgent(runtimeMode)
        {
            EventSourcing = new LifecycleEventSourcing(),
            Services = new ServiceCollection()
                .AddSingleton<IStreamProvider>(new SingleStreamProvider(stream))
                .AddSingleton<IAgentKindRegistry>(new AgentKindRegistry(
                [
                    new AgentRegistration(
                        "projection.test-scope",
                        typeof(LifecycleScopeAgent),
                        typeof(ProjectionScopeState)),
                ]))
                .BuildServiceProvider(),
        };

        typeof(GAgentBase)
            .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(agent, scopeId);
        agent.State.RootActorId = RootActorId;
        agent.State.ProjectionKind = "test-kind";
        agent.State.Active = true;
        agent.State.Released = false;
        agent.State.ObservationAttached = true;
        return (agent, stream);
    }

    private sealed class LifecycleScopeAgent(ProjectionRuntimeMode runtimeMode)
        : ProjectionScopeGAgentBase<LifecycleContext>
    {
        protected override ProjectionRuntimeMode RuntimeMode { get; } = runtimeMode;

        public Task DeactivateForTestAsync() => OnDeactivateAsync(CancellationToken.None);

        public Task ActivateForTestAsync() => OnActivateAsync(CancellationToken.None);

        protected override ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
            LifecycleContext context,
            EventEnvelope envelope,
            CancellationToken ct) =>
            ValueTask.FromResult(ProjectionScopeDispatchResult.Skip());
    }

    private sealed record LifecycleContext(string RootActorId, string ProjectionKind)
        : IProjectionMaterializationContext;

    private sealed class SingleStreamProvider(RecordingStream stream) : IStreamProvider
    {
        public IStream GetStream(string actorId)
        {
            actorId.Should().Be(stream.StreamId);
            return stream;
        }
    }

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId { get; } = streamId;
        public List<string> RemovedRelayTargetIds { get; } = [];
        public List<StreamForwardingBinding> UpsertedRelays { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage => throw new NotSupportedException();

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new() => throw new NotSupportedException();

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            UpsertedRelays.Add(binding);
            return Task.CompletedTask;
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RemovedRelayTargetIds.Add(targetStreamId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class LifecycleEventSourcing : IEventSourcingBehavior<ProjectionScopeState>
    {
        private readonly List<IMessage> _pending = [];
        public List<IMessage> PersistedEvents { get; } = [];
        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
                PersistedEvents.Add(evt);
                result.CommittedEvents.Add(new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = ++CurrentVersion,
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                });
            }

            result.LatestVersion = CurrentVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(ProjectionScopeState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ProjectionScopeState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<ProjectionScopeState?>(null);

        public void DiscardPendingEvents() => _pending.Clear();

        public ProjectionScopeState TransitionState(ProjectionScopeState current, IMessage evt) => evt switch
        {
            ProjectionScopeStartedEvent started => ProjectionScopeStateApplier.ApplyStarted(current, started),
            ProjectionScopeActivationGenerationMigratedEvent migrated =>
                ProjectionScopeStateApplier.ApplyActivationGenerationMigrated(current, migrated),
            ProjectionObservationAttachmentUpdatedEvent attached =>
                ProjectionScopeStateApplier.ApplyAttachmentUpdated(current, attached),
            ProjectionScopeReleasedEvent released => ProjectionScopeStateApplier.ApplyReleased(current, released),
            _ => current,
        };
    }
}
