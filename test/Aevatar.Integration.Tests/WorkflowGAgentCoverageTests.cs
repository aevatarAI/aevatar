using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Any = Google.Protobuf.WellKnownTypes.Any;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Aevatar.Integration.Tests;

public class WorkflowGAgentCoverageTests
{
    [Fact]
    public async Task WorkflowGAgent_WhenSwitchingWorkflowName_ShouldThrow()
    {
        var agent = CreateDefinitionAgent();
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_a");

        var act = () => agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_b");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot switch*");
    }

    [Fact]
    public async Task WorkflowGAgent_WhenYamlInvalid_ShouldMarkInvalidAndDescribe()
    {
        var agent = CreateDefinitionAgent();

        await agent.BindWorkflowDefinitionAsync("", "wf_invalid");
        var description = await agent.GetDescriptionAsync();

        agent.State.Compiled.Should().BeFalse();
        agent.State.CompilationError.Should().Be("workflow yaml is empty");
        description.Should().Contain("invalid");
        description.Should().Contain("wf_invalid");
    }

    [Fact]
    public async Task WorkflowGAgent_ReplayContract_ShouldRestoreBoundDefinitionAfterReactivate()
    {
        var eventStore = new InMemoryEventStore();
        var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        };

        var agent1 = CreateDefinitionAgent(eventStore);
        await agent1.ActivateAsync();
        await agent1.BindWorkflowDefinitionAsync(
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_replay",
            inlineWorkflowYamls);
        await agent1.DeactivateAsync();

        var persisted = await eventStore.GetEventsAsync(agent1.Id);
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(BindWorkflowDefinitionEvent), StringComparison.Ordinal));

        var agent2 = CreateDefinitionAgent(eventStore);
        await agent2.ActivateAsync();

        agent2.State.WorkflowName.Should().Be("wf_replay");
        agent2.State.Compiled.Should().BeTrue();
        agent2.State.InlineWorkflowYamls.Should().ContainKey("sub_flow");
        (await agent2.GetDescriptionAsync()).Should().Contain("compiled");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenNotCompiled_ShouldPublishFailureResponse()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync("definition-1", "", "wf_invalid", runId: "run-invalid");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        runtime.CreateCalls.Should().Be(0);
        var response = publisher.Published.Select(x => x.evt).OfType<ChatResponseEvent>().Single();
        response.Content.Should().Contain("not definition-bound or compiled");
        response.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenCompiled_ShouldCreateRoleActorsOnlyOnceAndPersistRunStart()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime,
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)),
            eventStore: eventStore);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-1");

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "first", SessionId = "s1" });
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "second", SessionId = "s2" });

        runtime.CreateCalls.Should().Be(1);
        runtime.Linked.Should().ContainSingle();
        runtime.Linked[0].child.Should().EndWith(":role_a");

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.RoleName.Should().Be("RoleA");
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        roleAgent.LastInitializeEvent!.SystemPrompt.Should().Be("helpful role");

        var starts = publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().ToList();
        starts.Should().HaveCount(2);
        starts.Should().OnlyContain(x => x.WorkflowName == "wf_valid" && x.RunId == "run-1");

        agent.State.Status.Should().Be("running");
        agent.State.Input.Should().Be("second");
        agent.State.DefinitionActorId.Should().Be("definition-1");

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Count(x => x.EventType.Contains(nameof(WorkflowRunExecutionStartedEvent), StringComparison.Ordinal))
            .Should()
            .Be(2);
    }

    [Fact]
    public async Task WorkflowRunGAgent_ShouldPassFullRoleConfigurationToInitializeEvent()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime,
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)));
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildWorkflowYamlWithFullRoleConfig(),
            "wf_role_fields",
            runId: "run-role");

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        var initializeEvent = runtime.CreatedActors.Single().Agent
            .Should().BeOfType<FakeRoleAgent>().Subject.LastInitializeEvent!;
        initializeEvent.RoleName.Should().Be("RoleA");
        initializeEvent.ProviderName.Should().Be("openai");
        initializeEvent.Model.Should().Be("gpt-4o-mini");
        initializeEvent.SystemPrompt.Should().Be("helpful role");
        initializeEvent.HasTemperature.Should().BeTrue();
        initializeEvent.Temperature.Should().BeApproximately(0.2f, 0.0001f);
        initializeEvent.MaxTokens.Should().Be(256);
        initializeEvent.MaxToolRounds.Should().Be(4);
        initializeEvent.MaxHistoryMessages.Should().Be(30);
        initializeEvent.StreamBufferCapacity.Should().Be(64);
        initializeEvent.EventModules.Should().Be("llm_handler,tool_handler");
        initializeEvent.EventRoutes.Should().Contain("event.type");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenRebindingDefinition_ShouldResetExecutionStateAndDestroyOldChildren()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime,
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)));
        SetAgentId(agent, "workflow-run-rebind");
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-1");
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "first", SessionId = "s1" });
        var oldChildActorId = runtime.CreatedActors.Single().Id;
        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));
        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_valid",
            RunId = "run-1",
            Success = true,
            Output = "done-a",
        });
        runtime.ThrowOnGetAsyncActorId = agent.Id;

        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_b", "RoleB"),
            "wf_valid",
            runId: "run-2");

        agent.State.Status.Should().Be("bound");
        agent.State.Input.Should().BeEmpty();
        agent.State.FinalOutput.Should().BeEmpty();
        agent.State.FinalError.Should().BeEmpty();
        agent.State.ExecutionStates.Should().BeEmpty();
        runtime.Unlinked.Should().Contain(oldChildActorId);
        runtime.Destroyed.Should().Contain(oldChildActorId);

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "second", SessionId = "s2" });

        runtime.Linked.Should().Contain(x => x.child.EndsWith(":role_b", StringComparison.Ordinal));
        runtime.CreatedActors.Select(x => x.Id).Should().Contain($"{agent.Id}:role_b");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenResolvedAgentNotIRoleAgent_ShouldThrow()
    {
        var agent = CreateRunAgent(
            runtime: new RecordingActorRuntime(),
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeNonRoleAgent)));
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_x", "RoleX"),
            "wf_error",
            runId: "run-error");

        var act = () => agent.HandleChatRequest(new ChatRequestEvent { Prompt = "x", SessionId = "s" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not implement IRoleAgent*");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenRoleIdMissing_ShouldThrow()
    {
        var agent = CreateRunAgent(
            runtime: new RecordingActorRuntime(),
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)));
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("", "RoleNoId"),
            "wf_missing_role",
            runId: "run-missing-role");

        var act = () => agent.HandleChatRequest(new ChatRequestEvent { Prompt = "x", SessionId = "s" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role id is required*");
    }

    [Fact]
    public async Task WorkflowRunGAgent_ReplayContract_ShouldRestoreTerminalStateAfterReactivate()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();

        var agent1 = CreateRunAgent(eventStore: eventStore);
        agent1.EventPublisher = publisher;
        await agent1.ActivateAsync();
        await agent1.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_replay",
            runId: "run-replay");
        await agent1.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_replay",
            RunId = "run-replay",
            Success = true,
            Output = "done",
        });
        await agent1.DeactivateAsync();

        var persisted = await eventStore.GetEventsAsync(agent1.Id);
        persisted.Should().Contain(x => x.EventType.Contains(nameof(BindWorkflowRunDefinitionEvent), StringComparison.Ordinal));
        persisted.Should().Contain(x => x.EventType.Contains(nameof(WorkflowCompletedEvent), StringComparison.Ordinal));

        var agent2 = CreateRunAgent(eventStore: eventStore);
        await agent2.ActivateAsync();

        agent2.State.WorkflowName.Should().Be("wf_replay");
        agent2.State.RunId.Should().Be("run-replay");
        agent2.State.Status.Should().Be("completed");
        agent2.State.FinalOutput.Should().Be("done");
        agent2.State.Compiled.Should().BeTrue();

        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>()
            .Should().ContainSingle(x => x.Content == "done");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenSelfWorkflowCompletedEnvelopeArrives_ShouldFinalizeRun()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent();
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-self");

        await agent.HandleEventAsync(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "wf_valid",
                RunId = "run-self",
                Success = true,
                Output = "done-via-envelope",
            },
            agent.Id,
            TopologyAudience.Self));

        agent.State.Status.Should().Be("completed");
        agent.State.FinalOutput.Should().Be("done-via-envelope");
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>()
            .Should().ContainSingle(x => x.Content == "done-via-envelope");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenExternalUnknownWorkflowCompletedEnvelopeArrives_ShouldIgnore()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent();
        agent.EventPublisher = publisher;

        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "wf_external",
                RunId = "run-external",
                Success = true,
                Output = "ok",
            },
            "external-child",
            TopologyAudience.ParentAndChildren));

        agent.State.Status.Should().BeEmpty();
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenDynamicYamlInvalid_ShouldPreserveBoundDefinitionSnapshot()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent(eventStore: eventStore);
        agent.EventPublisher = publisher;
        var originalYaml = BuildValidWorkflowYaml("role_a", "RoleA");
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            originalYaml,
            "wf_valid",
            runId: "run-dynamic");

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = "name: broken\nroles: [",
            Input = "hello",
        });

        agent.State.WorkflowYaml.Should().Be(originalYaml);
        agent.State.WorkflowName.Should().Be("wf_valid");
        agent.State.Compiled.Should().BeTrue();
        publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().Should().BeEmpty();
        publisher.Published.Select(x => x.evt).OfType<ChatResponseEvent>()
            .Should().ContainSingle(x => x.Content.Contains("Dynamic workflow YAML compilation failed", StringComparison.Ordinal));

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Count(x => x.EventType.Contains(nameof(BindWorkflowRunDefinitionEvent), StringComparison.Ordinal))
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenReplacingDefinition_ShouldResetExecutionStateAndRebuildChildTopology()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime,
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)));
        SetAgentId(agent, "workflow-run-replace");
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-replace");
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "first", SessionId = "s1" });
        var oldChildActorId = runtime.CreatedActors.Single().Id;
        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));
        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_valid",
            RunId = "run-replace",
            Success = true,
            Output = "done-a",
        });
        runtime.ThrowOnGetAsyncActorId = agent.Id;

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = BuildValidWorkflowYaml("role_b", "RoleB"),
            Input = "second",
        });

        runtime.Unlinked.Should().Contain(oldChildActorId);
        runtime.Destroyed.Should().Contain(oldChildActorId);
        agent.State.ExecutionStates.Should().BeEmpty();
        agent.State.FinalOutput.Should().BeEmpty();
        agent.State.FinalError.Should().BeEmpty();
        agent.State.Status.Should().Be("running");
        agent.State.Input.Should().Be("second");
        runtime.Linked.Should().Contain(x => x.child.EndsWith(":role_b", StringComparison.Ordinal));
        runtime.CreatedActors.Select(x => x.Id).Should().Contain($"{agent.Id}:role_b");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenSingletonSubWorkflowInvoked_ShouldPersistPendingAndReuseChildActor()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateRunAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-1",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            Input = "payload-a",
            Lifecycle = "singleton",
        });
        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-2",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            Input = "payload-b",
            Lifecycle = "singleton",
        });

        runtime.CreateCalls.Should().Be(1);
        agent.State.SubWorkflowBindings.Should().ContainSingle();
        agent.State.PendingSubWorkflowInvocations.Should().HaveCount(2);
        agent.State.PendingSubWorkflowInvocations.Select(x => x.ChildActorId).Distinct().Should().ContainSingle();
        agent.State.PendingChildRunIdsByParentRunId.Should().ContainKey("parent-run");
        publisher.Sent.Should().HaveCount(2);
        publisher.Sent.Select(x => x.targetActorId).Distinct().Should().ContainSingle();
        publisher.Sent.Select(x => x.evt).OfType<StartWorkflowEvent>().Should().HaveCount(2);

        var childAgent = runtime.CreatedChildWorkflowAgents.Single();
        childAgent.BindEvents.Should().ContainSingle();
        childAgent.StartEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenChildWorkflowCompletes_ShouldTranslateToParentStepCompleted()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateRunAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-child",
            ParentRunId = "parent-run",
            ParentStepId = "step-child",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });

        var pending = agent.State.PendingSubWorkflowInvocations.Single();
        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "sub_flow",
                RunId = pending.ChildRunId,
                Success = true,
                Output = "child-done",
            },
            pending.ChildActorId,
            TopologyAudience.ParentAndChildren));

        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
        agent.State.PendingChildRunIdsByParentRunId.Should().BeEmpty();

        var parentCompletion = publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        parentCompletion.StepId.Should().Be("step-child");
        parentCompletion.RunId.Should().Be("parent-run");
        parentCompletion.Success.Should().BeTrue();
        parentCompletion.Output.Should().Be("child-done");
        parentCompletion.Annotations["workflow_call.child_run_id"].Should().Be(pending.ChildRunId);
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenParentRunCompletes_ShouldCleanupPendingInvocationsAndDestroyNonSingletonChildren()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateRunAgent(runtime: runtime, workflowResolver: resolver);

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-singleton",
            ParentRunId = "parent-run",
            ParentStepId = "step-singleton",
            WorkflowName = "sub_flow",
            Input = "payload-singleton",
            Lifecycle = "singleton",
        });
        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-transient",
            ParentRunId = "parent-run",
            ParentStepId = "step-transient",
            WorkflowName = "sub_flow",
            Input = "payload-transient",
            Lifecycle = "transient",
        });

        var childActorByLifecycle = agent.State.PendingSubWorkflowInvocations
            .ToDictionary(x => x.Lifecycle, x => x.ChildActorId, StringComparer.OrdinalIgnoreCase);

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_parent",
            RunId = "parent-run",
            Success = true,
            Output = "done",
        });

        agent.State.Status.Should().Be("completed");
        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
        agent.State.PendingChildRunIdsByParentRunId.Should().BeEmpty();

        runtime.Unlinked.Should().Contain(childActorByLifecycle["transient"]);
        runtime.Destroyed.Should().Contain(childActorByLifecycle["transient"]);
        runtime.Unlinked.Should().NotContain(childActorByLifecycle["singleton"]);
        runtime.Destroyed.Should().NotContain(childActorByLifecycle["singleton"]);
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenRunCompletes_ShouldCleanupRoleActors()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime,
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)));

        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-complete");
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "first", SessionId = "s1" });

        var roleActorId = runtime.CreatedActors.Single().Id;

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_valid",
            RunId = "run-complete",
            Success = true,
            Output = "done",
        });

        agent.State.Status.Should().Be("completed");
        runtime.Unlinked.Should().Contain(roleActorId);
        runtime.Destroyed.Should().Contain(roleActorId);
        runtime.CreatedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_ReplayContract_ShouldRestoreModuleStateAndModulesAfterReactivate()
    {
        var eventStore = new InMemoryEventStore();
        var factory = new RecordingEventModuleFactory();
        var configurator = new RecordingModuleConfigurator();
        var pack = new TestModulePack(
            [new StaticDependencyExpander(0, "module_on_activate")],
            [configurator]);

        var agent1 = CreateRunAgent(
            eventModuleFactory: factory,
            packs: [pack],
            eventStore: eventStore);
        await agent1.ActivateAsync();
        await agent1.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_activate",
            runId: "run-activate");
        await agent1.UpsertExecutionStateAsync(
            "module_on_activate",
            Any.Pack(new Google.Protobuf.WellKnownTypes.StringValue { Value = "{\"status\":\"hot\"}" }));
        await agent1.DeactivateAsync();

        var agent2 = CreateRunAgent(
            eventModuleFactory: factory,
            packs: [pack],
            eventStore: eventStore);
        await agent2.ActivateAsync();

        agent2.State.Compiled.Should().BeTrue();
        agent2.State.RunId.Should().Be("run-activate");
        agent2.GetModules().Select(x => x.Name).Should().BeEquivalentTo(
            "workflow_execution_kernel",
            "workflow_execution_bridge");
        factory.CreatedNames.Count(x => x == "module_on_activate").Should().Be(2);
        agent2.GetExecutionState("module_on_activate")!
            .Unpack<Google.Protobuf.WellKnownTypes.StringValue>()
            .Value
            .Should()
            .Be("{\"status\":\"hot\"}");
        configurator.Configured.Should().Contain("module_on_activate:wf_valid");
    }

    private static WorkflowGAgent CreateDefinitionAgent(IEventStore? eventStore = null)
    {
        eventStore ??= new InMemoryEventStore();
        var services = BuildServices(eventStore, workflowResolver: null);
        var agent = new WorkflowGAgent
        {
            Services = services,
        };
        agent.EventSourcingBehaviorFactory =
            services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowState>>();
        return agent;
    }

    private static WorkflowRunGAgent CreateRunAgent(
        RecordingActorRuntime? runtime = null,
        IRoleAgentTypeResolver? roleResolver = null,
        IEventModuleFactory<IWorkflowExecutionContext>? eventModuleFactory = null,
        IEnumerable<IWorkflowModulePack>? packs = null,
        IEventStore? eventStore = null,
        IWorkflowDefinitionResolver? workflowResolver = null)
    {
        runtime ??= new RecordingActorRuntime();
        roleResolver ??= new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        eventModuleFactory ??= new RecordingEventModuleFactory();
        packs ??= [];
        eventStore ??= new InMemoryEventStore();

        var services = BuildServices(eventStore, workflowResolver);
        var agent = new WorkflowRunGAgent(runtime, runtime, roleResolver, eventModuleFactory, packs, workflowResolver)
        {
            Services = services,
        };
        agent.EventSourcingBehaviorFactory =
            services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunState>>();
        return agent;
    }

    private static ServiceProvider BuildServices(
        IEventStore eventStore,
        IWorkflowDefinitionResolver? workflowResolver)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
                sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));

        if (workflowResolver != null)
            services.AddSingleton(workflowResolver);

        return services.BuildServiceProvider();
    }

    private static EventEnvelope Envelope(IMessage message, string publisherId, TopologyAudience direction)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherId, direction),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };
    }

    private static void SetAgentId(WorkflowRunGAgent agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private static string BuildValidWorkflowYaml(
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

    private static string BuildWorkflowYamlWithFullRoleConfig()
    {
        return """
               name: wf_valid
               roles:
                 - id: role_a
                   name: RoleA
                   system_prompt: "helpful role"
                   provider: openai
                   model: gpt-4o-mini
                   temperature: 0.2
                   max_tokens: 256
                   max_tool_rounds: 4
                   max_history_messages: 30
                   stream_buffer_capacity: 64
                   event_modules: "llm_handler,tool_handler"
                   event_routes: |
                     event.type == ChatRequestEvent -> llm_handler
               steps:
                 - id: step_1
                   type: transform
               """;
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(IMessage evt, TopologyAudience direction)> Published { get; } = [];
        public List<(string targetActorId, IMessage evt)> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = sourceEnvelope;
            _ = options;
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Sent.Add((targetActorId, evt));
            _ = sourceEnvelope;
            _ = options;
            Published.Add((evt, TopologyAudience.Self));
            return Task.CompletedTask;
        }

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = audience;
            _ = sourceEnvelope;
            _ = options;
            Published.Add((evt, TopologyAudience.Self));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public int CreateCalls { get; private set; }
        public List<FakeActor> CreatedActors { get; } = [];
        public List<FakeWorkflowRunChildAgent> CreatedChildWorkflowAgents { get; } = [];
        public List<(string parent, string child)> Linked { get; } = [];
        public List<string> Destroyed { get; } = [];
        public List<string> Unlinked { get; } = [];
        public string? ThrowOnGetAsyncActorId { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            return CreateAsync(typeof(TAgent), id, ct);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"actor-{CreateCalls + 1}";
            var existing = CreatedActors.FirstOrDefault(x => x.Id == actorId);
            if (existing != null)
                return Task.FromResult<IActor>(existing);

            CreateCalls++;
            IAgent agent = agentType == typeof(FakeRoleAgent)
                ? new FakeRoleAgent(actorId)
                : agentType == typeof(FakeNonRoleAgent)
                    ? new FakeNonRoleAgent(actorId)
                    : agentType == typeof(WorkflowRunGAgent)
                        ? CreateChildWorkflowRunAgent(actorId)
                        : throw new InvalidOperationException($"Unsupported agent type '{agentType.FullName}'.");

            var actor = new FakeActor(actorId, agent);
            CreatedActors.Add(actor);
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            Destroyed.Add(id);
            CreatedActors.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            string.Equals(id, ThrowOnGetAsyncActorId, StringComparison.Ordinal)
                ? throw new InvalidOperationException($"Unexpected self GetAsync for actor '{id}'.")
                : Task.FromResult<IActor?>(CreatedActors.FirstOrDefault(x => x.Id == id));

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actor = CreatedActors.FirstOrDefault(x => x.Id == actorId)
                        ?? throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(CreatedActors.Any(x => x.Id == id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            Linked.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            Unlinked.Add(childId);
            return Task.CompletedTask;
        }

        private FakeWorkflowRunChildAgent CreateChildWorkflowRunAgent(string actorId)
        {
            var child = new FakeWorkflowRunChildAgent(actorId);
            CreatedChildWorkflowAgents.Add(child);
            return child;
        }
    }

    private sealed class StaticWorkflowDefinitionResolver(IReadOnlyDictionary<string, string> definitions)
        : IWorkflowDefinitionResolver
    {
        public Task<string?> GetWorkflowYamlAsync(string workflowName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(definitions.TryGetValue(workflowName, out var yaml) ? yaml : null);
        }
    }

    private sealed class FakeActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;

        public Task ActivateAsync(CancellationToken ct = default) => Agent.ActivateAsync(ct);
        public Task DeactivateAsync(CancellationToken ct = default) => Agent.DeactivateAsync(ct);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Agent.HandleEventAsync(envelope, ct);
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeRoleAgent(string id) : IRoleAgent
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
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWorkflowRunChildAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public List<BindWorkflowRunDefinitionEvent> BindEvents { get; } = [];
        public List<StartWorkflowEvent> StartEvents { get; } = [];

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope.Payload?.Is(BindWorkflowRunDefinitionEvent.Descriptor) == true)
                BindEvents.Add(envelope.Payload.Unpack<BindWorkflowRunDefinitionEvent>());

            if (envelope.Payload?.Is(StartWorkflowEvent.Descriptor) == true)
                StartEvents.Add(envelope.Payload.Unpack<StartWorkflowEvent>());

            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-child-run");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeNonRoleAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-non-role");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StaticRoleAgentTypeResolver(Type roleAgentType) : IRoleAgentTypeResolver
    {
        public Type ResolveRoleAgentType() => roleAgentType;
    }

    private sealed class RecordingEventModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public List<string> CreatedNames { get; } = [];

        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            CreatedNames.Add(name);
            module = new RecordingEventModule(name);
            return true;
        }
    }

    private sealed class RecordingEventModule(string name) : IEventModule<IWorkflowExecutionContext>
    {
        public string Name { get; } = name;
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => false;
        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StaticDependencyExpander(int order, params string[] moduleNames) : IWorkflowModuleDependencyExpander
    {
        public int Order { get; } = order;

        public void Expand(WorkflowDefinition? workflow, ISet<string> names)
        {
            _ = workflow;
            foreach (var moduleName in moduleNames)
                names.Add(moduleName);
        }
    }

    private sealed class RecordingModuleConfigurator : IWorkflowModuleConfigurator
    {
        public int Order => 0;
        public List<string> Configured { get; } = [];

        public void Configure(IEventModule<IWorkflowExecutionContext> module, WorkflowDefinition workflow)
        {
            Configured.Add($"{module.Name}:{workflow.Name}");
        }
    }

    private sealed class TestModulePack(
        IReadOnlyList<IWorkflowModuleDependencyExpander> expanders,
        IReadOnlyList<IWorkflowModuleConfigurator> configurators) : IWorkflowModulePack
    {
        public string Name => "test-pack";
        public IReadOnlyList<WorkflowModuleRegistration> Modules => [];
        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } = expanders;
        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = configurators;
    }
}
