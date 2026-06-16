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
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowRunGAgentSagaCompensationTests
{
    [Fact]
    public async Task TerminalFailure_WithCompensableLedger_ShouldDispatchCompensationsInReverseOrder()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());

        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        harness.Agent.State.CompensableLedger.Should().HaveCount(2);

        await FailStepAsync(harness, "ship_order", "ship failed");

        var firstRequest = CompensationRequests(harness.Publisher).Should().ContainSingle().Subject;
        firstRequest.CompensationStepId.Should().Be("refund_payment");
        firstRequest.CapturedOutput.Should().Be("charge-output");
        firstRequest.IdempotencyKey.Should().Be($"{harness.RunId}:charge_payment:1");
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .BeEmpty();

        await CompleteCompensationAsync(harness, firstRequest);
        var secondRequest = CompensationRequests(harness.Publisher).Last();
        secondRequest.CompensationStepId.Should().Be("cancel_order");
        secondRequest.CapturedOutput.Should().Be("order-output");
        secondRequest.IdempotencyKey.Should().Be($"{harness.RunId}:create_order:1");

        await CompleteCompensationAsync(harness, secondRequest);

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher)
            .Select(x => x.CompensationStepId)
            .Should()
            .Equal("refund_payment", "cancel_order");
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.CompensatedSteps.Should().Be(2);
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .ContainSingle()
            .Which.RunId.Should().Be(harness.RunId);
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensatedFailed);
    }

    [Fact]
    public async Task Reactivation_MidCompensation_ShouldReplayCurrentRequestWithoutDuplicateCommit()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var originalRequests = CommittedEvents<CompensationRequestEvent>(harness.CommittedPublisher).ToList();
        originalRequests.Should().ContainSingle();

        var reactivated = await CreateRunAsync(
            harness.RunId,
            SagaWorkflowYaml(),
            harness.EventStore,
            autoReplaySelfPublished: false);

        reactivated.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
        reactivated.Agent.State.CompensationCursor.Should().Be(1);
        reactivated.Agent.State.CompensationExecutionId.Should().Be(originalRequests[0].ExecutionId);
        var replayed = CompensationRequests(reactivated.Publisher).Should().ContainSingle().Subject;
        replayed.CompensationStepId.Should().Be(originalRequests[0].CompensationStepId);
        replayed.IdempotencyKey.Should().Be(originalRequests[0].IdempotencyKey);
        replayed.CapturedOutput.Should().Be(originalRequests[0].CapturedOutput);
        replayed.ExecutionId.Should().Be(originalRequests[0].ExecutionId);
        CommittedEvents<CompensationRequestEvent>(reactivated.CommittedPublisher).Should().BeEmpty();
    }

    [Fact]
    public async Task Reactivation_AfterPartialCompensation_ShouldReplayRemainingCurrentRequest()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var firstRequest = CompensationRequests(harness.Publisher).Single();
        await CompleteCompensationAsync(harness, firstRequest);
        var secondRequest = CompensationRequests(harness.Publisher).Last();

        var reactivated = await CreateRunAsync(
            harness.RunId,
            SagaWorkflowYaml(),
            harness.EventStore,
            autoReplaySelfPublished: false);

        reactivated.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
        reactivated.Agent.State.CompensationCursor.Should().Be(0);
        var replayed = CompensationRequests(reactivated.Publisher).Should().ContainSingle().Subject;
        replayed.CompensationStepId.Should().Be("cancel_order");
        replayed.IdempotencyKey.Should().Be(secondRequest.IdempotencyKey);
        replayed.CapturedOutput.Should().Be("order-output");
        replayed.ExecutionId.Should().Be(secondRequest.ExecutionId);
        CommittedEvents<CompensationRequestEvent>(reactivated.CommittedPublisher).Should().BeEmpty();
    }

    [Fact]
    public async Task Reactivation_WithRepeatedCompensationStepId_ShouldReplayExactCursor()
    {
        var harness = await CreateStartedRunAsync(RepeatedCompensationTargetWorkflowYaml());
        await CompleteStepAsync(harness, "reserve_stock", "stock-output");
        await CompleteStepAsync(harness, "reserve_credit", "credit-output");
        await FailStepAsync(harness, "finalize", "finalize failed");

        var firstRequest = CompensationRequests(harness.Publisher).Single();
        firstRequest.CompensationStepId.Should().Be("release");
        firstRequest.CapturedOutput.Should().Be("credit-output");
        firstRequest.IdempotencyKey.Should().Be($"{harness.RunId}:reserve_credit:1");

        var reactivated = await CreateRunAsync(
            harness.RunId,
            RepeatedCompensationTargetWorkflowYaml(),
            harness.EventStore,
            autoReplaySelfPublished: false);

        reactivated.Agent.State.CompensationCursor.Should().Be(1);
        var replayed = CompensationRequests(reactivated.Publisher).Should().ContainSingle().Subject;
        replayed.CompensationStepId.Should().Be("release");
        replayed.CapturedOutput.Should().Be("credit-output");
        replayed.IdempotencyKey.Should().Be($"{harness.RunId}:reserve_credit:1");
        replayed.ExecutionId.Should().Be(firstRequest.ExecutionId);
    }

    [Fact]
    public async Task StaleOrDuplicateCompensationCompletion_ShouldBeRejectedAndNotAdvanceCursor()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Single();
        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new CompensationStepCompletedEvent
        {
            RunId = harness.RunId,
            CompensationStepId = request.CompensationStepId,
            Success = true,
            ExecutionId = "stale-execution",
        }));

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        CommittedEvents<StaleStepCompletionRejectedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.ReceivedExecutionId.Should().Be("stale-execution");
        CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        harness.Agent.State.CompensationCursor.Should().Be(1);

        await CompleteCompensationAsync(harness, request);
        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new CompensationStepCompletedEvent
        {
            RunId = harness.RunId,
            CompensationStepId = request.CompensationStepId,
            Success = true,
            ExecutionId = request.ExecutionId,
        }));

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle(x => x.CompensationStepId == "refund_payment");
        CommittedEvents<StaleStepCompletionRejectedEvent>(harness.CommittedPublisher)
            .Should()
            .HaveCount(2);
    }

    [Fact]
    public async Task NoCompensationWorkflow_TerminalFailure_ShouldKeepOriginalCompletionShape()
    {
        var harness = await CreateStartedRunAsync(NoCompensationWorkflowYaml());

        await FailStepAsync(harness, "failed_step", "boom");

        CommittedEvents<CompensationRequestEvent>(harness.CommittedPublisher).Should().BeEmpty();
        var completed = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .ContainSingle()
            .Subject;
        completed.Should().BeEquivalentTo(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_2097",
            RunId = harness.RunId,
            Success = false,
            Error = "boom",
        });
        completed.ToByteArray().Should().Equal(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_2097",
            RunId = harness.RunId,
            Success = false,
            Error = "boom",
        }.ToByteArray());
    }

    [Fact]
    public async Task CompensationDispatch_ShouldUseSelfContinuation()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");

        await FailStepAsync(harness, "ship_order", "ship failed");

        harness.Publisher.Published
            .Where(x => x.Event is CompensationRequestEvent)
            .Should()
            .ContainSingle()
            .Which.Audience.Should().Be(TopologyAudience.Self);
        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent { StepId: "refund_payment" })
            .Should()
            .ContainSingle()
            .Which.Audience.Should().Be(TopologyAudience.Self);
    }

    [Fact]
    public async Task FailedCompensation_ShouldPersistDeadLetterTerminal()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Single();
        await FailCompensationAsync(harness, request, "refund failed");

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Should().BeEquivalentTo(new CompensationStepCompletedEvent
            {
                RunId = harness.RunId,
                CompensationStepId = "refund_payment",
                Success = false,
                Error = "refund failed",
                ExecutionId = request.ExecutionId,
            });
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        var failed = CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        failed.Should().BeEquivalentTo(new WorkflowCompensationFailedEvent
        {
            RunId = harness.RunId,
            FailedCompensationStepId = "refund_payment",
            RemainingUncompensated = 2,
            Error = "refund failed",
        });
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .BeEmpty();
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        harness.Agent.State.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        harness.Agent.State.DeadLetterRemainingUncompensated.Should().Be(2);
        harness.Agent.State.DeadLetterError.Should().Be("refund failed");
        var reactivated = await CreateRunAsync(
            harness.RunId,
            SagaWorkflowYaml(),
            harness.EventStore,
            autoReplaySelfPublished: false);
        CompensationRequests(reactivated.Publisher).Should().BeEmpty();
        reactivated.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
    }

    [Fact]
    public async Task FailedCompensation_WithOnErrorFallback_ShouldDeadLetterWithoutFallback()
    {
        var harness = await CreateStartedRunAsync(CompensationOnErrorWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Single();
        await FailCompensationAsync(harness, request, "cancel failed");

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Success.Should().BeFalse();
        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent { StepId: "fallback_cancel" })
            .Should()
            .BeEmpty();
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .BeEmpty();
        var failed = CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        failed.FailedCompensationStepId.Should().Be("cancel_order");
        failed.RemainingUncompensated.Should().Be(1);
        failed.Error.Should().Be("cancel failed");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
    }

    private static async Task CompleteStepAsync(RunHarness harness, string stepId, string output)
    {
        var request = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent requestEvent && requestEvent.StepId == stepId)
            .Select(x => (StepRequestEvent)x.Event)
            .Last();
        harness.Publisher.Published.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = stepId,
            Success = true,
            Output = output,
            ExecutionId = request.ExecutionId,
        }));
    }

    private static async Task FailStepAsync(RunHarness harness, string stepId, string error)
    {
        var request = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent requestEvent && requestEvent.StepId == stepId)
            .Select(x => (StepRequestEvent)x.Event)
            .Last();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = stepId,
            Success = false,
            Error = error,
            ExecutionId = request.ExecutionId,
        }));
    }

    private static async Task CompleteCompensationAsync(RunHarness harness, CompensationRequestEvent request)
    {
        var stepRequest = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent stepRequestEvent && stepRequestEvent.StepId == request.CompensationStepId)
            .Select(x => (StepRequestEvent)x.Event)
            .Last();
        harness.Publisher.Published.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = request.CompensationStepId,
            Success = true,
            Output = $"done:{request.CompensationStepId}",
            ExecutionId = stepRequest.ExecutionId,
        }));
    }

    private static async Task FailCompensationAsync(RunHarness harness, CompensationRequestEvent request, string error)
    {
        var stepRequest = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent stepRequestEvent && stepRequestEvent.StepId == request.CompensationStepId)
            .Select(x => (StepRequestEvent)x.Event)
            .Last();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = request.CompensationStepId,
            Success = false,
            Error = error,
            ExecutionId = stepRequest.ExecutionId,
        }));
    }

    private static async Task<RunHarness> CreateStartedRunAsync(string workflowYaml)
    {
        var runId = "run-2097-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, workflowYaml);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        }));

        return harness;
    }

    private static async Task<RunHarness> CreateRunAsync(
        string runId,
        string workflowYaml,
        RecordingEventStore? eventStore = null,
        bool autoReplaySelfPublished = true)
    {
        eventStore ??= new RecordingEventStore();
        var committedHook = new RecordingCommittedStatePublicationHook();
        var topologyPublisher = new RecordingEventPublisher(runId)
        {
            AutoReplaySelfPublished = autoReplaySelfPublished,
        };
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

        if (string.IsNullOrWhiteSpace(agent.State.WorkflowYaml))
        {
            await agent.HandleEventAsync(EnvelopeFrom("workflow-run-actor-port", new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = "definition-2097",
                WorkflowName = "wf_2097",
                WorkflowYaml = workflowYaml,
                RunId = runId,
                ScopeId = "scope-1",
            }));
        }

        return new RunHarness(agent, runId, eventStore, committedHook, topologyPublisher);
    }

    private static IReadOnlyList<CompensationRequestEvent> CompensationRequests(RecordingEventPublisher publisher) =>
        publisher.Published
            .Where(x => x.Event is CompensationRequestEvent)
            .Select(x => (CompensationRequestEvent)x.Event)
            .ToArray();

    private static IReadOnlyList<TEvent> CommittedEvents<TEvent>(RecordingCommittedStatePublicationHook committedPublisher)
        where TEvent : class, IMessage<TEvent>, new() =>
        committedPublisher.Events
            .Where(x => x.StateEvent?.EventData?.Is(new TEvent().Descriptor) == true)
            .Select(x => x.StateEvent.EventData.Unpack<TEvent>())
            .ToArray();

    private static EventEnvelope SelfEnvelope(string runId, IMessage payload) =>
        EnvelopeFrom(runId, payload);

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

    private static string SagaWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: transform
            next: charge_payment
            compensation: cancel_order
          - id: charge_payment
            type: transform
            next: ship_order
            compensation: refund_payment
          - id: ship_order
            type: transform
          - id: refund_payment
            type: transform
          - id: cancel_order
            type: transform
        """;

    private static string NoCompensationWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: failed_step
            type: transform
        """;

    private static string RepeatedCompensationTargetWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: reserve_stock
            type: transform
            next: reserve_credit
            compensation: release
          - id: reserve_credit
            type: transform
            next: finalize
            compensation: release
          - id: finalize
            type: transform
          - id: release
            type: transform
        """;

    private static string CompensationOnErrorWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: transform
            next: ship_order
            compensation: cancel_order
          - id: ship_order
            type: transform
          - id: cancel_order
            type: transform
            on_error:
              strategy: fallback
              fallback_step: fallback_cancel
          - id: fallback_cancel
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

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } = [];

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

        public bool AutoReplaySelfPublished { get; init; } = true;

        public WorkflowRunGAgent Agent { get; set; } = null!;

        public async Task PublishAsync<T>(
            T evt,
            TopologyAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((evt.Descriptor.Parser.ParseFrom(evt.ToByteArray()), audience));

            if (AutoReplaySelfPublished && audience == TopologyAudience.Self)
                await Agent.HandleEventAsync(SelfEnvelope(runId, evt), ct);
        }

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
