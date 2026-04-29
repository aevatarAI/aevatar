using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeGAgentBaseTests
{
    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldPropagate_RetryableOptimisticConcurrencyException()
    {
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-retry",
            onProcess: _ => throw new EventStoreOptimisticConcurrencyException("root-1", 3, 4));

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-retry");

        Func<Task> act = () => agent.HandleObservedEnvelopeAsync(envelope);

        await act.Should().ThrowAsync<EventStoreOptimisticConcurrencyException>();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldDiscardPendingEvents_WhenOccIsThrown()
    {
        var es = new TrackingEventSourcing();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-occ-discard",
            onProcess: _ => throw new EventStoreOptimisticConcurrencyException("root-1", 3, 4),
            eventSourcing: es);

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-occ-discard");

        await Assert.ThrowsAsync<EventStoreOptimisticConcurrencyException>(
            () => agent.HandleObservedEnvelopeAsync(envelope));

        es.DiscardCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldDiscardPendingEvents_WhenWrappedOccIsThrown()
    {
        var es = new TrackingEventSourcing();
        var wrappedOcc = new ProjectionDispatchAggregateException(
        [
            new ProjectionDispatchFailure(
                "projector", 1,
                new EventStoreOptimisticConcurrencyException("root-1", 3, 4)),
        ]);
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-wrapped-occ",
            onProcess: _ => throw wrappedOcc,
            eventSourcing: es);

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-wrapped-occ");

        await Assert.ThrowsAsync<ProjectionDispatchAggregateException>(
            () => agent.HandleObservedEnvelopeAsync(envelope));

        es.DiscardCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldNotDiscardPendingEvents_ForNonOccFailure()
    {
        var es = new TrackingEventSourcing();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-non-occ",
            onProcess: _ => throw new InvalidOperationException("not occ"),
            eventSourcing: es);

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-non-occ");

        await agent.HandleObservedEnvelopeAsync(envelope);

        es.DiscardCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldSwallow_DeterministicProjectionFailure()
    {
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-swallow",
            onProcess: _ => throw new InvalidOperationException("deterministic boom"));

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-swallow");

        Func<Task> act = () => agent.HandleObservedEnvelopeAsync(envelope);

        await act.Should().NotThrowAsync();
    }

    private static TestScopeAgent BuildActivatedAgent(
        string scopeId,
        Func<EventEnvelope, ProjectionScopeDispatchResult> onProcess,
        IEventSourcingBehavior<ProjectionScopeState>? eventSourcing = null)
    {
        var agent = new TestScopeAgent(onProcess);

        typeof(GAgentBase)
            .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(agent, scopeId);

        if (eventSourcing is not null)
            agent.EventSourcing = eventSourcing;

        agent.State.RootActorId = "root-actor";
        agent.State.ProjectionKind = "test-kind";
        agent.State.SessionId = "session-1";
        agent.State.Active = true;
        agent.State.Released = false;

        var services = new ServiceCollection();
        services.AddSingleton<Func<ProjectionRuntimeScopeKey, TestContext>>(
            static _ => new TestContext("root-actor", "test-kind"));
        agent.Services = services.BuildServiceProvider();

        return agent;
    }

    private static EventEnvelope BuildForwardedObserverEnvelope(string targetStreamId)
    {
        var original = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("publisher-actor"),
        };

        return StreamForwardingRules.BuildForwardedEnvelope(
            original,
            sourceStreamId: "publisher-actor",
            targetStreamId: targetStreamId,
            StreamForwardingMode.HandleThenForward);
    }

    private sealed class TestScopeAgent : ProjectionScopeGAgentBase<TestContext>
    {
        private readonly Func<EventEnvelope, ProjectionScopeDispatchResult> _onProcess;

        public TestScopeAgent(Func<EventEnvelope, ProjectionScopeDispatchResult> onProcess)
        {
            _onProcess = onProcess;
        }

        protected override ProjectionRuntimeMode RuntimeMode =>
            ProjectionRuntimeMode.DurableMaterialization;

        protected override ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
            TestContext context,
            EventEnvelope envelope,
            CancellationToken ct)
        {
            return ValueTask.FromResult(_onProcess(envelope));
        }
    }

    private sealed record TestContext(string RootActorId, string ProjectionKind)
        : IProjectionMaterializationContext;

    private sealed class TrackingEventSourcing : IEventSourcingBehavior<ProjectionScopeState>
    {
        public int DiscardCallCount { get; private set; }
        public long CurrentVersion => 0;
        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage { }
        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default) =>
            Task.FromResult(new EventStoreCommitResult());
        public Task PersistSnapshotAsync(ProjectionScopeState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<ProjectionScopeState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<ProjectionScopeState?>(null);
        public void DiscardPendingEvents() => DiscardCallCount++;
        public ProjectionScopeState TransitionState(ProjectionScopeState current, IMessage evt) => current;
    }
}
