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
    public async Task WorkflowRunGAgent_WhenBareLlmCallWorkflowRuns_ShouldCreateImplicitAssistantRoleActor()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime,
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)));
        SetAgentId(agent, "workflow-run-implicit-assistant");
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            """
            name: wf_implicit_assistant
            steps:
              - id: step_1
                type: llm_call
            """,
            "wf_implicit_assistant",
            runId: "run-implicit-assistant");

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        runtime.CreateCalls.Should().Be(1);
        runtime.Linked.Should().ContainSingle()
            .Which.child.Should().Be("workflow-run-implicit-assistant:assistant");

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.RoleName.Should().Be("Assistant");
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        roleAgent.LastInitializeEvent!.ProviderName.Should().BeEmpty();
        roleAgent.LastInitializeEvent.Model.Should().BeEmpty();
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
    public async Task WorkflowRunGAgent_WhenRoleIdMissing_ShouldMarkInvalidAndRejectExecution()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent(
            runtime: new RecordingActorRuntime(),
            roleResolver: new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent)));
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("", "RoleNoId"),
            "wf_missing_role",
            runId: "run-missing-role");

        agent.State.Compiled.Should().BeFalse();
        agent.State.CompilationError.Should().Contain("缺少 id 的角色");

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "x", SessionId = "s" });

        publisher.Published.Select(x => x.evt).OfType<ChatResponseEvent>()
            .Should().ContainSingle(response =>
                response.SessionId == "s" &&
                response.Content.Contains("not definition-bound or compiled", StringComparison.Ordinal));
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
        var runtime = new RecordingActorRuntime();
        var runPublisher = new RecordingEventPublisher();
        var definitionPublisher = new RecordingEventPublisher();
        var definitionAgent = await CreateRegisteredDefinitionAgentAsync(
            runtime,
            definitionPublisher,
            "workflow-definition:sub_flow",
            "sub_flow",
            BuildValidWorkflowYaml("sub_role", "SubRole", workflowName: "sub_flow"));
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-parent-singleton");
        agent.EventPublisher = runPublisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-1",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            Input = "payload-a",
            Lifecycle = "singleton",
        });

        agent.State.PendingSubWorkflowDefinitionResolutions.Should().ContainSingle(x => x.InvocationId == "invoke-1");
        runPublisher.Sent.Select(x => x.evt).OfType<SubWorkflowDefinitionResolveRequestedEvent>().Should().ContainSingle();
        await ResolveLatestDefinitionRequestAsync(agent, runPublisher, definitionAgent, definitionPublisher);

        runtime.CreateCalls.Should().Be(1);
        agent.State.SubWorkflowBindings.Should().ContainSingle(x =>
            x.DefinitionActorId == "workflow-definition:sub_flow" &&
            x.DefinitionVersion == definitionAgent.State.Version);
        agent.State.PendingSubWorkflowDefinitionResolutions.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocations.Should().ContainSingle(x =>
            x.InvocationId == "invoke-1" &&
            x.DefinitionActorId == "workflow-definition:sub_flow" &&
            x.DefinitionVersion == definitionAgent.State.Version);
        runPublisher.Sent.Select(x => x.evt).OfType<StartWorkflowEvent>().Should().ContainSingle();

        runPublisher.Sent.Clear();
        definitionPublisher.Sent.Clear();

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-2",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            Input = "payload-b",
            Lifecycle = "singleton",
        });

        agent.State.PendingSubWorkflowDefinitionResolutions.Should().ContainSingle(x => x.InvocationId == "invoke-2");
        runPublisher.Sent.Select(x => x.evt).OfType<SubWorkflowDefinitionResolveRequestedEvent>().Should().ContainSingle();
        await ResolveLatestDefinitionRequestAsync(agent, runPublisher, definitionAgent, definitionPublisher);

        runtime.CreateCalls.Should().Be(1);
        agent.State.SubWorkflowBindings.Should().ContainSingle();
        agent.State.PendingSubWorkflowInvocations.Should().HaveCount(2);
        agent.State.PendingSubWorkflowInvocations.Select(x => x.ChildActorId).Distinct().Should().ContainSingle();
        agent.State.PendingChildRunIdsByParentRunId.Should().ContainKey("parent-run");
        runPublisher.Sent.Select(x => x.evt).OfType<StartWorkflowEvent>().Should().ContainSingle();

        var childAgent = runtime.CreatedChildWorkflowAgents.Single();
        childAgent.BindEvents.Should().ContainSingle();
        childAgent.StartEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenChildWorkflowCompletes_ShouldTranslateToParentStepCompleted()
    {
        var runtime = new RecordingActorRuntime();
        var runPublisher = new RecordingEventPublisher();
        var definitionPublisher = new RecordingEventPublisher();
        var definitionAgent = await CreateRegisteredDefinitionAgentAsync(
            runtime,
            definitionPublisher,
            "workflow-definition:sub_flow",
            "sub_flow",
            BuildValidWorkflowYaml("sub_role", "SubRole", workflowName: "sub_flow"));
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-parent-completion");
        agent.EventPublisher = runPublisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-child",
            ParentRunId = "parent-run",
            ParentStepId = "step-child",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });
        await ResolveLatestDefinitionRequestAsync(agent, runPublisher, definitionAgent, definitionPublisher);

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

        var parentCompletion = runPublisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        parentCompletion.StepId.Should().Be("step-child");
        parentCompletion.RunId.Should().Be("parent-run");
        parentCompletion.Success.Should().BeTrue();
        parentCompletion.Output.Should().Be("child-done");
        parentCompletion.Annotations["workflow_call.child_run_id"].Should().Be(pending.ChildRunId);
        runPublisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenChildWorkflowStops_ShouldTranslateToParentStepFailure()
    {
        var runtime = new RecordingActorRuntime();
        var runPublisher = new RecordingEventPublisher();
        var definitionPublisher = new RecordingEventPublisher();
        var definitionAgent = await CreateRegisteredDefinitionAgentAsync(
            runtime,
            definitionPublisher,
            "workflow-definition:sub_flow",
            "sub_flow",
            BuildValidWorkflowYaml("sub_role", "SubRole", workflowName: "sub_flow"));
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-parent-stopped");
        agent.EventPublisher = runPublisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-child-stop",
            ParentRunId = "parent-run",
            ParentStepId = "step-child",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });
        await ResolveLatestDefinitionRequestAsync(agent, runPublisher, definitionAgent, definitionPublisher);

        var pending = agent.State.PendingSubWorkflowInvocations.Single();
        await agent.HandleWorkflowStoppedEnvelope(Envelope(
            new WorkflowStoppedEvent
            {
                WorkflowName = "sub_flow",
                RunId = pending.ChildRunId,
                Reason = "manual",
            },
            pending.ChildActorId,
            TopologyAudience.ParentAndChildren));

        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
        agent.State.PendingChildRunIdsByParentRunId.Should().BeEmpty();

        var parentCompletion = runPublisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        parentCompletion.StepId.Should().Be("step-child");
        parentCompletion.RunId.Should().Be("parent-run");
        parentCompletion.Success.Should().BeFalse();
        parentCompletion.Output.Should().BeEmpty();
        parentCompletion.Error.Should().Be("workflow_call child workflow stopped: manual");
        parentCompletion.Annotations["workflow_call.child_run_id"].Should().Be(pending.ChildRunId);
        runPublisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenChildWorkflowRunStops_ShouldTranslateToParentStepFailure()
    {
        var runtime = new RecordingActorRuntime();
        var runPublisher = new RecordingEventPublisher();
        var definitionPublisher = new RecordingEventPublisher();
        var definitionAgent = await CreateRegisteredDefinitionAgentAsync(
            runtime,
            definitionPublisher,
            "workflow-definition:sub_flow",
            "sub_flow",
            BuildValidWorkflowYaml("sub_role", "SubRole", workflowName: "sub_flow"));
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-parent-run-stopped");
        agent.EventPublisher = runPublisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-child-run-stop",
            ParentRunId = "parent-run",
            ParentStepId = "step-child",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });
        await ResolveLatestDefinitionRequestAsync(agent, runPublisher, definitionAgent, definitionPublisher);

        var pending = agent.State.PendingSubWorkflowInvocations.Single();
        await agent.HandleWorkflowRunStoppedEnvelope(Envelope(
            new WorkflowRunStoppedEvent
            {
                RunId = pending.ChildRunId,
                Reason = "operator stop",
            },
            pending.ChildActorId,
            TopologyAudience.ParentAndChildren));

        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
        agent.State.PendingChildRunIdsByParentRunId.Should().BeEmpty();

        var parentCompletion = runPublisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        parentCompletion.StepId.Should().Be("step-child");
        parentCompletion.RunId.Should().Be("parent-run");
        parentCompletion.Success.Should().BeFalse();
        parentCompletion.Output.Should().BeEmpty();
        parentCompletion.Error.Should().Be("workflow_call child workflow stopped: operator stop");
        parentCompletion.Annotations["workflow_call.child_run_id"].Should().Be(pending.ChildRunId);
        runPublisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenParentRunCompletes_ShouldCleanupPendingInvocationsAndDestroyNonSingletonChildren()
    {
        var runtime = new RecordingActorRuntime();
        var runPublisher = new RecordingEventPublisher();
        var definitionPublisher = new RecordingEventPublisher();
        var definitionAgent = await CreateRegisteredDefinitionAgentAsync(
            runtime,
            definitionPublisher,
            "workflow-definition:sub_flow",
            "sub_flow",
            BuildValidWorkflowYaml("sub_role", "SubRole", workflowName: "sub_flow"));
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-parent-cleanup");
        agent.EventPublisher = runPublisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-singleton",
            ParentRunId = "parent-run",
            ParentStepId = "step-singleton",
            WorkflowName = "sub_flow",
            Input = "payload-singleton",
            Lifecycle = "singleton",
        });
        await ResolveLatestDefinitionRequestAsync(agent, runPublisher, definitionAgent, definitionPublisher);

        runPublisher.Sent.Clear();
        definitionPublisher.Sent.Clear();

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-transient",
            ParentRunId = "parent-run",
            ParentStepId = "step-transient",
            WorkflowName = "sub_flow",
            Input = "payload-transient",
            Lifecycle = "transient",
        });
        await ResolveLatestDefinitionRequestAsync(agent, runPublisher, definitionAgent, definitionPublisher);

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

    [Fact]
    public async Task WorkflowRunGAgent_ShouldPersistObservedWorkflowCommandId_FromChatMetadata()
    {
        var eventStore = new InMemoryEventStore();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, "workflow-run-command");

        agent.RunId.Should().Be("workflow-run-command");

        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-command");

        (await agent.GetDescriptionAsync()).Should().Contain("bound");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
            Headers =
            {
                ["workflow.command_id"] = "cmd-123",
            },
        });

        agent.RunId.Should().Be("run-command");
        agent.State.LastCommandId.Should().Be("cmd-123");

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Should().Contain(x => x.EventData.Is(WorkflowCommandObservedEvent.Descriptor));
        persisted.Where(x => x.EventData.Is(WorkflowCommandObservedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<WorkflowCommandObservedEvent>().CommandId)
            .Should()
            .ContainSingle("cmd-123");
    }

    [Fact]
    public async Task WorkflowRunGAgent_BindWorkflowRunDefinition_ShouldTrimInlineWorkflowNames_AndIgnoreInvalidEntries()
    {
        var agent = CreateRunAgent();
        SetAgentId(agent, "workflow-run-inline");

        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [" sub_flow "] = BuildValidWorkflowYaml("sub_role", "SubRole"),
                ["   "] = BuildValidWorkflowYaml("ignored_role", "IgnoredRole"),
                ["blank_yaml"] = string.Empty,
            });

        agent.State.RunId.Should().Be("workflow-run-inline");
        agent.State.InlineWorkflowYamls.Should().ContainKey("sub_flow");
        agent.State.InlineWorkflowYamls.Should().NotContainKey("   ");
        agent.State.InlineWorkflowYamls.Should().NotContainKey("blank_yaml");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenReplacingDefinitionWithEmptyYaml_ShouldPublishFailureResponse()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent();
        agent.EventPublisher = publisher;

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = "   ",
            Input = "hello",
        });

        publisher.Published.Select(x => x.evt).OfType<ChatResponseEvent>()
            .Should()
            .ContainSingle(x => x.Content == "Dynamic workflow YAML is empty.");
    }

    [Fact]
    public async Task WorkflowRunGAgent_HandleWorkflowStopped_ShouldIgnoreMismatchedAndTerminalRuns()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent();
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-stop-ignore");
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            RunId = "other-run",
            Reason = "ignore-me",
        });

        agent.State.Status.Should().Be("running");
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Should().BeEmpty();

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_valid",
            RunId = "run-stop-ignore",
            Success = true,
            Output = "done",
        });

        var publishedCount = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Count();

        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            RunId = "run-stop-ignore",
            Reason = "already-done",
        });

        agent.State.Status.Should().Be("completed");
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Count().Should().Be(publishedCount);
    }

    [Fact]
    public async Task WorkflowRunGAgent_HandleWorkflowStopped_ShouldPersistStopAndCleanupRuntimeState()
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
            runId: "run-stop");
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "hello", SessionId = "s1" });
        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));

        var roleActorId = runtime.CreatedActors.Single().Id;

        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            Reason = "manual-stop",
        });

        agent.State.Status.Should().Be("stopped");
        agent.State.FinalError.Should().Be("manual-stop");
        agent.State.ExecutionStates.Should().BeEmpty();
        runtime.Unlinked.Should().Contain(roleActorId);
        runtime.Destroyed.Should().Contain(roleActorId);
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x => x.Content == "Workflow execution stopped: manual-stop");

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Should().Contain(x => x.EventData.Is(WorkflowStoppedEvent.Descriptor));
        persisted.Where(x => x.EventData.Is(WorkflowStoppedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<WorkflowStoppedEvent>())
            .Should()
            .ContainSingle(x => x.RunId == "run-stop" && x.WorkflowName == "wf_valid" && x.Reason == "manual-stop");
    }

    [Fact]
    public async Task WorkflowRunGAgent_HandleWorkflowRunStoppedAsync_ShouldPersistStopAndCleanupOnlyOnce()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime, eventStore: eventStore);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-stop-async");
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "hello", SessionId = "s1" });
        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));

        var roleActorId = runtime.CreatedActors.Single().Id;

        await agent.HandleWorkflowRunStoppedAsync(new WorkflowRunStoppedEvent
        {
            RunId = "run-stop-async",
            Reason = "requested",
        });
        await agent.HandleWorkflowRunStoppedAsync(new WorkflowRunStoppedEvent
        {
            RunId = "run-stop-async",
            Reason = "ignored",
        });

        agent.State.Status.Should().Be("stopped");
        agent.State.FinalError.Should().Be("requested");
        agent.State.ExecutionStates.Should().BeEmpty();
        runtime.Unlinked.Should().Contain(roleActorId);
        runtime.Destroyed.Should().Contain(roleActorId);
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x => x.Content == "Workflow execution stopped: requested");

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Count(x => x.EventData.Is(WorkflowRunStoppedEvent.Descriptor)).Should().Be(1);
    }

    [Fact]
    public async Task WorkflowRunGAgent_HandleWorkflowArtifactObservationEnvelope_ShouldPersistSupportedArtifactFacts()
    {
        var eventStore = new InMemoryEventStore();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, "workflow-run-artifacts");

        await agent.HandleWorkflowArtifactObservationEnvelope(Envelope(
            new StepRequestEvent
            {
                StepId = "step-1",
                StepType = "transform",
            },
            agent.Id,
            TopologyAudience.Self));
        await agent.HandleWorkflowArtifactObservationEnvelope(Envelope(
            new StepCompletedEvent
            {
                StepId = "step-1",
                Success = true,
            },
            agent.Id,
            TopologyAudience.Self));
        await agent.HandleWorkflowArtifactObservationEnvelope(Envelope(
            new WorkflowSuspendedEvent
            {
                StepId = "step-1",
                SuspensionType = "human_input",
            },
            agent.Id,
            TopologyAudience.Self));
        await agent.HandleWorkflowArtifactObservationEnvelope(Envelope(
            new WaitingForSignalEvent
            {
                StepId = "step-1",
                SignalName = "continue",
            },
            agent.Id,
            TopologyAudience.Self));
        await agent.HandleWorkflowArtifactObservationEnvelope(Envelope(
            new WorkflowSignalBufferedEvent
            {
                StepId = "step-1",
                SignalName = "continue",
            },
            agent.Id,
            TopologyAudience.Self));
        await agent.HandleWorkflowArtifactObservationEnvelope(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(agent.Id, TopologyAudience.Self),
        });
        await agent.HandleWorkflowArtifactObservationEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                RunId = "run-ignored",
                Success = true,
            },
            agent.Id,
            TopologyAudience.Self));

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Should().Contain(x => x.EventData.Is(StepRequestEvent.Descriptor));
        persisted.Should().Contain(x => x.EventData.Is(StepCompletedEvent.Descriptor));
        persisted.Should().Contain(x => x.EventData.Is(WorkflowSuspendedEvent.Descriptor));
        persisted.Should().Contain(x => x.EventData.Is(WaitingForSignalEvent.Descriptor));
        persisted.Should().Contain(x => x.EventData.Is(WorkflowSignalBufferedEvent.Descriptor));
        persisted.Should().NotContain(x => x.EventData.Is(WorkflowCompletedEvent.Descriptor));
    }

    [Fact]
    public async Task WorkflowRunGAgent_HandleWorkflowArtifactObservationEnvelope_ShouldTranslateChildRoleReplyFacts()
    {
        var eventStore = new InMemoryEventStore();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, "workflow-run-role-reply");

        await agent.HandleWorkflowArtifactObservationEnvelope(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("external-role"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-ignore",
                    EventData = Any.Pack(new RoleChatSessionCompletedEvent
                    {
                        SessionId = "ignored",
                    }),
                },
                StateRoot = Any.Pack(new RoleGAgentState()),
            }),
        });

        await agent.HandleWorkflowArtifactObservationEnvelope(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-run-role-reply:role_a"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-role-reply",
                    EventData = Any.Pack(new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-1",
                        Content = "reply",
                        ReasoningContent = "reasoning",
                        Prompt = "prompt",
                        ContentEmitted = true,
                        ToolCalls =
                        {
                            new ToolCallEvent
                            {
                                ToolName = "search",
                                CallId = "call-1",
                            },
                        },
                    }),
                },
                StateRoot = Any.Pack(new RoleGAgentState()),
            }),
        });

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Count(x => x.EventData.Is(WorkflowRoleReplyRecordedEvent.Descriptor)).Should().Be(1);

        var fact = persisted.Single(x => x.EventData.Is(WorkflowRoleReplyRecordedEvent.Descriptor))
            .EventData
            .Unpack<WorkflowRoleReplyRecordedEvent>();
        fact.RunId.Should().Be("workflow-run-role-reply");
        fact.RoleActorId.Should().Be("workflow-run-role-reply:role_a");
        fact.RoleId.Should().Be("role_a");
        fact.SessionId.Should().Be("session-1");
        fact.Content.Should().Be("reply");
        fact.ReasoningContent.Should().Be("reasoning");
        fact.Prompt.Should().Be("prompt");
        fact.ContentEmitted.Should().BeTrue();
        fact.ToolCalls.Should().ContainSingle(x => x.ToolName == "search" && x.CallId == "call-1");
    }

    [Fact]
    public void WorkflowRunGAgent_Constructor_ShouldValidateRequiredDependencies()
    {
        var runtime = new RecordingActorRuntime();
        var roleResolver = new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        var eventModuleFactory = new RecordingEventModuleFactory();
        var packs = Array.Empty<IWorkflowModulePack>();

        Action missingRuntime = () => new WorkflowRunGAgent(null!, runtime, roleResolver, eventModuleFactory, packs);
        Action missingDispatchPort = () => new WorkflowRunGAgent(runtime, null!, roleResolver, eventModuleFactory, packs);
        Action missingRoleResolver = () => new WorkflowRunGAgent(runtime, runtime, null!, eventModuleFactory, packs);
        Action missingEventModuleFactory = () => new WorkflowRunGAgent(runtime, runtime, roleResolver, null!, packs);
        Action missingPacks = () => new WorkflowRunGAgent(runtime, runtime, roleResolver, eventModuleFactory, null!);

        missingRuntime.Should().Throw<ArgumentNullException>().WithParameterName("runtime");
        missingDispatchPort.Should().Throw<ArgumentNullException>().WithParameterName("dispatchPort");
        missingRoleResolver.Should().Throw<ArgumentNullException>().WithParameterName("roleAgentTypeResolver");
        missingEventModuleFactory.Should().Throw<ArgumentNullException>().WithParameterName("stepExecutorFactory");
        missingPacks.Should().Throw<ArgumentNullException>().WithParameterName("modulePacks");
    }

    [Fact]
    public async Task WorkflowRunGAgent_ShouldRoundTripExecutionStates_AndReflectDescriptions()
    {
        var agent = CreateRunAgent();
        SetAgentId(agent, "workflow-run-execution-state");

        (await agent.GetDescriptionAsync()).Should().Contain("invalid");

        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-execution-state");

        (await agent.GetDescriptionAsync()).Should().Contain("(bound)");

        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));

        agent.GetExecutionState("scope-a")!
            .Unpack<StringValue>()
            .Value
            .Should()
            .Be("state-a");
        agent.GetExecutionStates().Should().ContainSingle(x => x.Key == "scope-a");

        await agent.ClearExecutionStateAsync("scope-a");

        agent.GetExecutionState("scope-a").Should().BeNull();
        agent.GetExecutionStates().Should().BeEmpty();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        (await agent.GetDescriptionAsync()).Should().Contain("(running)");
    }

    [Fact]
    public async Task WorkflowRunGAgent_HandleWorkflowStopped_WhenReasonMissing_ShouldPublishDefaultMessage()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent();
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-stop-default");
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent());

        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x => x.Content == "Workflow execution stopped.");
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

    private static async Task<WorkflowGAgent> CreateRegisteredDefinitionAgentAsync(
        RecordingActorRuntime runtime,
        RecordingEventPublisher publisher,
        string actorId,
        string workflowName,
        string workflowYaml)
    {
        var agent = CreateDefinitionAgent();
        SetAgentId(agent, actorId);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync(workflowYaml, workflowName);
        runtime.RegisterAgent(actorId, agent);
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

    private static async Task ResolveLatestDefinitionRequestAsync(
        WorkflowRunGAgent runAgent,
        RecordingEventPublisher runPublisher,
        WorkflowGAgent definitionAgent,
        RecordingEventPublisher definitionPublisher)
    {
        var request = runPublisher.Sent.Select(x => x.evt).OfType<SubWorkflowDefinitionResolveRequestedEvent>().Last();
        await definitionAgent.HandleSubWorkflowDefinitionResolveRequested(request);

        var reply = definitionPublisher.Sent.Select(x => x.evt).Last();
        switch (reply)
        {
            case SubWorkflowDefinitionResolvedEvent resolved:
                await runAgent.HandleSubWorkflowDefinitionResolved(resolved);
                break;
            case SubWorkflowDefinitionResolveFailedEvent failed:
                await runAgent.HandleSubWorkflowDefinitionResolveFailed(failed);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unexpected workflow definition reply '{reply.Descriptor.FullName}'.");
        }
    }

    private static void SetAgentId(GAgentBase agent, string agentId)
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
        string? model = null,
        string? workflowName = null)
    {
        var name = workflowName ?? "wf_valid";
        var providerLine = string.IsNullOrWhiteSpace(provider) ? string.Empty : $"\n    provider: \"{provider}\"";
        var modelLine = string.IsNullOrWhiteSpace(model) ? string.Empty : $"\n    model: \"{model}\"";
        return $$"""
                 name: {{name}}
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

        public void RegisterAgent(string actorId, IAgent agent)
        {
            CreatedActors.RemoveAll(x => string.Equals(x.Id, actorId, StringComparison.Ordinal));
            CreatedActors.Add(new FakeActor(actorId, agent));
        }

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
