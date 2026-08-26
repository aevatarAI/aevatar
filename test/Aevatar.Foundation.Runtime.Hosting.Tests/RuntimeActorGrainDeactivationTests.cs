using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using NSubstitute;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeActorGrainDeactivationTests
{
    [Fact]
    public async Task OnDeactivateAsync_WhenSelfStreamUnsubscribeFails_ShouldStillDeactivateAgent()
    {
        var unsubscribeFailure = new InvalidOperationException("unsubscribe-failed");
        var handle = new FailingSubscriptionHandle(unsubscribeFailure);
        var agent = new RecordingDeactivationAgent();
        var grain = CreateGrain(handle, agent);

        var act = () => grain.OnDeactivateAsync(
            new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "test"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("unsubscribe-failed");
        handle.UnsubscribeCallCount.Should().Be(1);
        agent.DeactivateCallCount.Should().Be(1);
        agent.BackgroundCancellationRequested.Should().BeTrue();
        GetPrivateField<IAgent>(grain, "_agent").Should().BeNull();
        GetPrivateField<StreamSubscriptionHandle<EventEnvelope>>(grain, "_selfStreamHandle").Should().BeNull();
    }

    [Fact]
    public async Task OnDeactivateAsync_WhenUnsubscribeAndAgentCleanupFail_ShouldPreserveBothFailures()
    {
        var unsubscribeFailure = new InvalidOperationException("unsubscribe-failed");
        var agentFailure = new InvalidOperationException("agent-cleanup-failed");
        var handle = new FailingSubscriptionHandle(unsubscribeFailure);
        var agent = new RecordingDeactivationAgent(agentFailure);
        var grain = CreateGrain(handle, agent);

        var act = () => grain.OnDeactivateAsync(
            new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "test"),
            CancellationToken.None);

        var aggregate = (await act.Should().ThrowAsync<AggregateException>()).Which;
        aggregate.InnerExceptions.Should().Contain(unsubscribeFailure);
        aggregate.InnerExceptions.Should().Contain(agentFailure);
        handle.UnsubscribeCallCount.Should().Be(1);
        agent.DeactivateCallCount.Should().Be(1);
        agent.BackgroundCancellationRequested.Should().BeTrue();
        GetPrivateField<IAgent>(grain, "_agent").Should().BeNull();
        GetPrivateField<StreamSubscriptionHandle<EventEnvelope>>(grain, "_selfStreamHandle").Should().BeNull();
    }

    private static RuntimeActorGrain CreateGrain(
        StreamSubscriptionHandle<EventEnvelope> handle,
        IAgent agent)
    {
        var state = Substitute.For<IPersistentState<RuntimeActorGrainState>>();
        var publicationState = Substitute.For<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>>();
        var grain = new RuntimeActorGrain(state, publicationState);
        SetPrivateField(grain, "_selfStreamHandle", handle);
        SetPrivateField(grain, "_agent", agent);
        return grain;
    }

    private static void SetPrivateField<T>(RuntimeActorGrain grain, string name, T value)
    {
        typeof(RuntimeActorGrain)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(grain, value);
    }

    private static T? GetPrivateField<T>(RuntimeActorGrain grain, string name) where T : class =>
        (T?)typeof(RuntimeActorGrain)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(grain);

    private sealed class RecordingDeactivationAgent(Exception? deactivationFailure = null) : IAgent
    {
        private readonly CancellationTokenSource _backgroundCancellation = new();

        public int DeactivateCallCount { get; private set; }

        public bool BackgroundCancellationRequested => _backgroundCancellation.IsCancellationRequested;

        public string Id => "deactivation-test-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("deactivation test agent");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            DeactivateCallCount++;
            _backgroundCancellation.Cancel();
            return deactivationFailure == null
                ? Task.CompletedTask
                : Task.FromException(deactivationFailure);
        }
    }

    private sealed class FailingSubscriptionHandle(Exception failure) : StreamSubscriptionHandle<EventEnvelope>
    {
        public int UnsubscribeCallCount { get; private set; }

        public override Guid HandleId { get; } = Guid.NewGuid();

        public override StreamId StreamId { get; } =
            StreamId.Create(OrleansRuntimeConstants.ActorEventStreamNamespace, "deactivation-test-agent");

        public override string ProviderName => "deactivation-test-provider";

        public override Task UnsubscribeAsync()
        {
            UnsubscribeCallCount++;
            return Task.FromException(failure);
        }

        public override Task<StreamSubscriptionHandle<EventEnvelope>> ResumeAsync(
            IAsyncObserver<EventEnvelope> observer,
            StreamSequenceToken? token = null) =>
            Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(this);

        public override Task<StreamSubscriptionHandle<EventEnvelope>> ResumeAsync(
            IAsyncBatchObserver<EventEnvelope> observer,
            StreamSequenceToken? token = null) =>
            Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(this);

        public override bool Equals(StreamSubscriptionHandle<EventEnvelope>? other) =>
            other is FailingSubscriptionHandle handle && handle.HandleId == HandleId;
    }
}
