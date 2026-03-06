using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SystemType = System.Type;
using static Aevatar.Integration.Tests.WorkflowGAgentCoverageTestHelpers;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowRunGAgentCoverageTests
{
    [Fact]
    public async Task BindWorkflowDefinitionAsync_WhenSwitchingWorkflowName_ShouldThrow()
    {
        var agent = CreateRunAgent();
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_a");

        Func<Task> act = () => agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_b");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot switch*");
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_WithEmptyYaml_ShouldMarkInvalidAndDescribe()
    {
        var agent = CreateRunAgent();

        await agent.BindWorkflowDefinitionAsync(string.Empty, "wf_empty");
        var description = await agent.GetDescriptionAsync();

        agent.State.Compiled.Should().BeFalse();
        agent.State.CompilationError.Should().Be("workflow yaml is empty");
        description.Should().Contain("wf_empty");
        description.Should().Contain("idle");
    }

    [Fact]
    public async Task HandleChatRequest_WhenNotCompiled_ShouldPublishFailureResponse()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync(string.Empty, "wf_invalid");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "run-1",
        });

        runtime.CreateCalls.Should().Be(0);
        publisher.Published.Should().ContainSingle();
        var response = publisher.Published.Single().evt.Should().BeOfType<ChatResponseEvent>().Subject;
        response.Content.Should().Contain("not compiled");
        response.SessionId.Should().Be("run-1");
    }

    [Fact]
    public async Task HandleChatRequest_WhenCompiled_ShouldCreateRoleActorsAndStartWorkflow()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_bound");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "first",
            SessionId = "session-1",
        });

        runtime.CreateCalls.Should().Be(1);
        runtime.Linked.Should().ContainSingle(x => x.parent == "workflow-run-root" && x.child == "workflow-run-root:role_a");

        var roleActor = runtime.CreatedActors.Single();
        roleActor.Id.Should().Be("workflow-run-root:role_a");
        roleActor.Agent.Should().BeOfType<FakeRoleAgent>();

        var roleAgent = (FakeRoleAgent)roleActor.Agent;
        roleAgent.RoleName.Should().Be("RoleA");
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        roleAgent.LastInitializeEvent!.ProviderName.Should().BeEmpty();
        roleAgent.LastInitializeEvent.Model.Should().BeEmpty();
        roleAgent.LastInitializeEvent.SystemPrompt.Should().Be("helpful role");

        var start = publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().Single();
        start.WorkflowName.Should().Be("wf_valid");
        start.RunId.Should().Be("session-1");
        agent.State.RunId.Should().Be("session-1");
        agent.State.Status.Should().Be("active");
    }

    [Fact]
    public async Task HandleChatRequest_ShouldPassThroughFullRoleConfigurationToInitializeEvent()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        await agent.BindWorkflowDefinitionAsync(BuildWorkflowYamlWithFullRoleConfig(), "wf_role_fields");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-2",
        });

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        var initialize = roleAgent.LastInitializeEvent!;
        initialize.RoleName.Should().Be("RoleA");
        initialize.ProviderName.Should().Be("openai");
        initialize.Model.Should().Be("gpt-4o-mini");
        initialize.SystemPrompt.Should().Be("helpful role");
        initialize.HasTemperature.Should().BeTrue();
        initialize.Temperature.Should().Be(0.2);
        initialize.MaxTokens.Should().Be(256);
        initialize.MaxToolRounds.Should().Be(4);
        initialize.MaxHistoryMessages.Should().Be(30);
        initialize.StreamBufferCapacity.Should().Be(64);
        initialize.EventModules.Should().Be("llm_handler,tool_handler");
        initialize.EventRoutes.Should().Contain("event.type");
    }

    [Fact]
    public async Task HandleChatRequest_WhenResolvedRoleTypeDoesNotImplementIRoleAgent_ShouldThrow()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime, roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeNonRoleAgent)));
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_x", "RoleX"), "wf_error");

        Func<Task> act = () => agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "bad-role",
            SessionId = "session-3",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not implement IRoleAgent*");
    }

    [Fact]
    public async Task HandleReplaceWorkflowDefinitionAndExecute_WhenYamlInvalid_ShouldKeepPreviousWorkflowState()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent();
        agent.EventPublisher = publisher;
        var originalYaml = BuildValidWorkflowYaml("role_a", "RoleA");
        await agent.BindWorkflowDefinitionAsync(originalYaml, "wf_valid");

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = "not: [valid",
            Input = "hello",
        });

        agent.State.WorkflowYaml.Should().Be(originalYaml);
        agent.State.WorkflowName.Should().Be("wf_valid");
        agent.State.Compiled.Should().BeTrue();
        publisher.Published.Select(x => x.evt).OfType<ChatResponseEvent>().Single().Content.Should().Contain("compilation failed");
    }

    [Fact]
    public async Task ActivateAsync_ShouldReplayPersistedRunState()
    {
        var eventStore = new InMemoryEventStore();
        var runtime1 = new RecordingActorRuntime();

        var agent1 = CreateRunAgent(runtime: runtime1, eventStore: eventStore);
        await agent1.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_replay");
        await agent1.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "run-replay",
        });

        var runtime2 = new RecordingActorRuntime();
        var agent2 = CreateRunAgent(runtime: runtime2, eventStore: eventStore);
        await agent2.ActivateAsync();

        agent2.State.Compiled.Should().BeTrue();
        agent2.State.WorkflowName.Should().Be("wf_replay");
        agent2.State.RunId.Should().Be("run-replay");
        agent2.State.Status.Should().Be("active");
        runtime2.CreatedActors.Should().ContainSingle();
        runtime2.CreatedActors.Single().Id.Should().Be("workflow-run-root:role_a");
    }

    [Fact]
    public async Task HandleCompletionEnvelope_WhenSubWorkflowCompletes_ShouldPublishChildRunTrackingMetadata()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_parent");

        agent.State.RunId = "parent-run";
        agent.State.PendingSubWorkflows["child-run-1"] = new WorkflowPendingSubWorkflowState
        {
            InvocationId = "invoke-1",
            ParentStepId = "call-step",
            WorkflowName = "sub_flow",
            Lifecycle = "transient",
            ChildActorId = "child-actor-1",
            ChildRunId = "child-run-1",
            ParentRunId = "parent-run",
        };

        await agent.HandleEventAsync(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "sub_flow",
                RunId = "child-run-1",
                Success = true,
                Output = "child-done",
            },
            publisherId: "child-actor-1",
            direction: EventDirection.Both));

        var completed = publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("call-step");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("child-done");
        completed.Metadata["workflow_call.invocation_id"].Should().Be("invoke-1");
        completed.Metadata["workflow_call.workflow_name"].Should().Be("sub_flow");
        completed.Metadata["workflow_call.lifecycle"].Should().Be("transient");
        completed.Metadata["workflow_call.child_actor_id"].Should().Be("child-actor-1");
        completed.Metadata["workflow_call.child_run_id"].Should().Be("child-run-1");
        agent.State.PendingSubWorkflows.Should().NotContainKey("child-run-1");
        runtime.Unlinked.Should().Contain("child-actor-1");
        runtime.Destroyed.Should().Contain("child-actor-1");
    }

    private static WorkflowRunGAgent CreateRunAgent(
        IActorRuntime? runtime = null,
        IRoleAgentTypeResolver? roleResolver = null,
        IEnumerable<IWorkflowModulePack>? modulePacks = null,
        IEventStore? eventStore = null)
    {
        runtime ??= new RecordingActorRuntime();
        roleResolver ??= new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        modulePacks ??= [new EmptyWorkflowModulePack()];
        eventStore ??= new InMemoryEventStore();

        var services = CreateServices(eventStore);
        var agent = new WorkflowRunGAgent(runtime, roleResolver, modulePacks)
        {
            Services = services,
        };
        AssignAgentId(agent, "workflow-run-root");
        agent.EventSourcingBehaviorFactory =
            services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunState>>();
        return agent;
    }
}

