using System.Diagnostics;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Runtime.Observability;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeObservabilityAndKindProbeCoverageTests
{
    [Fact]
    public async Task LocalActorKindProbe_ShouldResolveRuntimeAgentKind_AndReturnNullWhenActorMissing()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddSingleton(new AgentKindRegistryBuilder().Register<RecordingAgent>());
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var probe = provider.GetRequiredService<IActorKindProbe>();

        await runtime.CreateByKindAsync("tests.recording-agent", "actor-1");

        var kind = await probe.GetRuntimeAgentKindAsync("actor-1");
        kind.Should().Be("tests.recording-agent");

        (await probe.GetRuntimeAgentKindAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task LocalActorKindProbe_ShouldValidateInputAndCancellationToken()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<IActorKindProbe>();

        await Assert.ThrowsAsync<ArgumentException>(() => probe.GetRuntimeAgentKindAsync(""));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.GetRuntimeAgentKindAsync("actor-1", cts.Token));
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
        activity.GetTagItem("aevatar.agent.type").Should().Be("unknown");
        activity.GetTagItem("aevatar.event.id").Should().Be("evt-1");
        activity.GetTagItem("aevatar.event.type").Should().Be("type.googleapis.com/aevatar.ai.ChatRequestEvent");
    }

    [Fact]
    public void AevatarActivitySource_ShouldCreateInspectorActivityHelpers_WithExpectedTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var spawn = AevatarActivitySource.StartAgentSpawn("agent-1", "TestAgent");
        using var deactivate = AevatarActivitySource.StartAgentDeactivate("agent-1", "TestAgent");
        using var link = AevatarActivitySource.StartAgentLink("parent-1", "child-1");
        using var unlink = AevatarActivitySource.StartAgentUnlink("parent-1", "child-1");
        using var projection = AevatarActivitySource.StartProjectionMaterialize("TestProjectionContext", "evt-1");
        using var upsert = AevatarActivitySource.StartReadmodelUpsert("TestReadModel", 7);
        using var delete = AevatarActivitySource.StartReadmodelDelete("TestReadModel", "rm-1");
        using var workflow = AevatarActivitySource.StartWorkflowRun("run-1", "workflow-a", "step-a");

        spawn.Should().NotBeNull();
        spawn!.DisplayName.Should().Be(AevatarActivitySource.AgentSpawnActivityName);
        spawn.GetTagItem(AevatarActivitySource.AgentIdTag).Should().Be("agent-1");
        spawn.GetTagItem(AevatarActivitySource.AgentTypeTag).Should().Be("TestAgent");

        deactivate.Should().NotBeNull();
        deactivate!.DisplayName.Should().Be(AevatarActivitySource.AgentDeactivateActivityName);
        deactivate.GetTagItem(AevatarActivitySource.AgentIdTag).Should().Be("agent-1");
        deactivate.GetTagItem(AevatarActivitySource.AgentTypeTag).Should().Be("TestAgent");

        link.Should().NotBeNull();
        link!.DisplayName.Should().Be(AevatarActivitySource.AgentLinkActivityName);
        link.GetTagItem(AevatarActivitySource.AgentParentTag).Should().Be("parent-1");
        link.GetTagItem(AevatarActivitySource.AgentIdTag).Should().Be("child-1");

        unlink.Should().NotBeNull();
        unlink!.DisplayName.Should().Be(AevatarActivitySource.AgentUnlinkActivityName);
        unlink.GetTagItem(AevatarActivitySource.AgentParentTag).Should().Be("parent-1");
        unlink.GetTagItem(AevatarActivitySource.AgentIdTag).Should().Be("child-1");

        projection.Should().NotBeNull();
        projection!.DisplayName.Should().Be(AevatarActivitySource.ProjectionMaterializeActivityName);
        projection.GetTagItem(AevatarActivitySource.ProjectionNameTag).Should().Be("TestProjectionContext");
        projection.GetTagItem(AevatarActivitySource.ProjectionLastEventIdTag).Should().Be("evt-1");

        upsert.Should().NotBeNull();
        upsert!.DisplayName.Should().Be(AevatarActivitySource.ReadModelUpsertActivityName);
        upsert.GetTagItem(AevatarActivitySource.ReadModelNameTag).Should().Be("TestReadModel");
        upsert.GetTagItem(AevatarActivitySource.ReadModelStateVersionTag).Should().Be(7L);

        delete.Should().NotBeNull();
        delete!.DisplayName.Should().Be(AevatarActivitySource.ReadModelDeleteActivityName);
        delete.GetTagItem(AevatarActivitySource.ReadModelNameTag).Should().Be("TestReadModel");
        delete.GetTagItem(AevatarActivitySource.ReadModelIdTag).Should().Be("rm-1");

        workflow.Should().NotBeNull();
        workflow!.DisplayName.Should().Be(AevatarActivitySource.WorkflowRunActivityName);
        workflow.GetTagItem(AevatarActivitySource.WorkflowRunIdTag).Should().Be("run-1");
        workflow.GetTagItem(AevatarActivitySource.WorkflowNameTag).Should().Be("workflow-a");
        workflow.GetTagItem(AevatarActivitySource.WorkflowStepTag).Should().Be("step-a");
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

    [Theory]
    [InlineData("")]
    [InlineData("Recorded")]
    [InlineData("not-a-flag")]
    public void AevatarActivitySource_ShouldResolveEnvelopeTraceFlags(string traceFlags)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var traceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var envelope = new EventEnvelope
        {
            Id = "evt-flags",
            Propagation = new EnvelopePropagation
            {
                Trace = new TraceContext
                {
                    TraceId = traceId.ToString(),
                    SpanId = parentSpanId.ToString(),
                    TraceFlags = traceFlags,
                },
            },
        };

        using var activity = AevatarActivitySource.StartHandleEvent("agent-flags", envelope);

        activity.Should().NotBeNull();
        activity!.TraceId.Should().Be(traceId);
        activity.ParentSpanId.Should().Be(parentSpanId);
    }

    [Fact]
    public void AevatarActivitySource_ShouldFallbackToFreshActivity_WhenEnvelopeTraceIsInvalid()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var envelope = new EventEnvelope
        {
            Id = "evt-invalid-trace",
            Propagation = new EnvelopePropagation
            {
                Trace = new TraceContext
                {
                    TraceId = "not-a-trace-id",
                    SpanId = "not-a-span-id",
                    TraceFlags = "01",
                },
            },
        };

        using var activity = AevatarActivitySource.StartHandleEvent("agent-invalid-trace", envelope);

        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("HandleEvent:UnknownEvent");
        activity.ParentSpanId.Should().Be(default(ActivitySpanId));
    }

    [Fact]
    public void AevatarActivitySource_ShouldUseResolvedTypeName_WhenTypeUrlHasNoSlash()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AevatarActivitySource.StartHandleEvent("agent-1", "evt-1", "CustomEvent");

        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("HandleEvent:CustomEvent");
        activity.GetTagItem(AevatarActivitySource.EventTypeTag).Should().Be("CustomEvent");
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

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            if (Actor == null)
                throw new InvalidOperationException("Actor not configured.");

            await Actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
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

    [GAgent("tests.recording-agent")]
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
