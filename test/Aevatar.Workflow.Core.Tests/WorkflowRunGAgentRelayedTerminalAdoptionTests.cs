using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests;

/// <summary>
/// R1 (06-20-observatory-run-state-feed): the projection-root run actor must adopt a terminal that
/// belongs to its OWN run when the terminal was executed/relayed by a sub-actor of the same logical run
/// (publisher != self), so the current-state projector gate passes and the summary status advances.
/// A genuine child sub-workflow terminal (different run id) must NOT clobber the parent's state.
/// </summary>
public sealed class WorkflowRunGAgentRelayedTerminalAdoptionTests
{
    private const string ChildActorId = "scope-workflow:wf:run:child-executor";

    [Fact]
    public async Task RelayedOwnRunCompletedFailure_FromSubActor_ShouldAdoptTerminalFailedStatus()
    {
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        }));

        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.FinalError.Should().Be("inner failure");
        harness.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeTrue();
        AssertNoCrossActorSideEffects(harness);
    }

    [Fact]
    public async Task RelayedOwnRunCompletedSuccess_FromSubActor_ShouldAdoptTerminalCompletedStatus()
    {
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "inner output",
        }));

        harness.Agent.State.Status.Should().Be("completed");
        harness.Agent.State.FinalOutput.Should().Be("inner output");
        harness.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeTrue();
        AssertNoCrossActorSideEffects(harness);
    }

    [Fact]
    public async Task RelayedChildSubWorkflowTerminal_DifferentRunId_ShouldNotClobberParentState()
    {
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId + "-child",
            WorkflowName = "wf_child",
            Success = false,
            Error = "child failure",
        }));

        harness.Agent.State.Status.Should().Be("running");
        harness.Agent.State.FinalError.Should().BeEmpty();
        harness.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeFalse();
    }

    [Fact]
    public async Task RelayedOwnRunCompleted_DuplicateTerminal_ShouldCollapseToSingleTerminal()
    {
        var harness = await CreateStartedRunAsync();

        var firstTerminal = EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        });
        await harness.Agent.HandleEventAsync(firstTerminal);
        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "should be ignored",
        }));

        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.FinalError.Should().Be("inner failure");
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RelayedOwnRunTerminal_BeforeStart_ShouldNotBeAdopted()
    {
        // Bound but not started: RunId/ScopeId/StartedAtUtc are unset.
        var harness = await CreateRunAsync("run-not-started-" + Guid.NewGuid().ToString("N"));

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        }));

        harness.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeFalse();
        harness.Agent.State.Status.Should().NotBe("failed");
    }

    [Fact]
    public async Task ApiStop_FromExternalPublisher_ShouldRunFullStopNotStatusOnlyAdopt()
    {
        // C2 (06-20-observatory-run-state-feed): a legitimate manual/API stop arrives non-self
        // (publisher = api.workflow.stop) with the same RunId. It must run the FULL stop path
        // (CompleteStopAsync: cleanup + parent WorkflowLlmInvocationCompletedEvent), NOT be downgraded to a
        // status-only adopt. The typed [EventHandler] HandleWorkflowStopped fires for non-self publishers.
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api.workflow.stop", new WorkflowStoppedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Reason = "operator stop",
        }));

        harness.Agent.State.Status.Should().Be("stopped");
        harness.Agent.State.FinalError.Should().Be("operator stop");
        AssertRanFullStop(harness);
    }

    [Fact]
    public async Task ApiRunStop_FromExternalPublisher_ShouldRunFullStopNotStatusOnlyAdopt()
    {
        // C2: same as above for the run-stop (timeout) variant — port stop publisher
        // (workflow-run-actor-port) is non-self + same RunId and must run the full CompleteStopAsync path.
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom("workflow-run-actor-port", new WorkflowRunStoppedEvent
        {
            RunId = harness.RunId,
            Reason = "run timed out",
        }));

        harness.Agent.State.Status.Should().Be("stopped");
        harness.Agent.State.FinalError.Should().Be("run timed out");
        AssertRanFullStop(harness);
    }

    [Fact]
    public async Task DirectTypedStopHandler_ShouldRunFullStop_NotSilentNoOp()
    {
        // C2: a direct typed-handler invocation (no envelope publisher) must still run the full stop. The
        // removed IsSelfPublishedInbound() guard had made this a silent no-op.
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Reason = "direct stop",
        });

        harness.Agent.State.Status.Should().Be("stopped");
        harness.Agent.State.FinalError.Should().Be("direct stop");
        AssertRanFullStop(harness);
    }

    [Fact]
    public async Task TerminalRun_WithPendingSubWorkflowInvocation_ShouldNotRecoverOnActivation()
    {
        // C4 (06-20-observatory-run-state-feed): ApplyWorkflowCompleted does NOT clear
        // PendingSubWorkflowInvocations (those are cleared by HandleWorkflowCompleted's
        // CleanupPendingInvocationsForRunAsync, which the status-only completion-adopt path skips). So an
        // adopted-completed run can carry a stale recoverable pending invocation into activation. The
        // activation guard must skip RecoverPendingSubWorkflowInvocationsAsync for a terminal run, so a
        // terminal run never resurrects/drives in-flight child handoffs. The recording runtime is
        // unsupported: had recovery driven the handoff it would have thrown — reactivation succeeding (and
        // leaving the pending invocation untouched) proves recovery was skipped.
        var eventStore = new RecordingEventStore();
        var runId = "run-terminal-pending-" + Guid.NewGuid().ToString("N");

        // Seed the committed stream to represent an adopted-completed run that still carries a recoverable
        // pending invocation: start (-> running, sets RunId/ScopeId/StartedAtUtc), a registered pending
        // invocation (HandoffPhase=Registered, recoverable), then a terminal WorkflowCompletedEvent. Like
        // ApplyWorkflowCompleted (and the status-only adopt path), the terminal event does NOT clear the
        // pending invocation.
        var seeded = await CreateRunAsync(runId, eventStore);
        await seeded.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        }));
        await SeedPendingSubWorkflowInvocationAsync(eventStore, runId);
        await SeedTerminalCompletedAsync(eventStore, runId);

        // Reactivate the terminal run: the activation guard must NOT recover the pending invocation
        // (otherwise the unsupported runtime throws). Reactivation succeeds, state is terminal, and the
        // pending invocation is left untouched (not driven).
        var reactivated = await CreateRunAsync(runId, eventStore);

        reactivated.Agent.State.Status.Should().Be("failed");
        reactivated.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeTrue();
        reactivated.Agent.State.PendingSubWorkflowInvocations.Should().ContainSingle();
        reactivated.Agent.State.PendingSubWorkflowInvocations[0].HandoffPhase
            .Should().Be(SubWorkflowInvocationHandoffPhase.Registered);
    }

    private static void AssertRanFullStop(RunHarness harness)
    {
        // CompleteStopAsync publishes a parent WorkflowLlmInvocationCompletedEvent; its presence proves the
        // full stop path ran (vs a status-only transition that publishes nothing cross-actor).
        harness.Publisher.Published
            .Where(x => x.Event is WorkflowLlmInvocationCompletedEvent && x.Audience == TopologyAudience.Parent)
            .Should()
            .ContainSingle();
    }

    private static void AssertNoCrossActorSideEffects(RunHarness harness)
    {
        // The status-only adopt path must not re-run the cross-actor side effects the inner executor
        // already emitted: no parent completion publish, no LLM-invocation-completed publish.
        harness.Publisher.Published
            .Where(x => x.Audience == TopologyAudience.Parent)
            .Should()
            .BeEmpty();
        harness.Publisher.Published
            .Where(x => x.Event is WorkflowLlmInvocationCompletedEvent)
            .Should()
            .BeEmpty();
    }

    private static async Task<RunHarness> CreateStartedRunAsync()
    {
        var runId = "run-relayed-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        }));

        harness.Agent.State.Status.Should().Be("running");
        harness.Agent.State.RunId.Should().Be(runId);
        harness.Agent.State.ScopeId.Should().Be("scope-1");
        harness.Agent.State.StartedAtUtc.Should().NotBeNull();

        // Clear the start-path publishes so each test only observes adopt-path side effects.
        harness.Publisher.Published.Clear();
        return harness;
    }

    private static async Task<RunHarness> CreateRunAsync(string runId, RecordingEventStore? eventStore = null)
    {
        eventStore ??= new RecordingEventStore();
        var committedHook = new RecordingCommittedStatePublicationHook();
        var topologyPublisher = new RecordingEventPublisher(runId);
        var agent = new WorkflowRunGAgent(
            new UnsupportedActorRuntime(),
            new UnsupportedActorRuntime(),
            new EmptyEventModuleFactory(),
            [new EmptyWorkflowModulePack()])
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(eventStore),
            EventPublisher = topologyPublisher,
            Services = new TestServiceProvider(new NoopRuntimeCallbackScheduler(), committedHook),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, runId);
        topologyPublisher.Agent = agent;
        await agent.ActivateAsync();

        // Bind only on first activation; on reactivation the definition rehydrates from the event store.
        if (string.IsNullOrWhiteSpace(agent.State.WorkflowYaml))
        {
            await agent.HandleEventAsync(EnvelopeFrom("workflow-run-actor-port", new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = "definition-relayed",
                WorkflowName = "wf_relayed",
                WorkflowYaml = WorkflowYaml(),
                RunId = runId,
                ScopeId = "scope-1",
            }));
        }

        return new RunHarness(agent, runId, eventStore, committedHook, topologyPublisher);
    }

    // Seed a recoverable pending sub-workflow invocation directly into the run's committed event stream.
    // SubWorkflowInvocationRegisteredEvent's reducer (registered on WorkflowRunGAgent) adds the pending
    // invocation on replay; HandoffPhase=Registered keeps it recoverable (not StartDispatched/StartFailed).
    private static async Task SeedPendingSubWorkflowInvocationAsync(RecordingEventStore eventStore, string runId)
    {
        var registered = new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = runId + "-child",
            ParentRunId = runId,
            ParentStepId = "step-call",
            WorkflowName = "child_flow",
            ChildActorId = "scope-workflow:wf:run:" + runId + "-child",
            ChildRunId = runId + "-child",
            DefinitionActorId = "definition-child",
            DefinitionYaml = WorkflowYaml(),
            ScopeId = "scope-1",
            HandoffPhase = (int)SubWorkflowInvocationHandoffPhase.Registered,
        };

        await AppendSeedEventAsync(eventStore, runId, registered);
    }

    // Seed a terminal WorkflowCompletedEvent (failure) directly into the committed stream. Mirrors the
    // status-only completion-adopt path: ApplyWorkflowCompleted sets Status/TerminalWorkflowCompletionRecorded
    // but does NOT clear PendingSubWorkflowInvocations.
    private static async Task SeedTerminalCompletedAsync(RecordingEventStore eventStore, string runId)
    {
        await AppendSeedEventAsync(eventStore, runId, new WorkflowCompletedEvent
        {
            RunId = runId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        });
    }

    private static async Task AppendSeedEventAsync(RecordingEventStore eventStore, string runId, IMessage payload)
    {
        var version = await eventStore.GetVersionAsync(runId) + 1;
        await eventStore.AppendAsync(
            runId,
            [
                new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = version,
                    EventType = payload.Descriptor.FullName,
                    EventData = Any.Pack(payload),
                    AgentId = runId,
                },
            ],
            version - 1);
    }

    private static IEnumerable<T> CommittedEvents<T>(RecordingCommittedStatePublicationHook hook)
        where T : class, IMessage<T>, new()
    {
        var descriptor = new T().Descriptor;
        foreach (var published in hook.Events)
        {
            if (published.StateEvent?.EventData?.Is(descriptor) == true)
                yield return published.StateEvent.EventData.Unpack<T>();
        }
    }

    private static EventEnvelope EnvelopeFrom(string publisherActorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherActorId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

    private static EventEnvelope SelfEnvelope(string runId, IMessage payload) =>
        EnvelopeFrom(runId, payload);

    private static string WorkflowYaml() =>
        """
        name: wf_relayed
        roles: []
        steps:
          - id: only-step
            type: transform
        """;

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private sealed record RunHarness(
        WorkflowRunGAgent Agent,
        string RunId,
        RecordingEventStore EventStore,
        RecordingCommittedStatePublicationHook CommittedPublisher,
        RecordingEventPublisher Publisher);

    private sealed class EmptyWorkflowModulePack : IWorkflowModulePack
    {
        public string Name => "test.empty";

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } =
        [
            WorkflowModuleRegistration.Create<TransformModule>("transform"),
        ];

        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } = [];

        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = [];
    }

    private sealed class EmptyEventModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            _ = name;
            module = null;
            return false;
        }
    }

    private sealed class RecordingEventPublisher(string runId) : IEventPublisher
    {
        public List<(IMessage Event, TopologyAudience Audience)> Published { get; } = [];

        public async Task PublishAsync<T>(
            T evt,
            TopologyAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((evt, audience));

            if (audience == TopologyAudience.Self)
                await Agent.HandleEventAsync(SelfEnvelope(runId, evt), ct);
        }

        public WorkflowRunGAgent Agent { get; set; } = null!;

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage =>
            Task.CompletedTask;
    }

    private sealed class RecordingCommittedStatePublicationHook : ICommittedStatePublicationHook
    {
        public List<CommittedStateEventPublished> Events { get; } = [];

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(context.Published.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _streams = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            currentVersion.Should().Be(expectedVersion);

            var committed = events.Select(x => x.Clone()).ToList();
            stream.AddRange(committed);
            _streams[agentId] = stream;

            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult<IReadOnlyList<StateEvent>>(
                events
                    .Where(x => !fromVersion.HasValue || x.Version >= fromVersion.Value)
                    .Select(x => x.Clone())
                    .ToArray());
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult(events.Count == 0 ? 0 : events[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_streams.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var removed = stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)removed);
        }
    }

    private sealed class TestServiceProvider(
        NoopRuntimeCallbackScheduler scheduler,
        RecordingCommittedStatePublicationHook committedHook) : IServiceProvider
    {
        public object? GetService(System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return scheduler;
            if (serviceType == typeof(IEnumerable<ICommittedStatePublicationHook>))
                return new ICommittedStatePublicationHook[] { committedHook };

            return null;
        }
    }

    private sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class UnsupportedActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id) =>
            throw new NotSupportedException();

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