public sealed class WorkflowGAgentCoverageTests
{
    [Fact]
    public async Task HandleChatRequest_WhenNotCompiled_ShouldPublishFailureResponse()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateDefinitionAgent(runtime: runtime);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync(string.Empty, "wf_invalid");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        runtime.CreateCalls.Should().Be(0);
        publisher.Published.Should().ContainSingle();
        publisher.Published.Single().evt.Should().BeOfType<ChatResponseEvent>();
    }

    [Fact]
    public async Task HandleChatRequest_WhenCompiled_ShouldCreateRunActorAndForwardDefinitionAndRequest()
    {
        var runAgent = new RecordingRunAgent("actor-1");
        var runtime = new RecordingActorRuntime
        {
            ActorFactory = (agentType, actorId) =>
                agentType == typeof(WorkflowRunGAgent)
                    ? new FakeActor(actorId, runAgent)
                    : null,
        };
        var agent = CreateDefinitionAgent(runtime: runtime);
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_definition");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        runtime.CreateCalls.Should().Be(1);
        runtime.Linked.Should().ContainSingle(x => x.parent == "workflow-definition-root" && x.child == "actor-1");
        runAgent.Received.Should().HaveCount(2);

        runAgent.Received[0].Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
        var bind = runAgent.Received[0].Payload!.Unpack<BindWorkflowDefinitionEvent>();
        bind.WorkflowName.Should().Be("wf_definition");
        bind.WorkflowYaml.Should().Contain("name: wf_valid");

        runAgent.Received[1].Payload!.Is(ChatRequestEvent.Descriptor).Should().BeTrue();
        var request = runAgent.Received[1].Payload!.Unpack<ChatRequestEvent>();
        request.Prompt.Should().Be("hello");
        request.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task HandleReplaceWorkflowDefinitionAndExecute_WhenYamlInvalid_ShouldKeepPreviousWorkflowState()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateDefinitionAgent();
        agent.EventPublisher = publisher;
        var originalYaml = BuildValidWorkflowYaml("role_a", "RoleA");
        await agent.BindWorkflowDefinitionAsync(originalYaml, "wf_original");

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = "not: [valid",
            Input = "hello",
        });

        agent.State.WorkflowYaml.Should().Be(originalYaml);
        agent.State.WorkflowName.Should().Be("wf_original");
        agent.State.Compiled.Should().BeTrue();
        publisher.Published.Select(x => x.evt).OfType<ChatResponseEvent>().Single().Content.Should().Contain("compilation failed");
    }

    [Fact]
    public async Task HandleReplaceWorkflowDefinitionAndExecute_WhenYamlValid_ShouldRebindAndStartRun()
    {
        var runAgent = new RecordingRunAgent("actor-1");
        var runtime = new RecordingActorRuntime
        {
            ActorFactory = (agentType, actorId) =>
                agentType == typeof(WorkflowRunGAgent)
                    ? new FakeActor(actorId, runAgent)
                    : null,
        };
        var agent = CreateDefinitionAgent(runtime: runtime);

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = BuildValidWorkflowYaml("role_a", "RoleA"),
            Input = "dynamic-input",
        });

        agent.State.WorkflowName.Should().Be("wf_valid");
        agent.State.Compiled.Should().BeTrue();
        runAgent.Received.Should().HaveCount(2);

        var bind = runAgent.Received[0].Payload!.Unpack<BindWorkflowDefinitionEvent>();
        bind.WorkflowName.Should().Be("wf_valid");

        var request = runAgent.Received[1].Payload!.Unpack<ChatRequestEvent>();
        request.Prompt.Should().Be("dynamic-input");
        request.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleWorkflowCompleted_ShouldUpdateCountersAndPublishTerminalMessage()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateDefinitionAgent();
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_definition");

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_definition",
            RunId = "run-1",
            Success = true,
            Output = "done",
        });

        agent.State.TotalExecutions.Should().Be(1);
        agent.State.SuccessfulExecutions.Should().Be(1);
        agent.State.FailedExecutions.Should().Be(0);

        var terminal = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single();
        terminal.SessionId.Should().Be("run-1");
        terminal.Content.Should().Be("done");
    }

    [Fact]
    public async Task HandleWorkflowCompletionEnvelope_WhenPublisherIsRunActor_ShouldUpdateCounters()
    {
        var runtime = new RecordingActorRuntime();
        var runAgent = CreateRunAgentForInspection();
        runtime.AddActor(new FakeActor("run-1", runAgent));
        var agent = CreateDefinitionAgent(runtime: runtime);
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_definition");

        await agent.HandleEventAsync(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "wf_definition",
                RunId = "run-1",
                Success = true,
                Output = "done",
            },
            publisherId: "run-1",
            direction: EventDirection.Both));

        agent.State.TotalExecutions.Should().Be(1);
        agent.State.SuccessfulExecutions.Should().Be(1);
        agent.State.FailedExecutions.Should().Be(0);
    }

    [Fact]
    public async Task HandleWorkflowCompletionEnvelope_WhenPublisherIsNotRunActor_ShouldIgnore()
    {
        var runtime = new RecordingActorRuntime();
        runtime.AddActor(new FakeActor("not-a-run", new FakeNonRoleAgent("not-a-run")));
        var agent = CreateDefinitionAgent(runtime: runtime);
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_definition");

        await agent.HandleEventAsync(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "wf_definition",
                RunId = "run-1",
                Success = false,
                Error = "bad",
            },
            publisherId: "not-a-run",
            direction: EventDirection.Both));

        agent.State.TotalExecutions.Should().Be(0);
        agent.State.SuccessfulExecutions.Should().Be(0);
        agent.State.FailedExecutions.Should().Be(0);
    }

    private static WorkflowGAgent CreateDefinitionAgent(
        IActorRuntime? runtime = null,
        IEventStore? eventStore = null)
    {
        runtime ??= new RecordingActorRuntime();
        eventStore ??= new InMemoryEventStore();
        var services = CreateServices(eventStore);

        var agent = new WorkflowGAgent(
            runtime,
            [])
        {
            Services = services,
        };
        AssignAgentId(agent, "workflow-definition-root");
        agent.EventSourcingBehaviorFactory =
            services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowState>>();
        return agent;
    }

    private static WorkflowRunGAgent CreateRunAgentForInspection()
    {
        var services = CreateServices(new InMemoryEventStore());
        var agent = new WorkflowRunGAgent(new RecordingActorRuntime(), new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)), [])
        {
            Services = services,
        };
        AssignAgentId(agent, "run-1");
        agent.EventSourcingBehaviorFactory =
            services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunState>>();
        return agent;
    }
}

