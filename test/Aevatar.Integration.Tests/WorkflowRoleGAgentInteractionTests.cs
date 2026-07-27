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

public sealed class WorkflowRoleGAgentInteractionTests : WorkflowGAgentTestBase
{
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
                    agent_kind: workflow.role-agent
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
                "workflow.role-agent",
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
                    agent_kind: " workflow.role-agent "
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
                "workflow.role-agent",
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
        public async Task WorkflowRunGAgent_WhenRoleAgentKindMissing_ShouldUseDefaultRoleAgentKindAndRun()
        {
            var runtime = new RecordingActorRuntime();
            var agent = CreateRunAgent(runtime: runtime);
            SetAgentId(agent, "workflow-run-default-role");
            await agent.BindWorkflowRunDefinitionAsync(
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA", workflowName: "wf_default_role", includeAgentKind: false),
                "wf_default_role",
                runId: "run-default-role");

            await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });

            runtime.CreateByKindCalls.Should().ContainSingle().Which.Should().Be((
                WorkflowRoleConventions.DefaultAgentKind,
                "workflow-run-default-role:role_a"));
            runtime.CreateCalls.Should().Be(0);
            runtime.Linked.Should().ContainSingle()
                .Which.Should().Be(("workflow-run-default-role", "workflow-run-default-role:role_a"));

            var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
            roleAgent.LastInitializeEvent.Should().NotBeNull();
            roleAgent.LastInitializeEvent!.RoleId.Should().Be("role_a");
            roleAgent.LastInitializeEvent.RoleName.Should().Be("RoleA");
        }

        [Fact]
        public async Task WorkflowRunGAgent_WhenRoleAgentKindIsDefaultPrimary_ShouldCreateRoleActorByKindAndInitializeIt()
        {
            var runtime = new RecordingActorRuntime();
            var agent = CreateRunAgent(runtime: runtime);
            SetAgentId(agent, "workflow-run-public-alias");
            await agent.BindWorkflowRunDefinitionAsync(
                "definition-1",
                """
                name: wf_public_alias
                roles:
                  - id: assistant
                    name: Assistant
                    agent_kind: workflow.role-agent
                steps:
                  - id: step_1
                    type: llm_call
                    target_role: assistant
                """,
                "wf_public_alias",
                runId: "run-public-alias");

            await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "hello", SessionId = "s1" });

            runtime.CreateByKindCalls.Should().ContainSingle().Which.Should().Be((
                WorkflowRoleConventions.DefaultAgentKind,
                "workflow-run-public-alias:assistant"));
            runtime.CreateCalls.Should().Be(0);
            runtime.Linked.Should().ContainSingle()
                .Which.Should().Be(("workflow-run-public-alias", "workflow-run-public-alias:assistant"));

            var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
            roleAgent.LastInitializeEvent.Should().NotBeNull();
            roleAgent.LastInitializeEvent!.RoleId.Should().Be("assistant");
            roleAgent.LastInitializeEvent.RoleName.Should().Be("Assistant");
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
                CallerCredential = new WorkflowCallerCredential
                {
                    BearerToken = "token-123",
                },
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
            request.ToolContext.Should().NotBeNull();
            request.ToolContext!.Credentials.NyxIdAccessToken.Should().Be("token-123");
            request.ToolContext.Credentials.NyxIdOrgToken.Should().Be("token-123");
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

        // 06-24-studio-workflow-first-delivery W1: the channel-less Direct/studio chat runs as a
        // workflow whose llm_call step dispatches a WorkflowLlmExecutionIntent carrying the run's
        // scope. No inbound channel stamps the caller scope on this path, so without threading the
        // run scope into the tool caller, scope-scoped tools (aevatar_*) see no scope and fail with
        // caller_scope_unavailable. Assert the role actor fills Caller.ScopeId + OwnerSubject from
        // the intent scope (mirrors the channel inbound path that sets both from the registration scope).
        [Fact]
        public async Task WorkflowRoleGAgent_WhenWorkflowIntentCarriesScope_ShouldPopulateToolCallerScope()
        {
            var eventStore = new InMemoryEventStore();
            var llm = new RecordingWorkflowIntentLlmProvider();
            var (agent, _) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-scope");

            await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-scope",
                StepId = "step-scope",
                SessionId = "session-scope",
                Prompt = "make a daily tech-news workflow",
                ScopeId = "scope-studio-1",
            });

            llm.Requests.Should().ContainSingle();
            var toolContext = llm.Requests[0].ToolContext;
            toolContext.Should().NotBeNull();
            toolContext!.Caller.ScopeId.Should().Be("scope-studio-1");
            toolContext.Caller.OwnerSubject.Should().Be("scope-studio-1");
        }

        // No-op guards: the role actor must not fabricate a scope when the run has none (scope-less
        // local runs stay scope-less), and an already-populated caller scope must win over the run
        // scope (so any future inbound-stamped caller is never overwritten).
        [Fact]
        public async Task WorkflowRoleGAgent_WhenWorkflowIntentHasNoScope_ShouldLeaveToolCallerScopeEmpty()
        {
            var eventStore = new InMemoryEventStore();
            var llm = new RecordingWorkflowIntentLlmProvider();
            var (agent, _) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-no-scope");

            await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-no-scope",
                StepId = "step-no-scope",
                SessionId = "session-no-scope",
                Prompt = "hello",
            });

            llm.Requests.Should().ContainSingle();
            var toolContext = llm.Requests[0].ToolContext;
            toolContext.Should().NotBeNull();
            toolContext!.Caller.ScopeId.Should().BeNullOrEmpty();
            toolContext.Caller.OwnerSubject.Should().BeNullOrEmpty();
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenWorkflowLlmIntentHasMetadataOnlyAuthorization_ShouldNotPromoteToolCredentials()
        {
            var eventStore = new InMemoryEventStore();
            var llm = new RecordingWorkflowIntentLlmProvider();
            var (agent, _) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-metadata-auth");

            await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-metadata-auth",
                StepId = "step-metadata-auth",
                SessionId = "session-metadata-auth",
                Prompt = "hello",
                Headers =
                {
                    ["connector.http.authorization"] = "Bearer metadata-token",
                    ["trace-id"] = "trace-1",
                },
            });

            llm.Requests.Should().ContainSingle();
            var request = llm.Requests[0];
            request.ToolContext.Should().NotBeNull();
            request.ToolContext!.Credentials.NyxIdAccessToken.Should().BeNull();
            request.ToolContext.Credentials.NyxIdOrgToken.Should().BeNull();
            request.Metadata.Should().NotBeNull();
            request.Metadata!.Should().NotContainKey("connector.http.authorization");
            request.Metadata.Should().ContainKey("trace-id").WhoseValue.Should().Be("trace-1");
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
                "workflow-role-agent-tools",
                [new SuccessfulWorkflowTool("lookup")]);

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
                "workflow-role-agent-parts",
                [new SuccessfulWorkflowTool("search")]);

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
}
