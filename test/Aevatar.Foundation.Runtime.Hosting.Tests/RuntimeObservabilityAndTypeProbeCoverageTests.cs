using System.Diagnostics;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Local.TypeSystem;
using Aevatar.Foundation.Runtime.Observability;
using FluentAssertions;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;

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

        using var activity = AevatarActivitySource.StartHandleEvent(
            "agent-1",
            "evt-1",
            "type.googleapis.com/aevatar.ai.ChatRequestEvent");

        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("HandleEvent:ChatRequestEvent");
        activity.GetTagItem("aevatar.agent.id").Should().Be("agent-1");
        activity.GetTagItem("aevatar.event.id").Should().Be("evt-1");
        activity.GetTagItem("aevatar.event.type").Should().Be("type.googleapis.com/aevatar.ai.ChatRequestEvent");
    }

    [Fact]
    public void AevatarActivitySource_ShouldKeepHandleEventActivity_ForAllEventTypes()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.Agents",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var projectionActivity = AevatarActivitySource.StartHandleEvent(
            "projection.compensation.outbox:workflow",
            "evt-2",
            "type.googleapis.com/ProjectionCompensationTriggerReplayEvent");
        using var contentActivity = AevatarActivitySource.StartHandleEvent(
            "Workflow:run-1:assistant",
            "evt-3",
            "type.googleapis.com/aevatar.ai.TextMessageContentEvent");
        using var startActivity = AevatarActivitySource.StartHandleEvent(
            "Workflow:run-1:assistant",
            "evt-4",
            "type.googleapis.com/aevatar.ai.TextMessageStartEvent");
        using var endActivity = AevatarActivitySource.StartHandleEvent(
            "Workflow:run-1:assistant",
            "evt-5",
            "type.googleapis.com/aevatar.ai.TextMessageEndEvent");
        using var roleChatRequestActivity = AevatarActivitySource.StartHandleEvent(
            "Workflow:run-1:assistant",
            "evt-6",
            "type.googleapis.com/aevatar.ai.ChatRequestEvent");

        projectionActivity.Should().NotBeNull();
        contentActivity.Should().NotBeNull();
        startActivity.Should().NotBeNull();
        endActivity.Should().NotBeNull();
        roleChatRequestActivity.Should().NotBeNull();
    }

    [Fact]
    public void AevatarActivitySource_ShouldCreateChildActivity_FromEnvelopeTraceAndSpan()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.Agents",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var traceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var envelope = new EventEnvelope
        {
            Id = "evt-child",
            Propagation = new EnvelopePropagation
            {
                Trace = new TraceContext
                {
                    TraceId = traceId.ToString(),
                    SpanId = parentSpanId.ToString(),
                    TraceFlags = "01",
                },
            },
        };

        using var activity = AevatarActivitySource.StartHandleEvent("Workflow:run-1", envelope);

        activity.Should().NotBeNull();
        activity!.TraceId.Should().Be(traceId);
        activity.ParentSpanId.Should().Be(parentSpanId);
        activity.ActivityTraceFlags.Should().Be(ActivityTraceFlags.Recorded);
    }

    [Fact]
    public void AgentMetrics_Instruments_ShouldAllowRecording()
    {
        Action act = () =>
        {
            AgentMetrics.RecordEventHandled("Self", AgentMetrics.ResultOk, 18.3);
            AgentMetrics.ActiveActors.Add(1);
        };

        act.Should().NotThrow();
    }

    private sealed class RecordingRuntime : IActorRuntime
    {
        public IActor? Actor { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id)
        {
            _ = id;
            return Task.FromResult(Actor);
        }

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            if (Actor == null)
                throw new InvalidOperationException("Actor not configured.");

            await Actor.HandleEventAsync(envelope, ct);
        }

        public Task<bool> ExistsAsync(string id) => throw new NotSupportedException();

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
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

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

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

    private sealed class StatefulRecordingAgent : GAgentBase<StringValue>
    {
    }
}
