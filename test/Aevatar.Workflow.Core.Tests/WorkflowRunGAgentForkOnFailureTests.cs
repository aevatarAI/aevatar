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
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Abstractions.Execution;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowRunGAgentForkOnFailureTests
{
    [Fact]
    public async Task TerminalFailedRun_WithForkPolicy_ShouldCommitForkRequestedEvent()
    {
        var harness = await CreateStartedRunAsync(WorkflowYaml(onFailure: true), attempt: 1);

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = "failed-step",
            Success = false,
            Error = "boom",
            ExecutionId = harness.StepExecutionId,
        }));

        var requested = harness.CommittedPublisher.Events
            .Select(TryUnpackForkRequest)
            .OfType<WorkflowRunForkRequestedEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        requested.SourceRunId.Should().Be(harness.RunId);
        requested.StartAtStepId.Should().Be("failed-step");
        requested.Attempt.Should().Be(2);
        requested.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task TerminalFailedRun_WithoutForkPolicy_ShouldNotCommitForkRequestedEvent()
    {
        var harness = await CreateStartedRunAsync(WorkflowYaml(onFailure: false), attempt: 0);

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = "failed-step",
            Success = false,
            Error = "boom",
            ExecutionId = harness.StepExecutionId,
        }));

        harness.CommittedPublisher.Events
            .Select(TryUnpackForkRequest)
            .OfType<WorkflowRunForkRequestedEvent>()
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task TerminalFailedRun_WhenAttemptReachedMax_ShouldNotCommitForkRequestedEvent()
    {
        var harness = await CreateStartedRunAsync(WorkflowYaml(onFailure: true, maxAttempts: 2), attempt: 2);

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = "failed-step",
            Success = false,
            Error = "boom",
            ExecutionId = harness.StepExecutionId,
        }));

        harness.CommittedPublisher.Events
            .Select(TryUnpackForkRequest)
            .OfType<WorkflowRunForkRequestedEvent>()
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task ChatRequest_WithInputFileRef_ShouldBindArtifactOwnerBeforeStartingWorkflow()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new RecordingWorkflowFileArtifactOwnershipPort();
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false), ownershipPort);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            InputParts =
            {
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.Text,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "wf-file-123",
                        ArtifactId = "workflow-file://wf-file-123",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        FileName = "invoice.txt",
                        MediaType = "text/plain",
                        SizeBytes = 7,
                        Sha256 = "hash-1",
                    },
                },
            },
        }));

        ownershipPort.Bindings.Should().ContainSingle().Which.Should().BeEquivalentTo(new FileOwnerBinding(
            "wf-file-123",
            "workflow-file://wf-file-123",
            runId,
            "scope-1"));
        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent)
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task ChatRequest_WithFileIdOnlyInputFileRef_ShouldBindArtifactOwnerBeforeStartingWorkflow()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new RecordingWorkflowFileArtifactOwnershipPort();
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false), ownershipPort);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            InputParts =
            {
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.File,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "wf-file-only-123",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        FileName = "invoice.txt",
                        MediaType = "text/plain",
                    },
                },
            },
        }));

        ownershipPort.Bindings.Should().ContainSingle().Which.Should().BeEquivalentTo(new FileOwnerBinding(
            "wf-file-only-123",
            string.Empty,
            runId,
            "scope-1"));
        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent)
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task ChatRequest_WhenInputFileOwnerBindingFails_ShouldNotStartWorkflow()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new RecordingWorkflowFileArtifactOwnershipPort
        {
            Exception = new InvalidOperationException("owner already bound"),
        };
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false), ownershipPort);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            InputParts =
            {
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.Text,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "wf-file-123",
                        ArtifactId = "workflow-file://wf-file-123",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        SizeBytes = 7,
                    },
                },
            },
        }));

        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent)
            .Should()
            .BeEmpty();
        harness.Publisher.Published
            .Where(x => x.Event is WorkflowLlmInvocationCompletedEvent)
            .Select(x => (WorkflowLlmInvocationCompletedEvent)x.Event)
            .Should()
            .ContainSingle()
            .Which.Error.Should().Be("workflow_input_file_binding_failed");
    }

    [Fact]
    public async Task ChatRequest_WhenStartSelfPublishFails_ShouldCommitTerminalFailure()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false));
        harness.Publisher.FailPublish = evt => evt is StartWorkflowEvent;

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        }));

        CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.RunId.Should().Be(runId);
        var completed = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        completed.RunId.Should().Be(runId);
        completed.Success.Should().BeFalse();
        completed.Error.Should().StartWith("start_dispatch_failed: failed during start_dispatch: ");
        completed.Error.Should().NotContain("super-secret-token");
        completed.Error.Should().NotContain("Bearer");
        harness.Agent.State.FinalError.Should().Be(completed.Error);
    }

    [Fact]
    public async Task ReplaceWorkflowDefinitionAndExecute_WhenStartSelfPublishFails_ShouldCommitTerminalFailure()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false));
        harness.Publisher.FailPublish = evt => evt is StartWorkflowEvent;

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = WorkflowYaml(onFailure: false),
            Input = "direct-input",
        }));

        CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Input.Should().Be("direct-input");
        var completed = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        completed.RunId.Should().Be(runId);
        completed.Success.Should().BeFalse();
        completed.Error.Should().StartWith("start_dispatch_failed: failed during start_dispatch: ");
        completed.Error.Should().NotContain("super-secret-token");
        completed.Error.Should().NotContain("Bearer");
    }

    [Fact]
    public async Task ChatRequest_WhenStartTerminalizationFails_ShouldPublishParentFailure()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false));
        harness.Publisher.FailPublish = evt => evt is StartWorkflowEvent;
        harness.CommittedPublisher.FailBeforePublish = evt => evt is WorkflowCompletedEvent;

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            SessionId = "session-1",
        }));

        var fallback = harness.Publisher.Published
            .Where(x => x.Event is WorkflowLlmInvocationCompletedEvent)
            .Select(x => (WorkflowLlmInvocationCompletedEvent)x.Event)
            .Should()
            .ContainSingle()
            .Subject;
        fallback.RunId.Should().Be(runId);
        fallback.SessionId.Should().Be("session-1");
        fallback.Success.Should().BeFalse();
        fallback.Content.Should().Be("Workflow execution failed: start_dispatch_failed");
        fallback.Error.Should().StartWith("start_dispatch_failed: failed during start_dispatch: ");
        fallback.Error.Should().NotContain("super-secret-token");
        fallback.Error.Should().NotContain("Bearer");
    }

    [Fact]
    public async Task ChatRequest_ReplayedWithSameCommandId_ShouldStartRunOnlyOnce()
    {
        var runId = "work-order-run-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false));
        var envelope = EnvelopeFrom("work-order", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        });
        envelope.Id = "work-order-dispatch-command-1";
        envelope.Propagation.CorrelationId = "work-order-dispatch-command-1";

        await harness.Agent.HandleEventAsync(envelope);
        await harness.Agent.HandleEventAsync(envelope.Clone());

        CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should().ContainSingle();
        harness.Publisher.Published.Count(item => item.Event is StartWorkflowEvent)
            .Should().Be(1);
        harness.Agent.State.LastCommandId.Should().Be("work-order-dispatch-command-1");
    }

    private static async Task<RunHarness> CreateStartedRunAsync(string workflowYaml, int attempt)
    {
        var runId = "run-1859-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, workflowYaml);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            ForkSeed = new WorkflowRunForkSeed
            {
                Attempt = attempt,
            },
        }));

        var stepRequest = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent)
            .Select(x => (StepRequestEvent)x.Event)
            .Should()
            .ContainSingle()
            .Subject;

        return harness with { StepExecutionId = stepRequest.ExecutionId };
    }

    private static async Task<RunHarness> CreateRunAsync(
        string runId,
        string workflowYaml,
        RecordingWorkflowFileArtifactOwnershipPort? fileOwnershipPort = null)
    {
        var eventStore = new RecordingEventStore();
        var committedHook = new RecordingCommittedStatePublicationHook();
        var topologyPublisher = new RecordingEventPublisher(runId);
        var agent = new WorkflowRunGAgent(
            new UnsupportedActorRuntime(),
            new UnsupportedActorRuntime(),
            new EmptyEventModuleFactory(),
            [new EmptyWorkflowModulePack()],
            fileArtifactOwnership: fileOwnershipPort)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(eventStore),
            EventPublisher = topologyPublisher,
            Services = new TestServiceProvider(new NoopRuntimeCallbackScheduler(), committedHook),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, runId);
        topologyPublisher.Agent = agent;
        await agent.ActivateAsync();

        await agent.HandleEventAsync(EnvelopeFrom("workflow-run-actor-port", new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = "definition-1859",
            WorkflowName = "wf_1859",
            WorkflowYaml = workflowYaml,
            RunId = runId,
            ScopeId = "scope-1",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        }));

        return new RunHarness(agent, runId, string.Empty, committedHook, topologyPublisher);
    }

    private static WorkflowRunForkRequestedEvent? TryUnpackForkRequest(CommittedStateEventPublished published)
    {
        if (published.StateEvent?.EventData?.Is(WorkflowRunForkRequestedEvent.Descriptor) != true)
            return null;

        return published.StateEvent.EventData.Unpack<WorkflowRunForkRequestedEvent>();
    }

    private static IReadOnlyList<TEvent> CommittedEvents<TEvent>(RecordingCommittedStatePublicationHook hook)
        where TEvent : class, IMessage<TEvent>, new()
    {
        var descriptor = new TEvent().Descriptor;
        return hook.Events
            .Where(x => x.StateEvent?.EventData?.Is(descriptor) == true)
            .Select(x => x.StateEvent!.EventData.Unpack<TEvent>())
            .ToArray();
    }

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

    private static string WorkflowYaml(bool onFailure, int maxAttempts = 3) =>
        onFailure
            ? $$"""
                name: wf_1859
                on_failure:
                  action: fork_from_failed_step
                  max_attempts: {{maxAttempts}}
                roles: []
                steps:
                  - id: failed-step
                    type: transform
                """
            : """
                name: wf_1859
                roles: []
                steps:
                  - id: failed-step
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
        string StepExecutionId,
        RecordingCommittedStatePublicationHook CommittedPublisher,
        RecordingEventPublisher Publisher);

    private sealed record FileOwnerBinding(
        string? FileId,
        string? ArtifactId,
        string OwnerRunId,
        string? OwnerScopeId);

    private sealed class RecordingWorkflowFileArtifactOwnershipPort :
        Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort
    {
        public List<FileOwnerBinding> Bindings { get; } = [];

        public Exception? Exception { get; init; }

        public ValueTask BindOwnerAsync(
            Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef fileRef,
            string ownerRunId,
            string? ownerScopeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception != null)
                throw Exception;

            Bindings.Add(new FileOwnerBinding(
                fileRef.FileId,
                fileRef.ArtifactId,
                ownerRunId,
                ownerScopeId));
            return ValueTask.CompletedTask;
        }
    }

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

        public Func<IMessage, bool>? FailPublish { get; set; }

        public async Task PublishAsync<T>(
            T evt,
            TopologyAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (FailPublish?.Invoke(evt) == true)
                throw new InvalidOperationException("start failed with bearer super-secret-token");

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

        public Func<IMessage, bool>? FailBeforePublish { get; set; }

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (context.Published.StateEvent?.EventData is { } eventData)
            {
                var message = TryUnpackKnownEvent(eventData);
                if (message != null && FailBeforePublish?.Invoke(message) == true)
                    throw new InvalidOperationException("committed publication failed with bearer super-secret-token");
            }

            Events.Add(context.Published.Clone());
            return Task.CompletedTask;
        }

        private static IMessage? TryUnpackKnownEvent(Any eventData)
        {
            if (eventData.Is(WorkflowCompletedEvent.Descriptor))
                return eventData.Unpack<WorkflowCompletedEvent>();
            if (eventData.Is(WorkflowRunExecutionStartedEvent.Descriptor))
                return eventData.Unpack<WorkflowRunExecutionStartedEvent>();
            if (eventData.Is(WorkflowRunForkRequestedEvent.Descriptor))
                return eventData.Unpack<WorkflowRunForkRequestedEvent>();

            return null;
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
