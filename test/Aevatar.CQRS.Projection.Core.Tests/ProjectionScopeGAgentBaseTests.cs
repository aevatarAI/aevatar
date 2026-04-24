using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using FluentAssertions;
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
        Func<EventEnvelope, ProjectionScopeDispatchResult> onProcess)
    {
        var agent = new TestScopeAgent(onProcess);

        typeof(GAgentBase)
            .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(agent, scopeId);

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
}