file static class WorkflowGAgentCoverageTestHelpers
{
    public static void AssignAgentId(IAgent agent, string id)
    {
        var method = agent.GetType().BaseType?.GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(agent, [id]);
    }

    public static EventEnvelope Envelope(IMessage message, string publisherId, EventDirection direction) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            PublisherId = publisherId,
            Direction = direction,
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(message),
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

    public static string BuildValidWorkflowYaml(
        string roleId,
        string roleName,
        string? provider = null,
        string? model = null)
    {
        var providerLine = string.IsNullOrWhiteSpace(provider) ? string.Empty : $"\n    provider: \"{provider}\"";
        var modelLine = string.IsNullOrWhiteSpace(model) ? string.Empty : $"\n    model: \"{model}\"";
        return $$"""
                 name: wf_valid
                 roles:
                   - id: "{{roleId}}"
                     name: "{{roleName}}"
                     system_prompt: "helpful role"{{providerLine}}{{modelLine}}
                 steps:
                   - id: step_1
                     type: transform
                 """;
    }

    public static string BuildWorkflowYamlWithFullRoleConfig() =>
        """
        name: wf_valid
        roles:
          - id: role_a
            name: RoleA
            provider: openai
            model: gpt-4o-mini
            system_prompt: helpful role
            temperature: 0.2
            max_tokens: 256
            max_tool_rounds: 4
            max_history_messages: 30
            stream_buffer_capacity: 64
            extensions:
              event_modules: "llm_handler,tool_handler"
              event_routes: |
                event.type == ChatRequestEvent -> tool_handler
        steps:
          - id: step_1
            type: transform
        """;

    public static ServiceProvider CreateServices(IEventStore eventStore) =>
        new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
                sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
}

