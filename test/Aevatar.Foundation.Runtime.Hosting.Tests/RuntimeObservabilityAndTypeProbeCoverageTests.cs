using System.Diagnostics;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.TypeSystem;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeObservabilityAndTypeProbeCoverageTests
{
    [Fact]
    public async Task LocalActorTypeProbe_ShouldResolveRuntimeAgentTypeName_AndReturnNullWhenActorMissing()
    {
        var runtime = new RecordingRuntime
        {
            Actor = new RecordingActor("actor-1", new RecordingAgent()),
        };
        var probe = new LocalActorTypeProbe(runtime);

        var typeName = await probe.GetRuntimeAgentTypeNameAsync("actor-1");
        typeName.Should().Contain(typeof(RecordingAgent).FullName);

        runtime.Actor = null;
        (await probe.GetRuntimeAgentTypeNameAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task LocalActorTypeProbe_ShouldValidateInputAndCancellationToken()
    {
        var probe = new LocalActorTypeProbe(new RecordingRuntime());

        await Assert.ThrowsAsync<ArgumentException>(() => probe.GetRuntimeAgentTypeNameAsync(""));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.GetRuntimeAgentTypeNameAsync("actor-1", cts.Token));
    }

    [Fact]
    public void AevatarActivitySource_ShouldCreateHandleEventActivity_WhenListenerEnabled()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.Agents",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AevatarActivitySource.StartHandleEvent("agent-1", "evt-1");

        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("HandleEvent agent-1");
        activity.GetTagItem("aevatar.agent.id").Should().Be("agent-1");
        activity.GetTagItem("aevatar.event.id").Should().Be("evt-1");
    }

    [Fact]
    public void AgentMetrics_Instruments_ShouldAllowRecording()
    {
        Action act = () =>
        {
            AgentMetrics.EventsHandled.Add(1);
            AgentMetrics.HandlerDuration.Record(12.5);
            AgentMetrics.EventHandleDuration.Record(18.3);
            AgentMetrics.RouteTargets.Add(2);
            AgentMetrics.StateLoads.Add(1);
            AgentMetrics.StateSaves.Add(1);
            AgentMetrics.ActiveActors.Add(1);
        };

        act.Should().NotThrow();
    }

    private sealed class RecordingRuntime : IActorRuntime
    {
        public IActor? Actor { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id)
        {
            _ = id;
            return Task.FromResult(Actor);
        }

        public Task<bool> ExistsAsync(string id) => throw new NotSupportedException();

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RestoreAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = agent;

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent : IAgent
    {
        public string Id { get; } = "agent-1";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("recording-agent");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
