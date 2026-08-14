using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
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

        public Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage => throw new NotSupportedException();

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new() => throw new NotSupportedException();

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
            throw new NotSupportedException();

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
        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
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

        public ProjectionScopeState TransitionState(ProjectionScopeState current, IMessage evt) =>
            evt is ProjectionScopeReleasedEvent released
                ? ProjectionScopeStateApplier.ApplyReleased(current, released)
                : current;
    }
}