file sealed class RecordingEventPublisher : IEventPublisher
{
    public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
    public List<(string targetActorId, IMessage evt)> Sent { get; } = [];

    public Task PublishAsync<TEvent>(
        TEvent evt,
        EventDirection direction = EventDirection.Down,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null)
        where TEvent : IMessage
    {
        Published.Add((evt, direction));
        return Task.CompletedTask;
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null)
        where TEvent : IMessage
    {
        Sent.Add((targetActorId, evt));
        return Task.CompletedTask;
    }
}

file sealed class RecordingActorRuntime : IActorRuntime
{
    private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

    public Func<SystemType, string, IActor?>? ActorFactory { get; init; }

    public int CreateCalls { get; private set; }

    public List<IActor> CreatedActors { get; } = [];

    public List<(string parent, string child)> Linked { get; } = [];

    public List<string> Destroyed { get; } = [];

    public List<string> Unlinked { get; } = [];

    public void AddActor(IActor actor)
    {
        _actors[actor.Id] = actor;
        if (CreatedActors.All(existing => existing.Id != actor.Id))
            CreatedActors.Add(actor);
    }

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent =>
        CreateAsync(typeof(TAgent), id, ct);

    public Task<IActor> CreateAsync(SystemType agentType, string? id = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var actorId = id ?? $"actor-{CreateCalls + 1}";
        if (_actors.TryGetValue(actorId, out var existing))
            return Task.FromResult(existing);

        CreateCalls++;
        var actor =
            ActorFactory?.Invoke(agentType, actorId) ??
            CreateDefaultActor(agentType, actorId);
        _actors[actorId] = actor;
        CreatedActors.Add(actor);
        return Task.FromResult(actor);
    }

    public Task DestroyAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Destroyed.Add(id);
        _actors.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IActor?> GetAsync(string id) =>
        Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

    public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Linked.Add((parentId, childId));
        return Task.CompletedTask;
    }

    public Task UnlinkAsync(string childId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Unlinked.Add(childId);
        return Task.CompletedTask;
    }

    private static IActor CreateDefaultActor(SystemType agentType, string actorId)
    {
        IAgent agent = agentType == typeof(FakeRoleAgent)
            ? new FakeRoleAgent(actorId)
            : new FakeNonRoleAgent(actorId);
        return new FakeActor(actorId, agent);
    }
}

