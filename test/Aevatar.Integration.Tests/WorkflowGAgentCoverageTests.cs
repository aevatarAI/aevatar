using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
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
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Integration.AI;
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

        await agent.HandleChatRequest(new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        runtime.CreateCalls.Should().Be(0);
        var response = publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>().Single();
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
            eventStore: eventStore);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-1");

        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "first", SessionId = "s1" });
        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "second", SessionId = "s2" });

        runtime.CreateCalls.Should().Be(0);
        runtime.CreateByKindCalls.Should().ContainSingle().Which.Should().Be((
            "workflow.assistant-role",
            $"{agent.Id}:role_a"));
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
    public async Task WorkflowRunGAgent_WhenChatInputHasFileRef_ShouldPublishStartWorkflowFileRefs()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-1");
        var inputFileRef = new WorkflowFileRef
        {
            FileId = "wf-file-chat",
            ArtifactId = "workflow-file://wf-file-chat",
            SourceKind = WorkflowFileSourceKind.ChatInput,
            SourceMessageId = "message-1",
            SourceResourceKey = "attachment-1",
            FileName = "input.txt",
            MediaType = "text/plain",
            SizeBytes = 12,
            Sha256 = "abc123",
            CreatedAtUnixMs = 1,
            ExpiresAtUnixMs = 2,
            OwnerRunId = "incoming-run",
            OwnerScopeId = "scope-1",
        };

        await agent.HandleChatRequest(new WorkflowChatRequestEvent
        {
            Prompt = "extract",
            SessionId = "s1",
            InputParts =
            {
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.Text,
                    Text = "extract",
                },
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.Image,
                    FileRef = inputFileRef,
                },
            },
        });

        var start = publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().Single();
        start.WorkflowName.Should().Be("wf_valid");
        start.RunId.Should().Be("run-1");
        var startFileRef = start.InputFileRefs.Should().ContainSingle().Subject;
        startFileRef.Should().NotBeSameAs(inputFileRef);
        startFileRef.FileId.Should().Be("wf-file-chat");
        startFileRef.ArtifactId.Should().Be("workflow-file://wf-file-chat");
        startFileRef.SourceKind.Should().Be(WorkflowFileSourceKind.ChatInput);
        startFileRef.SourceMessageId.Should().Be("message-1");
        startFileRef.SourceResourceKey.Should().Be("attachment-1");
        startFileRef.FileName.Should().Be("input.txt");
        startFileRef.MediaType.Should().Be("text/plain");
        startFileRef.SizeBytes.Should().Be(12);
        startFileRef.Sha256.Should().Be("abc123");
        startFileRef.CreatedAtUnixMs.Should().Be(1);
        startFileRef.ExpiresAtUnixMs.Should().Be(2);
        startFileRef.OwnerRunId.Should().Be("incoming-run");
        startFileRef.OwnerScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task WorkflowRunGAgent_ShouldPassFullRoleConfigurationToInitializeEvent()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime);
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildWorkflowYamlWithFullRoleConfig(),
            "wf_role_fields",
            runId: "run-role");

        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        var initializeEvent = runtime.CreatedActors.Single().Agent
            .Should().BeOfType<FakeRoleAgent>().Subject.LastInitializeEvent!;
        initializeEvent.RoleName.Should().Be("RoleA");
        initializeEvent.ProviderName.Should().Be("openai");
        initializeEvent.Model.Should().Be("gpt-5.4");
        initializeEvent.SystemPrompt.Should().Be("helpful role");
        initializeEvent.HasTemperature.Should().BeTrue();
        initializeEvent.Temperature.Should().BeApproximately(0.2f, 0.0001f);
        initializeEvent.MaxTokens.Should().Be(256);
        initializeEvent.MaxToolRounds.Should().Be(4);
        initializeEvent.MaxHistoryMessages.Should().Be(30);
        initializeEvent.EventModules.Should().Be("llm_handler,tool_handler");
        initializeEvent.EventRoutes.Should().Contain("event.type");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenBareLlmCallWorkflowRuns_ShouldCreateImplicitAssistantRoleActor()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime);
        SetAgentId(agent, "workflow-run-implicit-assistant");
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            """
            name: wf_implicit_assistant
            roles:
              - id: assistant
                name: Assistant
                agent_kind: workflow.assistant-role
            steps:
              - id: step_1
                type: llm_call
                target_role: assistant
            """,
            "wf_implicit_assistant",
            runId: "run-implicit-assistant");

        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        runtime.CreateCalls.Should().Be(0);
        runtime.CreateByKindCalls.Should().ContainSingle().Which.Should().Be((
            "workflow.assistant-role",
            "workflow-run-implicit-assistant:assistant"));
        runtime.Linked.Should().ContainSingle()
            .Which.child.Should().Be("workflow-run-implicit-assistant:assistant");

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.RoleName.Should().Be("Assistant");
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        roleAgent.LastInitializeEvent!.ProviderName.Should().BeEmpty();
        roleAgent.LastInitializeEvent.Model.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenRoleAgentKindConfigured_ShouldCreateRoleActorByKindAndInitializeIt()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime);
        SetAgentId(agent, "workflow-run-kind");
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            """
            name: wf_kind
            roles:
              - id: assistant
                name: Assistant
                agent_kind: " workflow.assistant-role "
            steps:
              - id: step_1
                type: llm_call
                target_role: assistant
            """,
            "wf_kind",
            runId: "run-kind");

        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        runtime.CreateCalls.Should().Be(0);
        runtime.CreateByKindCalls.Should().ContainSingle().Which.Should().Be((
            "workflow.assistant-role",
            "workflow-run-kind:assistant"));
        runtime.Linked.Should().ContainSingle()
            .Which.Should().Be(("workflow-run-kind", "workflow-run-kind:assistant"));

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        roleAgent.LastInitializeEvent!.RoleId.Should().Be("assistant");
        roleAgent.LastInitializeEvent.RoleName.Should().Be("Assistant");

        var persisted = await ((InMemoryEventStore)agent.Services.GetRequiredService<IEventStore>()).GetEventsAsync(agent.Id);
        persisted.Should().Contain(x => x.EventType.Contains(nameof(WorkflowRoleActorLinkedEvent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenRoleAgentKindMissing_ShouldRejectExecution()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-default-role");
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA", workflowName: "wf_default_role", includeAgentKind: false),
            "wf_default_role",
            runId: "run-default-role");

        var act = () => agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must declare agent_kind*");
        runtime.CreateByKindCalls.Should().BeEmpty();
        runtime.CreateCalls.Should().Be(0);
        runtime.CreatedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenRoleAgentKindCannotCreate_ShouldFailBeforeLinkingRole()
    {
        var runtime = new RecordingActorRuntime
        {
            CreateByKindException = new InvalidOperationException("unknown agent kind"),
        };
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-invalid-kind");
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            """
            name: wf_invalid_kind
            roles:
              - id: bridge
                name: Bridge
                agent_kind: workflow.missing-kind
            steps:
              - id: step_1
                type: llm_call
                target_role: bridge
            """,
            "wf_invalid_kind",
            runId: "run-invalid-kind");

        var act = () => agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown agent kind*");
        runtime.CreateByKindCalls.Should().ContainSingle().Which.Should().Be((
            "workflow.missing-kind",
            "workflow-run-invalid-kind:bridge"));
        runtime.Linked.Should().BeEmpty();
        runtime.CreatedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenRebindingDefinition_ShouldResetExecutionStateAndDestroyOldChildren()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime);
        SetAgentId(agent, "workflow-run-rebind");
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-1");
        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "first", SessionId = "s1" });
        var oldChildActorId = runtime.CreatedActors.Single().Id;
        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));
        await SeedRuntimeContextAsync(agent);
        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_valid",
            RunId = "run-1",
            Success = true,
            Output = "done-a",
        });
        await SeedRuntimeContextAsync(agent);
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
        AssertRuntimeContextCleared(agent);
        runtime.Unlinked.Should().Contain(oldChildActorId);
        runtime.Destroyed.Should().Contain(oldChildActorId);

        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "second", SessionId = "s2" });

        runtime.Linked.Should().Contain(x => x.child.EndsWith(":role_b", StringComparison.Ordinal));
        runtime.CreatedActors.Select(x => x.Id).Should().Contain($"{agent.Id}:role_b");
    }

    [Fact]
    public async Task WorkflowRunGAgent_WhenRoleIdMissing_ShouldMarkInvalidAndRejectExecution()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent(
            runtime: new RecordingActorRuntime());
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("", "RoleNoId"),
            "wf_missing_role",
            runId: "run-missing-role");

        agent.State.Compiled.Should().BeFalse();
        agent.State.CompilationError.Should().Contain("role 缺 id");

        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "x", SessionId = "s" });

        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
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
        await SeedRuntimeContextAsync(agent1);
        await agent1.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_replay",
            RunId = "run-replay",
            Success = true,
            Output = "done",
        });
        AssertRuntimeContextCleared(agent1);
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

        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should().ContainSingle(x => x.Content == "done");
    }

    [Fact]
    public async Task WorkflowRunGAgent_ReplayContract_ShouldRestoreExecutionContextAfterChatStart()
    {
        var eventStore = new InMemoryEventStore();
        var runtime = new RecordingActorRuntime();
        var agent1 = CreateRunAgent(runtime: runtime, eventStore: eventStore);
        SetAgentId(agent1, "workflow-run-context-replay");

        await agent1.ActivateAsync();
        await agent1.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_context_replay",
            runId: "run-context-replay");

        await agent1.HandleChatRequest(new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "s1",
            Metadata =
            {
                ["trace-id"] = " trace-abc ",
            },
            ConnectorHttpAuthorization = " Bearer secret ",
            LlmControl = new WorkflowLlmControlContext
            {
                ModelOverride = " model-main ",
                MaxToolRoundsOverride = 4,
                UserMemoryPrompt = " memory-main ",
            },
        });

        agent1.State.ExecutionContext.Connector!.HttpAuthorization.Should().Be("Bearer secret");
        agent1.State.ExecutionContext.Llm!.ModelOverride.Should().Be("model-main");
        await agent1.DeactivateAsync();

        var persisted = await eventStore.GetEventsAsync(agent1.Id);
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(WorkflowRunExecutionStartedEvent), StringComparison.Ordinal));

        var agent2 = CreateRunAgent(runtime: runtime, eventStore: eventStore);
        SetAgentId(agent2, "workflow-run-context-replay");
        await agent2.ActivateAsync();

        agent2.State.ExecutionContext.Connector!.HttpAuthorization.Should().Be("Bearer secret");
        agent2.State.ExecutionContext.Llm!.ModelOverride.Should().Be("model-main");
        agent2.State.ExecutionContext.Llm.MaxToolRoundsOverride.Should().Be(4);
        agent2.State.ExecutionContext.Llm.UserMemoryPrompt.Should().Be("memory-main");
    }

    [Fact]
    public async Task WorkflowRunGAgent_CommittedObservation_ShouldRedactExecutionContextAndCapturedSecrets()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, "workflow-run-redaction");
        agent.EventPublisher = publisher;
        agent.CommittedStateEventPublisher = publisher;

        await agent.ActivateAsync();
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_redaction",
            runId: "run-redaction");

        await ConnectorAuthorizationRuntimeContextAccess.SetAuthorizationAsync(
            agent,
            "Bearer secret");
        await WorkflowRequestMetadataRuntimeContextAccess.SetLlmControlAsync(
            agent,
            new WorkflowLlmControlContext
            {
                ModelOverride = "model",
                MaxToolRoundsOverride = 2,
                UserMemoryPrompt = "memory",
            });

        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));
        await agent.UpsertExecutionStateAsync(
            SecureInputStateAccess.ModuleStateKey,
            Any.Pack(new SecureInputModuleState
            {
                Captured =
                {
                    ["run-redaction::api_key"] = new CapturedSecureInputState
                    {
                        RunId = "run-redaction",
                        VariableName = "api_key",
                        Value = "sk-secret",
                    },
                },
            }));

        agent.State.ExecutionContext.Llm!.ModelOverride.Should().Be("model");
        agent.State.ExecutionContext.Connector!.HttpAuthorization.Should().Be("Bearer secret");
        agent.State.ExecutionStates[SecureInputStateAccess.ModuleStateKey]
            .Unpack<SecureInputModuleState>()
            .Captured["run-redaction::api_key"]
            .Value.Should().Be("sk-secret");

        var observedState = publisher.Published
            .Select(x => x.evt)
            .OfType<CommittedStateEventPublished>()
            .Last()
            .StateRoot
            .Unpack<WorkflowRunState>();

        observedState.ExecutionContext.Llm!.ModelOverride.Should().Be("model");
        observedState.ExecutionContext.Llm.MaxToolRoundsOverride.Should().Be(2);
        observedState.ExecutionContext.Llm.UserMemoryPrompt.Should().Be("memory");
        observedState.ExecutionContext.Connector!.HttpAuthorization.Should().BeEmpty();
        observedState.ExecutionStates[SecureInputStateAccess.ModuleStateKey]
            .Unpack<SecureInputModuleState>()
            .Captured["run-redaction::api_key"]
            .Value.Should().BeEmpty();

        var observedEvent = publisher.Published
            .Select(x => x.evt)
            .OfType<CommittedStateEventPublished>()
            .Last()
            .StateEvent
            .EventData
            .Unpack<WorkflowExecutionStateUpsertedEvent>();
        observedEvent.State.Unpack<SecureInputModuleState>()
            .Captured["run-redaction::api_key"]
            .Value.Should().BeEmpty();

        var observedContextEvent = publisher.Published
            .Select(x => x.evt)
            .OfType<CommittedStateEventPublished>()
            .Select(x => x.StateEvent.EventData)
            .Where(x => x.Is(WorkflowRunExecutionContextUpdatedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowRunExecutionContextUpdatedEvent>())
            .ToList();
        observedContextEvent.Should().HaveCount(2);
        observedContextEvent[0].ExecutionContextDelta.Connector!.HttpAuthorization.Should().BeEmpty();
        observedContextEvent[1].ExecutionContextDelta.Llm!.ModelOverride.Should().Be("model");
        observedContextEvent[1].ExecutionContextDelta.Llm.MaxToolRoundsOverride.Should().Be(2);
        observedContextEvent[1].ExecutionContextDelta.Llm.UserMemoryPrompt.Should().Be("memory");
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
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
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
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
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
            runtime: runtime);
        SetAgentId(agent, "workflow-run-replace");
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-replace");
        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "first", SessionId = "s1" });
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
    public async Task WorkflowRunGAgent_BindDefinition_ShouldCleanupPendingSubWorkflowChild()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-bind-reset");
        var childActorId = $"{agent.Id}:workflow:sub_flow:parent-run:invoke-reset";
        runtime.RegisterAgent(childActorId, new FakeWorkflowRunChildAgent(childActorId));
        agent.State.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-reset",
            ParentRunId = "parent-run",
            ParentStepId = "step-reset",
            WorkflowName = "sub_flow",
            ChildActorId = childActorId,
            ChildRunId = "invoke-reset",
            Lifecycle = WorkflowCallLifecycle.Transient,
            HandoffPhase = SubWorkflowInvocationHandoffPhase.Bound,
            DefinitionYaml = BuildValidWorkflowYaml("sub_role", "SubRole", workflowName: "sub_flow"),
        });
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId["invoke-reset"] = 0;

        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-reset");

        runtime.Unlinked.Should().Contain(childActorId);
        runtime.Destroyed.Should().Contain(childActorId);
        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunGAgent_ReplaceDefinition_ShouldCleanupPendingSubWorkflowChild()
    {
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(runtime: runtime);
        SetAgentId(agent, "workflow-run-replace-reset");
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-reset");
        var childActorId = $"{agent.Id}:workflow:sub_flow:parent-run:invoke-replace-reset";
        runtime.RegisterAgent(childActorId, new FakeWorkflowRunChildAgent(childActorId));
        agent.State.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-replace-reset",
            ParentRunId = "parent-run",
            ParentStepId = "step-reset",
            WorkflowName = "sub_flow",
            ChildActorId = childActorId,
            ChildRunId = "invoke-replace-reset",
            Lifecycle = WorkflowCallLifecycle.Transient,
            HandoffPhase = SubWorkflowInvocationHandoffPhase.Bound,
            DefinitionYaml = BuildValidWorkflowYaml("sub_role", "SubRole", workflowName: "sub_flow"),
        });
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId["invoke-replace-reset"] = 0;

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = BuildValidWorkflowYaml("role_b", "RoleB"),
            Input = "replace",
        });

        runtime.Unlinked.Should().Contain(childActorId);
        runtime.Destroyed.Should().Contain(childActorId);
        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
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
        childAgent.BindEvents.Select(x => x.RunId).Should().Equal("invoke-1", "invoke-2");
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
        runPublisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>().Should().BeEmpty();
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
        runPublisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>().Should().BeEmpty();
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
        runPublisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>().Should().BeEmpty();
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
            runtime: runtime);

        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-complete");
        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "first", SessionId = "s1" });

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

        await agent.HandleEventAsync(Envelope(
            new WorkflowChatRequestEvent
            {
                Prompt = "hello",
                SessionId = "session-1",
            },
            "client",
            TopologyAudience.Self,
            id: "cmd-123"));

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

        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
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
        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            RunId = "other-run",
            Reason = "ignore-me",
        });

        agent.State.Status.Should().Be("running");
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>().Should().BeEmpty();

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_valid",
            RunId = "run-stop-ignore",
            Success = true,
            Output = "done",
        });

        var publishedCount = publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>().Count();

        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            RunId = "run-stop-ignore",
            Reason = "already-done",
        });

        agent.State.Status.Should().Be("completed");
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>().Count().Should().Be(publishedCount);
    }

    [Fact]
    public async Task WorkflowRunGAgent_HandleWorkflowStopped_ShouldPersistStopAndCleanupRuntimeState()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateRunAgent(
            runtime: runtime,
            eventStore: eventStore);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowRunDefinitionAsync(
            "definition-1",
            BuildValidWorkflowYaml("role_a", "RoleA"),
            "wf_valid",
            runId: "run-stop");
        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });
        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));
        await SeedRuntimeContextAsync(agent);

        var roleActorId = runtime.CreatedActors.Single().Id;

        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            Reason = "manual-stop",
        });

        agent.State.Status.Should().Be("stopped");
        agent.State.FinalError.Should().Be("manual-stop");
        agent.State.ExecutionStates.Should().BeEmpty();
        AssertRuntimeContextCleared(agent);
        runtime.Unlinked.Should().Contain(roleActorId);
        runtime.Destroyed.Should().Contain(roleActorId);
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
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
        await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });
        await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));
        await SeedRuntimeContextAsync(agent);

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
        AssertRuntimeContextCleared(agent);
        runtime.Unlinked.Should().Contain(roleActorId);
        runtime.Destroyed.Should().Contain(roleActorId);
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
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
                    EventData = Any.Pack(new StringValue
                    {
                        Value = "ignored",
                    }),
                },
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
                    EventData = Any.Pack(new WorkflowLlmInvocationCompletedEvent
                    {
                        SessionId = "session-1",
                        Content = "reply",
                        ReasoningContent = "reasoning",
                        Success = true,
                    }),
                },
            }),
        });

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Count(x => x.EventData.Is(WorkflowRoleReplyRecordedEvent.Descriptor)).Should().Be(1);

        var fact = persisted.Single(x => x.EventData.Is(WorkflowRoleReplyRecordedEvent.Descriptor))
            .EventData
            .Unpack<WorkflowRoleReplyRecordedEvent>();
        fact.RunId.Should().Be("workflow-run-role-reply");
        fact.RoleActorId.Should().Be("workflow-run-role-reply:role_a");
        fact.RoleId.Should().Be("workflow-run-role-reply:role_a");
        fact.SessionId.Should().Be("session-1");
        fact.Content.Should().Be("reply");
        fact.ReasoningContent.Should().Be("reasoning");
        fact.ContentEmitted.Should().BeTrue();
        fact.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowRunGAgent_Constructor_ShouldValidateRequiredDependencies()
    {
        var runtime = new RecordingActorRuntime();
        var eventModuleFactory = new RecordingEventModuleFactory();
        var packs = Array.Empty<IWorkflowModulePack>();

        Action missingRuntime = () => new WorkflowRunGAgent(null!, runtime, eventModuleFactory, packs);
        Action missingDispatchPort = () => new WorkflowRunGAgent(runtime, null!, eventModuleFactory, packs);
        Action missingEventModuleFactory = () => new WorkflowRunGAgent(runtime, runtime, null!, packs);
        Action missingPacks = () => new WorkflowRunGAgent(runtime, runtime, eventModuleFactory, null!);

        missingRuntime.Should().Throw<ArgumentNullException>().WithParameterName("runtime");
        missingDispatchPort.Should().Throw<ArgumentNullException>().WithParameterName("dispatchPort");
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

        await agent.HandleChatRequest(new WorkflowChatRequestEvent
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
        await agent.HandleChatRequest(new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent());

        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should()
            .ContainSingle(x => x.Content == "Workflow execution stopped.");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenWorkflowLlmIntentReceived_ShouldInvokeLlmAndPublishWorkflowEvents()
    {
        var eventStore = new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var llm = new RecordingWorkflowIntentLlmProvider();
        var publisher = new RecordingEventPublisher();
        var agent = new WorkflowRoleGAgent(llm)
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        SetAgentId(agent, "workflow-role-agent");
        await agent.ActivateAsync();

        await agent.HandleWorkflowRoleInitialize(new WorkflowRoleInitializeEvent
        {
            RoleId = "assistant",
            RoleName = "Assistant",
            ProviderName = "mock",
            SystemPrompt = "workflow role",
        });
        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "step-1",
            SessionId = "session-1",
            Prompt = "hello",
            Model = "model-a",
            MaxToolRounds = 3,
            UserMemoryPrompt = "remember this",
            Headers = { ["trace-id"] = "trace-1" },
            Annotations = { ["annotation"] = "value" },
        });

        llm.Requests.Should().ContainSingle();
        var request = llm.Requests[0];
        request.Messages.Last().Content.Should().Be("hello");
        request.LlmControl.Should().NotBeNull();
        request.LlmControl!.ModelOverride.Should().Be("model-a");
        request.LlmControl.MaxToolRoundsOverride.Should().Be(3);
        request.LlmControl.UserMemoryPrompt.Should().Be("remember this");
        request.Metadata.Should().NotBeNull();
        request.Metadata!.Should().ContainKey("trace-id").WhoseValue.Should().Be("trace-1");
        request.Metadata.Should().ContainKey("annotation").WhoseValue.Should().Be("value");

        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationStartedEvent>()
            .Should()
            .ContainSingle(x => x.RunId == "run-1" && x.StepId == "step-1" && x.SessionId == "session-1");
        var chunks = publisher.Published.Select(x => x.evt).OfType<WorkflowLlmStreamChunkEvent>().ToList();
        chunks.Should().Contain(x => x.DeltaContent == "workflow ");
        chunks.Should().Contain(x => x.DeltaReasoningContent == "reasoning");
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should()
            .ContainSingle(x =>
                x.Success &&
                x.Content == "workflow answer" &&
                x.ReasoningContent == "reasoning" &&
                x.RoleActorId == "workflow-role-agent");

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        var completion = persisted
            .Where(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.RoleId.Should().Be("assistant");
        completion.SessionId.Should().Be("session-1");
        completion.Content.Should().Be("workflow answer");
        completion.ReasoningContent.Should().Be("reasoning");
        completion.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenWorkflowLlmProviderFails_ShouldPublishFailedWorkflowCompletion()
    {
        var eventStore = new InMemoryEventStore();
        var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
            eventStore,
            new ThrowingWorkflowIntentLlmProvider(new InvalidOperationException(" provider failed \n")),
            "workflow-role-agent-failure");

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-failure",
            StepId = "step-failure",
            SessionId = "session-failure",
            Prompt = "hello",
        });

        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationStartedEvent>()
            .Should()
            .ContainSingle(x => x.RunId == "run-failure" && x.StepId == "step-failure" && x.SessionId == "session-failure");
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should()
            .ContainSingle(x =>
                !x.Success &&
                x.RunId == "run-failure" &&
                x.StepId == "step-failure" &&
                x.SessionId == "session-failure" &&
                x.RoleActorId == "workflow-role-agent-failure" &&
                x.Error == "provider failed");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenWorkflowLlmProviderCancelsAfterTimeout_ShouldPublishTimeoutCompletion()
    {
        var eventStore = new InMemoryEventStore();
        var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
            eventStore,
            new CancellationWorkflowIntentLlmProvider(),
            "workflow-role-agent-timeout");

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-timeout",
            StepId = "step-timeout",
            SessionId = "session-timeout",
            Prompt = "hello",
            TimeoutMs = 1,
        });

        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationStartedEvent>()
            .Should()
            .ContainSingle(x => x.RunId == "run-timeout" && x.StepId == "step-timeout" && x.SessionId == "session-timeout");
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should()
            .ContainSingle(x =>
                !x.Success &&
                x.RunId == "run-timeout" &&
                x.StepId == "step-timeout" &&
                x.SessionId == "session-timeout" &&
                x.RoleActorId == "workflow-role-agent-timeout" &&
                x.Error == "LLM request timed out after 1ms");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenWorkflowLlmProviderStreamsToolCallFragments_ShouldPersistAssembledToolCall()
    {
        var eventStore = new InMemoryEventStore();
        var (agent, _) = await CreateActivatedWorkflowRoleAgentAsync(
            eventStore,
            new ToolCallWorkflowIntentLlmProvider(),
            "workflow-role-agent-tools");

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-tools",
            StepId = "step-tools",
            SessionId = "session-tools",
            Prompt = "hello",
        });

        var completion = (await eventStore.GetEventsAsync(agent.Id))
            .Where(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        var toolCall = completion.ToolCalls.Should().ContainSingle().Subject;
        toolCall.CallId.Should().Be("call-1");
        toolCall.ToolName.Should().Be("lookup");
        Assert.Equal("""{"query":"aevatar"}""", toolCall.ArgumentsJson);
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenWorkflowInitializationUsesSparsePayload_ShouldNormalizeDefaults()
    {
        var eventStore = new InMemoryEventStore();
        await using var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new WorkflowRoleGAgent(new RecordingWorkflowIntentLlmProvider())
        {
            Services = services,
            EventPublisher = new RecordingEventPublisher(),
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        SetAgentId(agent, "workflow-role-agent-sparse-init");
        await agent.ActivateAsync();

        await agent.HandleWorkflowRoleInitialize(new WorkflowRoleInitializeEvent
        {
            RoleId = "",
            RoleName = "",
            ProviderName = " ",
            Model = " ",
            SystemPrompt = "",
            MaxTokens = 0,
            MaxToolRounds = 0,
            MaxHistoryMessages = 0,
            EventModules = " ",
            EventRoutes = " ",
        });

        agent.State.RoleId.Should().BeEmpty();
        agent.State.RoleName.Should().BeEmpty();
        agent.State.ConfigOverrides.Should().NotBeNull();
        agent.State.ConfigOverrides.ProviderName.Should().BeEmpty();
        agent.State.ConfigOverrides.Model.Should().BeEmpty();
        agent.State.ConfigOverrides.HasTemperature.Should().BeFalse();
        agent.State.ConfigOverrides.HasMaxTokens.Should().BeFalse();
        agent.State.ConfigOverrides.HasMaxToolRounds.Should().BeFalse();
        agent.State.ConfigOverrides.HasMaxHistoryMessages.Should().BeFalse();
        agent.EffectiveConfig.Temperature.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenWorkflowIntentHasSparseFields_ShouldPublishDefaultFailureAndNoMetadataRequest()
    {
        var eventStore = new InMemoryEventStore();
        var llm = new EmptyMessageThrowingWorkflowIntentLlmProvider();
        var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
            eventStore,
            llm,
            "workflow-role-agent-sparse-intent");

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent());

        llm.Requests.Should().ContainSingle();
        var request = llm.Requests[0];
        request.Messages.Last().Content.Should().Be("[content]");
        request.LlmControl.Should().NotBeNull();
        request.LlmControl!.ModelOverride.Should().BeNullOrEmpty();
        request.LlmControl.UserMemoryPrompt.Should().BeNullOrEmpty();
        request.LlmControl.MaxToolRoundsOverride.Should().BeNull();
        request.Metadata.Should().NotBeNull();
        request.Metadata.Should().BeEmpty();
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationStartedEvent>()
            .Should()
            .ContainSingle(x => x.RunId == "" && x.StepId == "" && x.SessionId == "");
        publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should()
            .ContainSingle(x =>
                !x.Success &&
                x.RunId == "" &&
                x.StepId == "" &&
                x.SessionId == "" &&
                x.Error == "LLM request failed.");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenWorkflowIntentStreamsContentPartsAndAnonymousTools_ShouldPersistOutputParts()
    {
        var eventStore = new InMemoryEventStore();
        var (agent, _) = await CreateActivatedWorkflowRoleAgentAsync(
            eventStore,
            new ContentPartAndAnonymousToolWorkflowIntentLlmProvider(),
            "workflow-role-agent-parts");

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-parts",
            StepId = "step-parts",
            SessionId = "session-parts",
            Prompt = "describe",
        });

        var completion = (await eventStore.GetEventsAsync(agent.Id))
            .Where(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Content.Should().BeEmpty();
        completion.ContentEmitted.Should().BeFalse();
        completion.OutputParts.Should().ContainSingle(x => x.Kind == ChatContentPartKind.Text && x.Text == "part-only");
        completion.ToolCalls.Should().HaveCount(2);
        completion.ToolCalls.Should().ContainSingle(x => x.ArgumentsJson == "{}");
        completion.ToolCalls.Should().ContainSingle(x => x.ArgumentsJson == "[]");
    }

    [Fact]
    public void WorkflowRoleGAgent_ResolveWorkflowRequestInputParts_ShouldCombinePromptWithProtoParts()
    {
        var method = typeof(WorkflowRoleGAgent).GetMethod(
            "ResolveWorkflowRequestInputParts",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var withoutPrompt = new ChatRequestEvent();
        withoutPrompt.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Text,
            Text = "part-only",
        });
        var partsWithoutPrompt = (IReadOnlyList<ContentPart>)method!.Invoke(null, [withoutPrompt])!;
        partsWithoutPrompt.Should().ContainSingle();
        partsWithoutPrompt[0].Text.Should().Be("part-only");

        var withPrompt = new ChatRequestEvent { Prompt = "prompt" };
        withPrompt.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Text,
            Text = "part",
        });
        var partsWithPrompt = (IReadOnlyList<ContentPart>)method.Invoke(null, [withPrompt])!;
        partsWithPrompt.Should().HaveCount(2);
        partsWithPrompt[0].Text.Should().Be("prompt");
        partsWithPrompt[1].Text.Should().Be("part");
    }

    [Fact]
    public void WorkflowRoleGAgent_ToolCallAccumulator_ShouldPromoteAndReuseStreamingToolDeltas()
    {
        var (accumulator, track, build) = CreateWorkflowToolCallAccumulator();

        track.Invoke(accumulator, [new ToolCall { Id = "", Name = "lookup", ArgumentsJson = "{" }]);
        track.Invoke(accumulator, [new ToolCall { Id = "", Name = "", ArgumentsJson = "\"q\"" }]);
        track.Invoke(accumulator, [new ToolCall { Id = "known-1", Name = "", ArgumentsJson = "}" }]);
        track.Invoke(accumulator, [new ToolCall { Id = "", Name = "", ArgumentsJson = "!" }]);

        var toolCalls = (IReadOnlyList<ToolCall>)build.Invoke(accumulator, [])!;
        toolCalls.Should().ContainSingle();
        toolCalls[0].Id.Should().Be("known-1");
        toolCalls[0].Name.Should().Be("lookup");
        toolCalls[0].ArgumentsJson.Should().Be("{\"q\"}!");
    }

    [Fact]
    public void WorkflowRoleGAgent_ToolCallAccumulator_ShouldReplaceNonFirstAnonymousOrderKey()
    {
        var (accumulator, track, build) = CreateWorkflowToolCallAccumulator();
        var type = accumulator.GetType();

        track.Invoke(accumulator, [new ToolCall { Id = "first", Name = "first_tool", ArgumentsJson = "{}" }]);
        type.GetField("_lastKnownKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(accumulator, null);

        track.Invoke(accumulator, [new ToolCall { Id = "", Name = "second_tool", ArgumentsJson = "[" }]);
        track.Invoke(accumulator, [new ToolCall { Id = "second", Name = "", ArgumentsJson = "]" }]);

        var toolCalls = (IReadOnlyList<ToolCall>)build.Invoke(accumulator, [])!;
        toolCalls.Should().HaveCount(2);
        toolCalls[0].Id.Should().Be("first");
        toolCalls[1].Id.Should().Be("second");
        toolCalls[1].Name.Should().Be("second_tool");
        toolCalls[1].ArgumentsJson.Should().Be("[]");
    }

    [Fact]
    public void WorkflowRoleGAgent_ToolCallAccumulator_ShouldRecoverWhenActiveAnonymousKeyIsStale()
    {
        var (accumulator, track, build) = CreateWorkflowToolCallAccumulator();
        var type = accumulator.GetType();

        type.GetField("_activeAnonymousKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(accumulator, "anon:missing");
        track.Invoke(accumulator, [new ToolCall { Id = "known", Name = "", ArgumentsJson = "" }]);

        var toolCalls = (IReadOnlyList<ToolCall>)build.Invoke(accumulator, [])!;
        toolCalls.Should().ContainSingle();
        toolCalls[0].Id.Should().Be("known");
        toolCalls[0].Name.Should().BeEmpty();
        toolCalls[0].ArgumentsJson.Should().BeEmpty();
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

    private static async Task<(WorkflowRoleGAgent Agent, RecordingEventPublisher Publisher)> CreateActivatedWorkflowRoleAgentAsync(
        IEventStore eventStore,
        ILLMProviderFactory llmProviderFactory,
        string agentId)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var publisher = new RecordingEventPublisher();
        var agent = new WorkflowRoleGAgent(llmProviderFactory)
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        SetAgentId(agent, agentId);
        await agent.ActivateAsync();
        await agent.HandleWorkflowRoleInitialize(new WorkflowRoleInitializeEvent
        {
            RoleId = "assistant",
            RoleName = "Assistant",
            ProviderName = "mock",
            SystemPrompt = "workflow role",
        });
        return (agent, publisher);
    }

    private static WorkflowRunGAgent CreateRunAgent(
        RecordingActorRuntime? runtime = null,
        IEventModuleFactory<IWorkflowExecutionContext>? eventModuleFactory = null,
        IEnumerable<IWorkflowModulePack>? packs = null,
        IEventStore? eventStore = null,
        IWorkflowDefinitionResolver? workflowResolver = null)
    {
        runtime ??= new RecordingActorRuntime();
        eventModuleFactory ??= new RecordingEventModuleFactory();
        packs ??= [];
        eventStore ??= new InMemoryEventStore();

        var services = BuildServices(eventStore, workflowResolver);
        var agent = new WorkflowRunGAgent(runtime, runtime, eventModuleFactory, packs, workflowResolver)
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
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddAevatarWorkflow();

        if (workflowResolver != null)
            services.AddSingleton(workflowResolver);

        return services.BuildServiceProvider();
    }

    private static EventEnvelope Envelope(
        IMessage message,
        string publisherId,
        TopologyAudience direction,
        string? id = null)
    {
        return new EventEnvelope
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
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

    private static async Task SeedRuntimeContextAsync(WorkflowRunGAgent agent)
    {
        var host = (IWorkflowExecutionStateHost)agent;
        await host.UpdateExecutionContextAsync(
            new WorkflowRunExecutionContextDelta
            {
                ClearLlm = true,
                ClearConnector = true,
                Llm = new WorkflowRunLlmExecutionContextDelta
                {
                    ModelOverride = "model",
                    MaxToolRoundsOverride = 2,
                    UserMemoryPrompt = "memory",
                },
                Connector = new WorkflowRunConnectorExecutionContextDelta
                {
                    HttpAuthorization = "Bearer secret",
                },
            });
        host.RuntimeContext.RequestPassthroughMetadata.Set("trace-id", "abc");
    }

    private static void AssertRuntimeContextCleared(WorkflowRunGAgent agent)
    {
        var host = (IWorkflowExecutionStateHost)agent;
        host.ExecutionContextSnapshot.Llm.Should().BeNull();
        host.ExecutionContextSnapshot.Connector.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();
    }

    private static (object Accumulator, MethodInfo Track, MethodInfo Build) CreateWorkflowToolCallAccumulator()
    {
        var type = typeof(WorkflowRoleGAgent).GetNestedType(
            "WorkflowToolCallAccumulator",
            BindingFlags.NonPublic);
        type.Should().NotBeNull();
        var accumulator = Activator.CreateInstance(type!);
        accumulator.Should().NotBeNull();
        var track = type!.GetMethod("TrackDelta", BindingFlags.Public | BindingFlags.Instance);
        var build = type.GetMethod("BuildToolCalls", BindingFlags.Public | BindingFlags.Instance);
        track.Should().NotBeNull();
        build.Should().NotBeNull();
        return (accumulator!, track!, build!);
    }

    private static string BuildValidWorkflowYaml(
        string roleId,
        string roleName,
        string? provider = null,
        string? model = null,
        string? workflowName = null,
        bool includeAgentKind = true)
    {
        var name = workflowName ?? "wf_valid";
        var agentKindLine = includeAgentKind ? "\n    agent_kind: workflow.assistant-role" : string.Empty;
        var providerLine = string.IsNullOrWhiteSpace(provider) ? string.Empty : $"\n    provider: \"{provider}\"";
        var modelLine = string.IsNullOrWhiteSpace(model) ? string.Empty : $"\n    model: \"{model}\"";
        return $$"""
                 name: {{name}}
                 roles:
                   - id: "{{roleId}}"
                     name: "{{roleName}}"
                     system_prompt: "helpful role"{{agentKindLine}}{{providerLine}}{{modelLine}}
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
                   agent_kind: workflow.assistant-role
                   system_prompt: "helpful role"
                   provider: openai
                   model: gpt-5.4
                   temperature: 0.2
                   max_tokens: 256
                   max_tool_rounds: 4
                   max_history_messages: 30
                   event_modules: "llm_handler,tool_handler"
                   event_routes: |
                     event.type == ChatRequestEvent -> llm_handler
               steps:
                 - id: step_1
                   type: transform
               """;
    }

    private sealed class RecordingEventPublisher : IEventPublisher, ICommittedStateEventPublisher
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

        Task ICommittedStateEventPublisher.PublishAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
        {
            _ = audience;
            _ = sourceEnvelope;
            _ = options;
            Published.Add((evt, TopologyAudience.Self));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowIntentLlmProvider : ILLMProviderFactory, ILLMProvider
    {
        public List<LLMRequest> Requests { get; } = [];
        public string Name => "mock";

        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new LLMStreamChunk { DeltaContent = "workflow " };
            yield return new LLMStreamChunk { DeltaReasoningContent = "reasoning" };
            yield return new LLMStreamChunk { DeltaContent = "answer" };
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
            await Task.CompletedTask;
        }
    }

    private abstract class WorkflowIntentLlmProviderBase : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "mock";

        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public abstract IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            CancellationToken ct = default);
    }

    private sealed class ThrowingWorkflowIntentLlmProvider(Exception exception) : WorkflowIntentLlmProviderBase
    {
        public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            if (exception is not null)
                throw exception;
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }
    }

    private sealed class EmptyMessageThrowingWorkflowIntentLlmProvider : WorkflowIntentLlmProviderBase
    {
        public List<LLMRequest> Requests { get; } = [];

        public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.CompletedTask;
            var emit = false;
            if (emit)
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
            throw new InvalidOperationException(" ");
        }
    }

    private sealed class CancellationWorkflowIntentLlmProvider : WorkflowIntentLlmProviderBase
    {
        public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            while (!ct.IsCancellationRequested)
                Thread.Yield();
            await Task.CompletedTask;
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }
    }

    private sealed class ToolCallWorkflowIntentLlmProvider : WorkflowIntentLlmProviderBase
    {
        private int _calls;

        public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) > 1)
            {
                yield return new LLMStreamChunk { DeltaContent = "done" };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
                yield break;
            }

            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-1",
                    Name = "lookup",
                    ArgumentsJson = """{"query":""",
                },
            };
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "",
                    Name = "",
                    ArgumentsJson = "\"aevatar\"}",
                },
            };
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
            await Task.CompletedTask;
        }
    }

    private sealed class ContentPartAndAnonymousToolWorkflowIntentLlmProvider : WorkflowIntentLlmProviderBase
    {
        private int _calls;

        public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) > 1)
            {
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
                yield break;
            }

            yield return new LLMStreamChunk
            {
                DeltaContentPart = ContentPart.TextPart("part-only"),
            };
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "",
                    Name = "",
                    ArgumentsJson = "{}",
                },
            };
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "known-1",
                    Name = "search",
                    ArgumentsJson = "",
                },
            };
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "",
                    Name = "",
                    ArgumentsJson = "[]",
                },
            };
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public int CreateCalls { get; private set; }
        public List<(string agentKind, string actorId)> CreateByKindCalls { get; } = [];
        public List<FakeActor> CreatedActors { get; } = [];
        public List<FakeWorkflowRunChildAgent> CreatedChildWorkflowAgents { get; } = [];
        public List<(string parent, string child)> Linked { get; } = [];
        public List<string> Destroyed { get; } = [];
        public List<string> Unlinked { get; } = [];
        public string? ThrowOnGetAsyncActorId { get; set; }
        public Exception? CreateByKindException { get; set; }

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

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"{agentKind}:actor-{CreateByKindCalls.Count + 1}";
            CreateByKindCalls.Add((agentKind.Trim(), actorId));
            if (CreateByKindException != null)
                throw CreateByKindException;

            var existing = CreatedActors.FirstOrDefault(x => x.Id == actorId);
            if (existing != null)
                return Task.FromResult<IActor>(existing);

            var actor = new FakeActor(actorId, new FakeRoleAgent(actorId));
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

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actor = CreatedActors.FirstOrDefault(x => x.Id == actorId)
                        ?? throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
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
        public WorkflowRoleInitializeEvent? LastInitializeEvent { get; private set; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope.Payload?.Is(WorkflowRoleInitializeEvent.Descriptor) == true)
            {
                var evt = envelope.Payload.Unpack<WorkflowRoleInitializeEvent>();
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