file sealed class FakeActor(string id, IAgent agent) : IActor
{
    public string Id { get; } = id;

    public IAgent Agent { get; } = agent;

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
        Agent.HandleEventAsync(envelope, ct);

    public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
}

file sealed class RecordingRunAgent(string id) : IAgent
{
    public string Id { get; } = id;

    public List<EventEnvelope> Received { get; } = [];

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        Received.Add(envelope);
        return Task.CompletedTask;
    }

    public Task<string> GetDescriptionAsync() => Task.FromResult("recording-run");

    public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<SystemType>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class FakeRoleAgent(string id) : IRoleAgent
{
    public string Id { get; } = id;

    public string RoleName { get; private set; } = string.Empty;

    public InitializeRoleAgentEvent? LastInitializeEvent { get; private set; }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope.Payload?.Is(InitializeRoleAgentEvent.Descriptor) == true)
        {
            var evt = envelope.Payload.Unpack<InitializeRoleAgentEvent>();
            LastInitializeEvent = evt;
            RoleName = evt.RoleName;
        }

        return Task.CompletedTask;
    }

    public Task<string> GetDescriptionAsync() => Task.FromResult("fake-role");

    public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<SystemType>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class FakeNonRoleAgent(string id) : IAgent
{
    public string Id { get; } = id;

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("fake-non-role");

    public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<SystemType>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class StaticRoleAgentTypeResolver(SystemType roleAgentType) : IRoleAgentTypeResolver
{
    public SystemType ResolveRoleAgentType() => roleAgentType;
}

file sealed class EmptyWorkflowModulePack : IWorkflowModulePack
{
    public string Name => "empty";

    public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } = [];
}
