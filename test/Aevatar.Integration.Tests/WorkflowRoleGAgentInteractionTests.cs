using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
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
using Microsoft.Extensions.Time.Testing;
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            var agent = new WorkflowRoleGAgent(UnexpectedAgentToolExecutionPort.Instance, llm)
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
                    Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
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
            request.ToolContext.Credentials.SenderNyxIdAccessToken.Should().Be("token-123");
            request.ToolContext.Credentials.NyxIdCredentialKind.Should()
                .Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
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
        public async Task WorkflowRoleGAgent_WhenCompletedIntentIsRedelivered_ShouldNotPublishOrphanStartedEvent()
        {
            var eventStore = new InMemoryEventStore();
            var llm = new RecordingWorkflowIntentLlmProvider();
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-redelivery");
            var intent = new WorkflowLlmExecutionIntent
            {
                RunId = "run-redelivery",
                StepId = "step-redelivery",
                SessionId = "session-redelivery",
                Prompt = "hello",
            };

            await agent.HandleWorkflowLlmExecutionIntent(intent);
            await agent.HandleWorkflowLlmExecutionIntent(intent.Clone());

            llm.Requests.Should().ContainSingle();
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationStartedEvent>()
                .Should().ContainSingle();
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle();
        }

        // 06-24-studio-workflow-first-delivery W1: the channel-less Direct/studio chat runs as a
        // workflow whose llm_call step dispatches a WorkflowLlmExecutionIntent carrying the run's
        // scope. No inbound channel stamps the caller scope on this path, so without threading the
        // run scope into the tool caller, scope-scoped tools (aevatar_*) see no scope and fail with
        // caller_scope_unavailable. Assert the role actor fills Caller.ScopeId from the intent scope
        // without inventing a NyxID OwnerSubject from a resource-scope identity.
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
            toolContext.Caller.OwnerSubject.Should().BeNullOrEmpty();
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
            var llm = new ThrowingWorkflowIntentLlmProvider(
                new InvalidOperationException(" provider failed \n"));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
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
                    x.Error == "llm_request_failed: provider failed");
            var terminal = agent.State.Sessions["session-failure"];
            terminal.Completed.Should().BeTrue();
            terminal.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            terminal.FailureCode.Should().Be("LLM_REQUEST_FAILED");
            llm.CallCount.Should().Be(1);

            await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-failure");

            llm.CallCount.Should().Be(1,
                "activation must replay the committed terminal instead of invoking the provider again");
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenToolRequiresAuthorization_ShouldCommitBlockedTerminal()
        {
            var eventStore = new InMemoryEventStore();
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new AuthorizationRequiredWorkflowIntentLlmProvider(),
                "workflow-role-agent-authorization-required");

            await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-authorization-required",
                StepId = "step-authorization-required",
                SessionId = "session-authorization-required",
                Prompt = "read calendar",
            });

            var terminal = agent.State.Sessions["session-authorization-required"];
            terminal.Completed.Should().BeTrue();
            terminal.Outcome.Should().Be(RoleChatSessionOutcome.Blocked);
            terminal.FailureCode.Should().Be("AUTHORIZATION_REQUIRED");
            terminal.SafeMessage.Should().Be("Connect Calendar to continue.");
            terminal.AuthorizationRequired.Should().NotBeNull();
            terminal.AuthorizationRequired!.ServiceSlug.Should().Be("calendar");
            terminal.ToolReceipts.Should().ContainSingle(receipt =>
                receipt.Status == AgentToolReceiptStatus.AuthorizationRequired);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.Error ==
                    "authorization_required: Connect Calendar to continue.");
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

            agent.State.Sessions["session-timeout"].WorkflowLlmCompletionDeliveryStatus.Should()
                .Be(WorkflowLlmCompletionDeliveryStatus.Dispatched);
            publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationStartedEvent>()
                .Should()
                .ContainSingle(x => x.RunId == "run-timeout" && x.StepId == "step-timeout" && x.SessionId == "session-timeout");
            var completed = publisher.Published.Select(x => x.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle().Which;
            completed.Success.Should().BeFalse();
            completed.RunId.Should().Be("run-timeout");
            completed.StepId.Should().Be("step-timeout");
            completed.SessionId.Should().Be("session-timeout");
            completed.RoleActorId.Should().Be("workflow-role-agent-timeout");
            completed.Error.Should().Be(
                "llm_timeout: The LLM turn exceeded its deadline. Please try again.");
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

            var persisted = await eventStore.GetEventsAsync(agent.Id);
            var started = persisted
                .Where(x => x.EventData.Is(RoleChatSessionStartedEvent.Descriptor))
                .Select(x => x.EventData.Unpack<RoleChatSessionStartedEvent>())
                .Should().ContainSingle().Which;
            started.RecoveryCheckpoint.Stage.Should().Be(RoleChatRecoveryCheckpointStage.ModelReady);
            started.RecoveryCheckpoint.WorkflowLlmCompletionDeliveryContext.Should().BeEquivalentTo(
                new WorkflowLlmCompletionDeliveryContext
                {
                    RunId = "run-tools",
                    StepId = "step-tools",
                    SessionId = "session-tools",
                });
            persisted
                .Where(x => x.EventData.Is(RoleChatRecoveryCheckpointUpdatedEvent.Descriptor))
                .Select(x => x.EventData.Unpack<RoleChatRecoveryCheckpointUpdatedEvent>().Checkpoint.Stage)
                .Should().Contain(RoleChatRecoveryCheckpointStage.ToolBatchPrepared);
            var completion = persisted
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
        public async Task WorkflowRoleGAgent_WhenCheckpointRecoveryContinues_ShouldRetainRequestLocalToolCatalog()
        {
            var eventStore = new FailCompletionCheckpointEventStore();
            var llm = new ToolCallWorkflowIntentLlmProvider();
            var tool = new SuccessfulWorkflowTool("lookup");
            var registry = new FixedToolSetRegistry(
                "workflow.request.tools",
                new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-checkpoint-recovery",
                toolSetRegistry: registry);
            var intent = new WorkflowLlmExecutionIntent
            {
                RunId = "run-checkpoint-recovery",
                StepId = "step-checkpoint-recovery",
                SessionId = "session-checkpoint-recovery",
                Prompt = "lookup",
                AgentToolScope = new WorkflowAgentToolScope
                {
                    RestrictAllowedToolNames = true,
                    RestrictToolSets = true,
                    AllowedToolNames = { tool.Name },
                    ToolSetRefs = { "workflow.request.tools" },
                },
            };

            await agent.HandleWorkflowLlmExecutionIntent(intent);

            tool.ExecuteCount.Should().Be(1);
            llm.CallCount.Should().Be(1);
            agent.State.Sessions[intent.SessionId].Completed.Should().BeFalse();
            var recovery = publisher.Published.Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().ContainSingle(
                    "published={0}; pending={1}; sessions={2}",
                    string.Join(',', publisher.Published.Select(static item => item.evt.GetType().Name)),
                    agent.State.PendingApproval?.RequestId ?? "<null>",
                    string.Join(',', agent.State.Sessions.Select(static pair =>
                        $"{pair.Key}:{pair.Value.Outcome}:{pair.Value.FailureCode}"))).Which;

            await agent.HandleChatRecoveryContinuationRequestedAsync(recovery);

            tool.ExecuteCount.Should().Be(1,
                "the sealed external result must be adopted without another tool invocation");
            llm.CallCount.Should().Be(2);
            llm.Requests[1].Tools.Should().ContainSingle(candidate => candidate.Name == tool.Name);
            llm.Requests[1].ToolContext!.ToolVisibility.Allows(tool.Name).Should().BeTrue();
            var terminal = agent.State.Sessions[intent.SessionId];
            terminal.Completed.Should().BeTrue();
            terminal.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
            terminal.FinalContent.Should().Be("done");
            terminal.ToolCalls.Should().ContainSingle(call => call.CallId == "call-1");
            terminal.ToolResults.Should().ContainSingle(result =>
                result.CallId == "call-1" && result.Success);
            terminal.ToolReceipts.Should().ContainSingle(receipt =>
                receipt.CallId == "call-1" && receipt.Status == AgentToolReceiptStatus.Success);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    completed.Success &&
                    completed.RunId == intent.RunId &&
                    completed.StepId == intent.StepId &&
                    completed.SessionId == intent.SessionId);
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenPreparedRequestLocalToolRecovers_ShouldResolveFromCheckpointScope()
        {
            const string actorId = "workflow-role-agent-prepared-dynamic-recovery";
            const string sessionId = "session-prepared-dynamic-recovery";
            var eventStore = new InMemoryEventStore();
            var vault = new InMemorySecretVault();
            var tool = new SuccessfulWorkflowTool("dynamic_lookup");
            var registry = new FixedToolSetRegistry(
                "workflow.dynamic.tools",
                new FixedToolSource(tool));
            var llm = new RecordingWorkflowIntentLlmProvider();
            var (original, _) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                actorId,
                toolSetRegistry: registry,
                chatToolRecoverySecretVault: vault);
            var continuation = new WorkflowLlmToolApprovalContinuation
            {
                RunId = "run-prepared-dynamic-recovery",
                StepId = "step-prepared-dynamic-recovery",
                SessionId = sessionId,
                Model = "dynamic-recovery-model",
                RestrictToolSets = true,
                RestrictAllowedToolNames = true,
                ToolSetRefs = { "workflow.dynamic.tools" },
                AllowedToolNames = { tool.Name },
            };
            var toolContext = AgentToolExecutionContext.Empty with
            {
                InvocationSurface = AgentToolInvocationSurface.WorkflowLlmToolLoop,
                ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
                ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames([tool.Name]),
                Request = new AgentToolRequestIdentity(sessionId, "call-dynamic-recovery"),
                Chat = new AgentChatInvocationContext(
                    AgentChatInvocationSurface.WorkflowChat,
                    continuation.RunId,
                    sessionId,
                    null,
                    continuation.StepId,
                    null),
            };
            var request = new ChatRequestEvent
            {
                SessionId = sessionId,
                Prompt = "recover dynamic tool",
                ToolContext = toolContext.ToPayload(),
                LlmControl = new LLMControlContextPayload
                {
                    ModelOverride = continuation.Model,
                },
                WorkflowLlmToolApprovalContinuation = continuation.Clone(),
                WorkflowLlmCompletionDeliveryContext = new WorkflowLlmCompletionDeliveryContext
                {
                    RunId = continuation.RunId,
                    StepId = continuation.StepId,
                    SessionId = sessionId,
                },
            };
            await original.StartRecoverySessionForTestAsync(request, toolContext);
            await original.PrepareBatchAsync(new ChatToolBatchIntent(
                sessionId,
                Round: 0,
                [new ChatToolOperationIntent(
                    new ToolCall
                    {
                        Id = "call-dynamic-recovery",
                        Name = tool.Name,
                        ArgumentsJson = "{}",
                    },
                    toolContext,
                    AgentToolReplayPolicy.ReadOnlyRetryable,
                    new ToolPresentationDescriptor())]));
            original.State.Sessions[sessionId].RecoveryCheckpoint.Stage.Should()
                .Be(RoleChatRecoveryCheckpointStage.ToolBatchPrepared);
            tool.ExecuteCount.Should().Be(0);
            llm.Requests.Should().BeEmpty();

            var (recovered, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                actorId,
                toolSetRegistry: registry,
                chatToolRecoverySecretVault: vault);
            var recovery = publisher.Published.Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().ContainSingle(item => item.SessionId == sessionId).Which;

            await recovered.HandleChatRecoveryContinuationRequestedAsync(recovery);

            tool.ExecuteCount.Should().Be(1);
            llm.Requests.Should().ContainSingle();
            llm.Requests[0].Tools.Should().ContainSingle(candidate => candidate.Name == tool.Name);
            var terminal = recovered.State.Sessions[sessionId];
            terminal.Completed.Should().BeTrue();
            terminal.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
            terminal.ToolCalls.Should().ContainSingle(call => call.CallId == "call-dynamic-recovery");
            terminal.ToolResults.Should().ContainSingle(result =>
                result.CallId == "call-dynamic-recovery" && result.Success);
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenLaterProviderRoundFails_ShouldCommitExecutedToolFacts()
        {
            var eventStore = new InMemoryEventStore();
            var tool = new SuccessfulWorkflowTool("lookup");
            var llm = new ToolThenThrowWorkflowIntentLlmProvider(tool.Name);
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-partial-failure",
                tools: [tool]);
            var intent = new WorkflowLlmExecutionIntent
            {
                RunId = "run-partial-failure",
                StepId = "step-partial-failure",
                SessionId = "session-partial-failure",
                Prompt = "lookup then answer",
            };

            await agent.HandleWorkflowLlmExecutionIntent(intent);

            llm.CallCount.Should().Be(2);
            tool.ExecuteCount.Should().Be(1);
            var terminal = agent.State.Sessions[intent.SessionId];
            terminal.Completed.Should().BeTrue();
            terminal.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            terminal.FailureCode.Should().Be("LLM_REQUEST_FAILED");
            terminal.ToolCalls.Should().ContainSingle(call => call.CallId == "call-partial-failure");
            terminal.ToolResults.Should().ContainSingle(result =>
                result.CallId == "call-partial-failure" && result.Success);
            terminal.ToolReceipts.Should().ContainSingle(receipt =>
                receipt.CallId == "call-partial-failure" &&
                receipt.Status == AgentToolReceiptStatus.Success);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.Error == "llm_request_failed: later provider round failed");

            await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-partial-failure",
                tools: [tool]);

            llm.CallCount.Should().Be(2);
            tool.ExecuteCount.Should().Be(1);
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenPostExternalCheckpointMaterialIsInvalid_ShouldCommitOutcomeUncertain()
        {
            var eventStore = new InMemoryEventStore();
            var tool = new SuccessfulWorkflowTool("lookup");
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ToolCallWorkflowIntentLlmProvider(),
                "workflow-role-agent-post-external-material-failure",
                [tool],
                chatToolRecoverySecretVault: new InvalidResultReferenceSecretVault());

            await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-post-external",
                StepId = "step-post-external",
                SessionId = "session-post-external",
                Prompt = "lookup",
            });

            tool.ExecuteCount.Should().Be(1);
            var session = agent.State.Sessions["session-post-external"];
            session.Completed.Should().BeTrue();
            session.Outcome.Should().Be(RoleChatSessionOutcome.OutcomeUncertain);
            session.FailureCode.Should().Be("SESSION_OUTCOME_UNCERTAIN");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.RunId == "run-post-external" &&
                    completed.StepId == "step-post-external" &&
                    completed.SessionId == "session-post-external" &&
                    completed.Error.Contains("session_outcome_uncertain", StringComparison.Ordinal));
            publisher.Published.Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>().Should().BeEmpty();
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenStartedPublicationBlocks_ShouldApplyHostDeadline()
        {
            const int timeoutMs = 1_000;
            const string sessionId = "workflow-started-publication-deadline";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var deadlineProbe = new ApprovalResumeDeadlineProbe();
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new RecordingWorkflowIntentLlmProvider(),
                "workflow-role-agent-started-publication-deadline",
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));
            publisher.BeforePublishAsync = (evt, ct) => evt is WorkflowLlmInvocationStartedEvent
                ? deadlineProbe.HangAsync(ct)
                : Task.CompletedTask;

            var execution = agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-started-deadline",
                StepId = "step-started-deadline",
                SessionId = sessionId,
                Prompt = "deadline includes started publication",
            });
            await deadlineProbe.Started;

            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await deadlineProbe.CancellationObserved;
            await execution;

            var completion = (await eventStore.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == sessionId).Which;
            completion.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            completion.FailureCode.Should().Be("LLM_TIMEOUT");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.SessionId == sessionId &&
                    completed.Error.Contains("llm_timeout", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenCompletionPublisherIgnoresCancellation_ShouldReleaseNextTurn()
        {
            const int timeoutMs = 1_000;
            const string firstSessionId = "workflow-completion-publisher-deadline";
            const string nextSessionId = "workflow-completion-publisher-next-turn";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var provider = new RecordingWorkflowIntentLlmProvider();
            var publicationProbe = new IgnoringCancellationPostTurnProbe();
            var callbackScheduler = new RecordingWorkflowCompletionCallbackScheduler();
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                provider,
                "workflow-role-agent-completion-publisher-deadline",
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(
                    maxTurnDeadlineMs: 5_000,
                    postTurnProcessingTimeoutMs: timeoutMs),
                callbackScheduler: callbackScheduler);
            publisher.BeforePublishAsync = (evt, _) => evt is WorkflowLlmInvocationCompletedEvent
                {
                    SessionId: firstSessionId,
                }
                ? publicationProbe.HangIgnoringCancellationAsync()
                : Task.CompletedTask;

            var firstTurn = agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-completion-publisher-deadline",
                StepId = "step-completion-publisher-deadline",
                SessionId = firstSessionId,
                Prompt = "commit before the publisher hangs",
            });
            await publicationProbe.Started;
            agent.State.Sessions[firstSessionId].Completed.Should().BeTrue();

            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await firstTurn;

            var firstSession = agent.State.Sessions[firstSessionId];
            firstSession.WorkflowLlmCompletionDeliveryStatus.Should()
                .Be(WorkflowLlmCompletionDeliveryStatus.RetryScheduled);
            firstSession.WorkflowLlmCompletionDeliveryAttempt.Should().Be(1);
            var retryEnvelope = callbackScheduler.TimeoutRequests.Should()
                .ContainSingle().Subject.TriggerEnvelope;
            var retry = retryEnvelope.Payload
                .Unpack<WorkflowLlmCompletionDeliveryRetryFiredEvent>();
            retry.SessionId.Should().Be(firstSessionId);
            retry.DeliveryId.Should().Be(
                "run-completion-publisher-deadline:step-completion-publisher-deadline:" +
                firstSessionId);
            retry.Attempt.Should().Be(1);

            publicationProbe.Release();
            await publicationProbe.Completed;
            agent.State.Sessions[firstSessionId].WorkflowLlmCompletionDeliveryStatus.Should()
                .Be(WorkflowLlmCompletionDeliveryStatus.RetryScheduled);
            (await eventStore.GetEventsAsync(agent.Id)).Should().NotContain(stateEvent =>
                stateEvent.EventData.Is(WorkflowLlmCompletionDeliveryDispatchedEvent.Descriptor));

            publisher.BeforePublishAsync = null;
            await agent.HandleEventAsync(retryEnvelope);
            await agent.HandleEventAsync(retryEnvelope);

            agent.State.Sessions[firstSessionId].WorkflowLlmCompletionDeliveryStatus.Should()
                .Be(WorkflowLlmCompletionDeliveryStatus.Dispatched);
            publisher.PublicationsWithOptions
                .Where(static publication =>
                    publication.Event is WorkflowLlmInvocationCompletedEvent completed &&
                    completed.SessionId == firstSessionId)
                .Should().ContainSingle()
                .Which.Options!.Delivery!.OperationId.Should().Be(
                    "workflow-llm-terminal:run-completion-publisher-deadline:" +
                    "step-completion-publisher-deadline:" + firstSessionId + ":outcome:1");

            await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-completion-publisher-next-turn",
                StepId = "step-completion-publisher-next-turn",
                SessionId = nextSessionId,
                Prompt = "process the next inbox turn",
            });

            provider.Requests.Should().HaveCount(2);
            agent.State.Sessions[firstSessionId].FinalContent.Should().Be("workflow answer");
            agent.State.Sessions[nextSessionId].Completed.Should().BeTrue();
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed => completed.SessionId == nextSessionId);
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenCompletionPending_ShouldRedeliverOnActivationAndFenceStaleCallback()
        {
            const string actorId = "workflow-role-agent-completion-activation-retry";
            const string sessionId = "workflow-completion-activation-retry";
            var eventStore = new InMemoryEventStore();
            var recoverySecretVault = new InMemorySecretVault();
            var initialScheduler = new RecordingWorkflowCompletionCallbackScheduler();
            var (initial, initialPublisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new RecordingWorkflowIntentLlmProvider(),
                actorId,
                callbackScheduler: initialScheduler,
                chatToolRecoverySecretVault: recoverySecretVault);
            initialPublisher.BeforePublishAsync = (evt, _) => evt is WorkflowLlmInvocationCompletedEvent
                ? Task.FromException(new InvalidOperationException("simulated workflow completion failure"))
                : Task.CompletedTask;

            await initial.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-activation-retry",
                StepId = "step-activation-retry",
                SessionId = sessionId,
                Prompt = "commit then recover on activation",
            });

            initial.State.Sessions[sessionId].WorkflowLlmCompletionDeliveryStatus.Should()
                .Be(WorkflowLlmCompletionDeliveryStatus.RetryScheduled);
            var staleRetryEnvelope = initialScheduler.TimeoutRequests.Should()
                .ContainSingle().Subject.TriggerEnvelope;

            var recoveredScheduler = new RecordingWorkflowCompletionCallbackScheduler();
            var (recovered, recoveredPublisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new RecordingWorkflowIntentLlmProvider(),
                actorId,
                callbackScheduler: recoveredScheduler,
                chatToolRecoverySecretVault: recoverySecretVault);

            recovered.State.Sessions[sessionId].WorkflowLlmCompletionDeliveryStatus.Should()
                .Be(WorkflowLlmCompletionDeliveryStatus.Dispatched);
            recovered.State.Sessions[sessionId].WorkflowLlmCompletionDeliveryAttempt.Should().Be(1);
            recoveredPublisher.PublicationsWithOptions
                .Where(static publication =>
                    publication.Event is WorkflowLlmInvocationCompletedEvent completed &&
                    completed.SessionId == sessionId)
                .Should().ContainSingle()
                .Which.Options!.Delivery!.OperationId.Should().Be(
                    "workflow-llm-terminal:run-activation-retry:step-activation-retry:" +
                    sessionId + ":outcome:1");

            await recovered.HandleEventAsync(staleRetryEnvelope);

            recoveredPublisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed => completed.SessionId == sessionId);
            recoveredScheduler.TimeoutRequests.Should().BeEmpty();
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenApprovalTimeoutCancellationBlocks_ShouldApplyHostDeadline()
        {
            const int timeoutMs = 1_000;
            const string continuationTurnId = "approval-callback-cancel-deadline";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var callbackScheduler = new BlockingCancelRuntimeCallbackScheduler();
            var tool = new ApprovalRequiredWorkflowTool();
            var registry = new FixedToolSetRegistry("studio.write", new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ApprovalWorkflowIntentLlmProvider(tool.Name),
                "workflow-role-agent-approval-callback-cancel-deadline",
                toolSetRegistry: registry,
                callerAccessTokenProvider: new RotatingWorkflowCallerAccessTokenProvider(),
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs),
                callbackScheduler: callbackScheduler);
            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));

            var decision = agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = continuationTurnId,
            });
            await callbackScheduler.CancelStarted;

            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await callbackScheduler.CancelCancellationObserved;
            await decision;

            agent.State.PendingApproval.Should().BeNull();
            publisher.Published.Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>().Should().BeEmpty();
            var completion = (await eventStore.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == continuationTurnId).Which;
            completion.FailureCode.Should().Be("APPROVAL_TOOL_TIMEOUT");
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenApprovalFails_ShouldCommitActorTerminalBeforeWorkflowCompletion()
        {
            var operationLog = new List<string>();
            var eventStore = new RecordingTerminalOrderEventStore(operationLog);
            var tool = new ApprovalRequiredWorkflowTool();
            var registry = new FixedToolSetRegistry("studio.write", new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ApprovalWorkflowIntentLlmProvider(tool.Name),
                "workflow-role-agent-approval-terminal-order",
                toolSetRegistry: registry);
            publisher.BeforePublishAsync = (evt, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (evt is WorkflowLlmInvocationCompletedEvent)
                    operationLog.Add("workflow-completion-published");
                return Task.CompletedTask;
            };
            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));

            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = false,
                Reason = "not allowed",
                ContinuationTurnId = "approval-terminal-order",
            });

            operationLog.Should().ContainInOrder(
                "actor-terminal-committed",
                "workflow-completion-published");
            operationLog.IndexOf("actor-terminal-committed")
                .Should().BeLessThan(operationLog.IndexOf("workflow-completion-published"));
            var terminalBatch = eventStore.Batches.Should().ContainSingle(batch =>
                    batch.Any(stateEvent =>
                        stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
                        stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>().SessionId ==
                        "approval-terminal-order"))
                .Which;
            terminalBatch.Select(stateEvent => stateEvent.EventData.TypeUrl).Should().Equal(
                Any.Pack(new RoleChatSessionCompletedEvent()).TypeUrl,
                Any.Pack(new ClearPendingApprovalEvent()).TypeUrl);
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenApprovalTerminalHookHangs_ShouldClearPendingBeforeBoundedHook()
        {
            const int timeoutMs = 1_000;
            const string continuationTurnId = "approval-terminal-hook-deadline";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var hookProbe = new ApprovalResumeDeadlineProbe();
            var tool = new ApprovalRequiredWorkflowTool();
            var registry = new FixedToolSetRegistry("studio.write", new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ApprovalWorkflowIntentLlmProvider(tool.Name),
                "workflow-role-agent-terminal-hook-deadline",
                toolSetRegistry: registry,
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(
                    maxTurnDeadlineMs: 5_000,
                    postTurnProcessingTimeoutMs: timeoutMs));
            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            publisher.BeforePublishAsync = (evt, ct) => evt is WorkflowLlmInvocationCompletedEvent
                ? hookProbe.HangAsync(ct)
                : Task.CompletedTask;

            var decision = agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = false,
                Reason = "not allowed",
                ContinuationTurnId = continuationTurnId,
            });
            await hookProbe.Started;

            agent.State.PendingApproval.Should().BeNull();
            agent.State.Sessions[continuationTurnId].FailureCode.Should().Be("APPROVAL_DENIED");
            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await hookProbe.CancellationObserved;
            await decision;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WorkflowRoleGAgent_WhenInitialStreamEndsAfterHostDeadline_ShouldRejectLateProviderOutcome(
            bool yieldLateChunk)
        {
            const int timeoutMs = 1_000;
            const string sessionId = "session-initial-deadline";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var llmProvider = new LateAfterCancellationWorkflowIntentLlmProvider(yieldLateChunk);
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llmProvider,
                $"workflow-role-agent-initial-deadline-{yieldLateChunk}",
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));

            var execution = agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-initial-deadline",
                StepId = "step-initial-deadline",
                SessionId = sessionId,
                Prompt = "ignore the deadline",
            });
            await llmProvider.StreamStarted;

            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await llmProvider.CancellationObserved;
            llmProvider.ReleaseAfterCancellation();
            await execution;

            var completion = (await eventStore.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == sessionId).Which;
            completion.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            completion.FailureCode.Should().Be("LLM_TIMEOUT");
            completion.Content.Should().NotContain(LateAfterCancellationWorkflowIntentLlmProvider.LateContent);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmStreamChunkEvent>()
                .Should().NotContain(chunk => chunk.DeltaContent.Contains(
                    LateAfterCancellationWorkflowIntentLlmProvider.LateContent,
                    StringComparison.Ordinal));
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.Error.Contains("llm_timeout", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenSuccessCompletionCommitWaitsPastDeadline_ShouldPublishOnlyTimeout()
        {
            const int timeoutMs = 1_000;
            const string sessionId = "workflow-post-stream-deadline";
            var eventStore = new BlockingWorkflowSuccessCompletionEventStore();
            var timeProvider = new FakeTimeProvider();
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new RecordingWorkflowIntentLlmProvider(),
                "workflow-role-agent-post-stream-deadline",
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));

            var execution = agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-post-stream",
                StepId = "step-post-stream",
                SessionId = sessionId,
                Prompt = "finish then wait on persistence",
            });
            await eventStore.SuccessCompletionAppendStarted;
            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await eventStore.CancellationObserved;
            await execution;

            var completion = (await eventStore.Inner.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == sessionId).Which;
            completion.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            completion.FailureCode.Should().Be("LLM_TIMEOUT");
            completion.Content.Should().NotContain("workflow answer");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.SessionId == sessionId &&
                    completed.Error.Contains("llm_timeout", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenSuccessCommitResultReturnsAfterDeadline_ShouldKeepCommittedSuccess()
        {
            const int timeoutMs = 1_000;
            const string sessionId = "workflow-committed-before-deadline-result";
            var eventStore = new LateReturningCommittedWorkflowSuccessEventStore();
            var timeProvider = new FakeTimeProvider();
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new RecordingWorkflowIntentLlmProvider(),
                "workflow-role-agent-committed-before-deadline-result",
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));

            var execution = agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
            {
                RunId = "run-committed",
                StepId = "step-committed",
                SessionId = sessionId,
                Prompt = "commit before the deadline result returns",
            });
            await eventStore.SuccessCommitCompleted;
            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await eventStore.DeadlineObserved;
            await execution;

            var completion = (await eventStore.Inner.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == sessionId).Which;
            completion.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
            completion.FailureCode.Should().BeEmpty();
            completion.Content.Should().Be("workflow answer");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    completed.Success &&
                    completed.SessionId == sessionId &&
                    completed.Content == "workflow answer");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WorkflowRoleGAgent_WhenApprovalContinuationStreamEndsAfterHostDeadline_ShouldRejectLateProviderOutcome(
            bool yieldLateChunk)
        {
            const int timeoutMs = 1_000;
            const string continuationTurnId = "approval-stream-deadline";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var tool = new ApprovalRequiredWorkflowTool();
            var llmProvider = new ApprovalThenLateWorkflowIntentLlmProvider(tool.Name, yieldLateChunk);
            var registry = new FixedToolSetRegistry("studio.write", new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llmProvider,
                $"workflow-role-agent-continuation-deadline-{yieldLateChunk}",
                toolSetRegistry: registry,
                callerAccessTokenProvider: new RotatingWorkflowCallerAccessTokenProvider(),
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = continuationTurnId,
            });
            var continuation = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().ContainSingle().Which;

            var execution = agent.HandleChatRecoveryContinuationRequestedAsync(continuation);
            await llmProvider.ContinuationStreamStarted;
            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await llmProvider.CancellationObserved;
            llmProvider.ReleaseAfterCancellation();
            await execution;

            var sourceReconciliations = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Where(item => item.SessionId == "session-approval")
                .ToArray();
            sourceReconciliations.Should().HaveCount(2);
            await agent.HandleChatRecoveryContinuationRequestedAsync(sourceReconciliations[^1]);

            var completion = (await eventStore.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == continuationTurnId).Which;
            completion.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            completion.FailureCode.Should().Be("APPROVAL_TOOL_TIMEOUT");
            completion.Content.Should().NotContain(LateAfterCancellationWorkflowIntentLlmProvider.LateContent);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmStreamChunkEvent>()
                .Should().NotContain(chunk => chunk.DeltaContent.Contains(
                    LateAfterCancellationWorkflowIntentLlmProvider.LateContent,
                    StringComparison.Ordinal));
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.RunId == "run-approval" &&
                    completed.SessionId == "session-approval" &&
                    completed.Error.Contains("approval_tool_timeout", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("token_refresh")]
        [InlineData("catalog_discovery")]
        [InlineData("tool_execution")]
        public async Task WorkflowRoleGAgent_WhenApprovalResumePhaseExceedsHostDeadline_ShouldCommitTypedTimeout(
            string hangingPhase)
        {
            const int timeoutMs = 1_000;
            const string continuationTurnId = "approval-tool-deadline";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var deadlineProbe = new ApprovalResumeDeadlineProbe();
            var tool = new ApprovalResumeDeadlineTool(
                deadlineProbe,
                hang: hangingPhase == "tool_execution");
            var source = new ApprovalResumeDeadlineToolSource(
                tool,
                deadlineProbe,
                hangOnApprovalResume: hangingPhase == "catalog_discovery");
            var tokenProvider = new ApprovalResumeDeadlineTokenProvider(
                deadlineProbe,
                hang: hangingPhase == "token_refresh");
            var registry = new FixedToolSetRegistry("studio.write", source);
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ApprovalWorkflowIntentLlmProvider(tool.Name),
                $"workflow-role-agent-approval-phase-deadline-{hangingPhase}",
                toolSetRegistry: registry,
                callerAccessTokenProvider: tokenProvider,
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            var decision = agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = continuationTurnId,
            });
            await deadlineProbe.Started;

            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await deadlineProbe.CancellationObserved;
            await decision;

            agent.State.PendingApproval.Should().BeNull();
            publisher.Published.Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>().Should().BeEmpty();
            var completion = (await eventStore.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == continuationTurnId).Which;
            completion.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            completion.FailureCode.Should().Be("APPROVAL_TOOL_TIMEOUT");
            completion.SafeMessage.Should().Be(
                "The approval continuation exceeded its deadline. Please try again.");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.RunId == "run-approval" &&
                    completed.SessionId == "session-approval" &&
                    completed.Error.Contains("approval_tool_timeout", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("token_refresh", false)]
        [InlineData("token_refresh", true)]
        [InlineData("catalog_discovery", false)]
        [InlineData("catalog_discovery", true)]
        [InlineData("tool_execution", false)]
        [InlineData("tool_execution", true)]
        public async Task WorkflowRoleGAgent_WhenApprovalResumeReturnsAfterDeadline_ShouldKeepTimeoutAuthority(
            string latePhase,
            bool throwLateFailure)
        {
            const int timeoutMs = 1_000;
            const string continuationTurnId = "approval-late-return-deadline";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var deadlineProbe = new LateApprovalResumeProbe(throwLateFailure);
            var tool = new LateApprovalResumeTool(
                deadlineProbe,
                late: latePhase == "tool_execution");
            var source = new LateApprovalResumeToolSource(
                tool,
                deadlineProbe,
                lateOnApprovalResume: latePhase == "catalog_discovery");
            var tokenProvider = new LateApprovalResumeTokenProvider(
                deadlineProbe,
                late: latePhase == "token_refresh");
            var registry = new FixedToolSetRegistry("studio.write", source);
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ApprovalWorkflowIntentLlmProvider(tool.Name),
                $"workflow-role-agent-approval-late-{latePhase}-{throwLateFailure}",
                toolSetRegistry: registry,
                callerAccessTokenProvider: tokenProvider,
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            var decision = agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = continuationTurnId,
            });
            await deadlineProbe.Started;

            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await deadlineProbe.CancellationObserved;
            deadlineProbe.ReleaseAfterDeadline();
            await decision;

            agent.State.PendingApproval.Should().BeNull();
            publisher.Published.Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>().Should().BeEmpty();
            var completion = (await eventStore.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == continuationTurnId).Which;
            completion.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            completion.FailureCode.Should().Be("APPROVAL_TOOL_TIMEOUT");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.Error.Contains("approval_tool_timeout", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenClearPendingReturnsAfterDeadline_ShouldCommitOneTimeoutWithoutContinuation()
        {
            const int timeoutMs = 1_000;
            const string continuationTurnId = "approval-clear-pending-deadline";
            var eventStore = new LateReturningClearPendingEventStore();
            var timeProvider = new FakeTimeProvider();
            var tool = new ApprovalRequiredWorkflowTool();
            var registry = new FixedToolSetRegistry("studio.write", new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ApprovalWorkflowIntentLlmProvider(tool.Name),
                "workflow-role-agent-clear-pending-deadline",
                toolSetRegistry: registry,
                callerAccessTokenProvider: new RotatingWorkflowCallerAccessTokenProvider(),
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            var decision = agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = continuationTurnId,
            });
            await eventStore.ClearPendingCommitted;

            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            eventStore.ReleaseLateReturn();
            await decision;

            agent.State.PendingApproval.Should().BeNull();
            publisher.Published.Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>().Should().BeEmpty();
            var persisted = await eventStore.Inner.GetEventsAsync(agent.Id);
            persisted.Count(stateEvent =>
                    stateEvent.EventData.Is(ClearPendingApprovalEvent.Descriptor))
                .Should().Be(1);
            var completion = persisted
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == continuationTurnId).Which;
            completion.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            completion.FailureCode.Should().Be("APPROVAL_TOOL_TIMEOUT");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.Error.Contains("approval_tool_timeout", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenAdmittedContinuationSendReturnsAfterDeadline_ShouldFenceQueuedContinuation()
        {
            const int timeoutMs = 1_000;
            const string continuationTurnId = "approval-admitted-send-deadline";
            var eventStore = new InMemoryEventStore();
            var timeProvider = new FakeTimeProvider();
            var sendProbe = new LateApprovalResumeProbe(throwLateFailure: false);
            var tool = new ApprovalRequiredWorkflowTool();
            var llmProvider = new ApprovalWorkflowIntentLlmProvider(tool.Name);
            var tokenProvider = new RotatingWorkflowCallerAccessTokenProvider();
            var registry = new FixedToolSetRegistry("studio.write", new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llmProvider,
                "workflow-role-agent-admitted-send-deadline",
                toolSetRegistry: registry,
                callerAccessTokenProvider: tokenProvider,
                timeProvider: timeProvider,
                chatExecutionOptions: new RoleChatExecutionOptions(timeoutMs));
            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            RoleChatRecoveryContinuationRequested? queuedContinuation = null;
            publisher.BeforePublishAsync = async (evt, ct) =>
            {
                if (evt is not RoleChatRecoveryContinuationRequested continuation)
                    return;

                queuedContinuation = continuation;
                await sendProbe.CompleteAfterDeadlineAsync(true, ct);
            };

            var decision = agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = continuationTurnId,
            });
            await sendProbe.Started;

            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await sendProbe.CancellationObserved;
            sendProbe.ReleaseAfterDeadline();
            await decision;

            queuedContinuation.Should().NotBeNull();
            var providerCallsAfterTimeout = llmProvider.CallCount;
            var tokenRefreshesAfterTimeout = tokenProvider.Authorities.Count;
            var toolExecutionsAfterTimeout = tool.ExecuteCount;

            await agent.HandleChatRecoveryContinuationRequestedAsync(queuedContinuation!);

            llmProvider.CallCount.Should().Be(providerCallsAfterTimeout);
            tokenProvider.Authorities.Should().HaveCount(tokenRefreshesAfterTimeout);
            tool.ExecuteCount.Should().Be(toolExecutionsAfterTimeout);
            agent.State.PendingApproval.Should().BeNull();
            var completion = (await eventStore.GetEventsAsync(agent.Id))
                .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
                .Should().ContainSingle(completed => completed.SessionId == continuationTurnId).Which;
            completion.FailureCode.Should().Be("APPROVAL_TOOL_TIMEOUT");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Where(completed => !completed.Success)
                .Should().ContainSingle(completed =>
                    completed.Error.Contains("approval_tool_timeout", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenRequestLocalWriteToolNeedsApproval_ShouldResumeOriginalStepAfterApproval()
        {
            var eventStore = new InMemoryEventStore();
            var tool = new ApprovalRequiredWorkflowTool();
            var handler = new RecordingNyxIdHandler();
            var requireServiceTool = new NyxIdRequireServiceTool(new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
                new HttpClient(handler)));
            var tokenProvider = new RotatingWorkflowCallerAccessTokenProvider();
            var registry = new FixedToolSetRegistry(
                "studio.write",
                new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ApprovalWorkflowIntentLlmProvider(tool.Name),
                "workflow-role-agent-approval",
                toolSetRegistry: registry,
                callerAccessTokenProvider: tokenProvider);

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));

            agent.State.PendingApproval.Should().NotBeNull();
            var pendingContext = agent.State.PendingApproval.ToolContext;
            pendingContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
            pendingContext.Credentials.NyxIdOrgToken.Should().BeEmpty();
            pendingContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
            pendingContext.NyxIdAuthority.Should().BeEquivalentTo(new AgentToolNyxIdAuthorityContextPayload
            {
                Platform = "lark",
                Tenant = "tenant-alpha",
                ExternalUserId = "user-alpha",
                Scope = "scope-alpha",
            });
            agent.State.PendingApproval.ToString().Should().NotContain("original-bearer");
            tool.ExecuteCount.Should().Be(0);
            publisher.Published.Select(static item => item.evt)
                .OfType<ToolApprovalRequestEvent>().Should().ContainSingle();
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>().Should().BeEmpty();

            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = "approval-continuation",
            });

            var continuation = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().ContainSingle().Which;
            await agent.HandleChatRecoveryContinuationRequestedAsync(continuation);
            var sourceReconciliations = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Where(item => item.SessionId == "session-approval")
                .ToArray();
            sourceReconciliations.Should().HaveCount(2);
            var sourceReconciliation = sourceReconciliations[^1];
            await agent.HandleChatRecoveryContinuationRequestedAsync(sourceReconciliation);

            tool.ExecuteCount.Should().Be(1);
            tool.AccessTokens.Should().Equal("fresh-token-1");
            tool.ExecutionContext.Should().NotBeNull();
            tool.ExecutionContext!.Credentials.NyxIdAccessToken.Should().NotBeNullOrWhiteSpace();
            tool.ExecutionContext.Credentials.NyxIdCredentialKind.Should()
                .Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
            tool.ExecutionContext.Credentials.NyxIdOrgToken.Should().BeNull();
            tool.ExecutionContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
            using (AgentToolContextScope.Push(tool.ExecutionContext))
            {
                var result = await requireServiceTool.ExecuteAsync(
                    """{"service_slug":"api-github"}""");
                result.Should().Contain("NYXID_SOURCE_UNAVAILABLE");
            }
            handler.Requests.Should().Be(0);
            tokenProvider.Authorities.Should().HaveCount(2);
            tokenProvider.Authorities.Should().OnlyContain(authority =>
                authority.Platform == "lark" &&
                authority.Tenant == "tenant-alpha" &&
                authority.ExternalUserId == "user-alpha" &&
                authority.Scope == "scope-alpha" &&
                authority.BindingId == "binding-alpha");
            agent.State.PendingApproval.Should().BeNull();
            var workflowCompletion = publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    completed.Success &&
                    completed.RunId == "run-approval" &&
                    completed.StepId == "step-approval" &&
                    completed.SessionId == "session-approval" &&
                    completed.Content == "approved completion").Which;
            workflowCompletion.Usage.Should().NotBeNull();
            workflowCompletion.Usage!.Model.Should().Be("workflow-approval-model");
            var continuationTerminal = agent.State.Sessions["approval-continuation"];
            var sourceTerminal = agent.State.Sessions["session-approval"];
            sourceTerminal.Completed.Should().BeTrue();
            sourceTerminal.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
            sourceTerminal.ToolCalls.Should().ContainSingle(call =>
                call.CallId == "call-approval" && call.ToolName == tool.Name);
            sourceTerminal.ToolResults.Should().ContainSingle(result =>
                result.CallId == "call-approval" && result.Success);
            sourceTerminal.ToolReceipts.Should().ContainSingle(receipt =>
                receipt.CallId == "call-approval" && receipt.Status == AgentToolReceiptStatus.Success);
            sourceTerminal.Usage.Should().BeEquivalentTo(continuationTerminal.Usage);
            sourceTerminal.Model.Should().Be(continuationTerminal.Model);
            sourceTerminal.Model.Should().Be("workflow-approval-model");
            sourceTerminal.FailureCode.Should().BeEmpty();
            sourceTerminal.AuthorizationRequired.Should().BeNull();
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenOnlyTargetRecoveryRuns_ShouldTerminalizeTargetAndSourceOnce()
        {
            const string continuationSessionId = "approval-target-recovery";
            var eventStore = new FailCompletionCheckpointEventStore(failureOrdinal: 2);
            var approvalTool = new ApprovalRequiredWorkflowTool();
            var continuationTool = new SuccessfulWorkflowTool("lookup_after_approval");
            var llm = new ApprovalThenToolWorkflowIntentLlmProvider(
                approvalTool.Name,
                continuationTool.Name);
            var registry = new FixedToolSetRegistry(
                "studio.write",
                new FixedToolSource(approvalTool, continuationTool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-target-only-recovery",
                toolSetRegistry: registry,
                callerAccessTokenProvider: new RotatingWorkflowCallerAccessTokenProvider());
            var intent = ApprovalIntent(approvalTool.Name);
            intent.AgentToolScope.AllowedToolNames.Add(continuationTool.Name);

            await agent.HandleWorkflowLlmExecutionIntent(intent);
            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = continuationSessionId,
            });
            var sourceRecovery = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().ContainSingle().Which;

            await agent.HandleChatRecoveryContinuationRequestedAsync(sourceRecovery);

            approvalTool.ExecuteCount.Should().Be(1);
            continuationTool.ExecuteCount.Should().Be(1);
            llm.CallCount.Should().Be(2);
            agent.State.Sessions["session-approval"].RecoveryCheckpoint!.Stage.Should()
                .Be(RoleChatRecoveryCheckpointStage.ContinuationPrepared);
            agent.State.Sessions[continuationSessionId].RecoveryCheckpoint!.Stage.Should()
                .Be(RoleChatRecoveryCheckpointStage.ToolBatchPrepared);
            agent.State.Sessions["session-approval"].Completed.Should().BeFalse();
            agent.State.Sessions[continuationSessionId].Completed.Should().BeFalse();
            var targetRecovery = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().HaveCount(2).And.ContainSingle(item =>
                    item.SessionId == continuationSessionId).Which;

            await agent.HandleChatRecoveryContinuationRequestedAsync(targetRecovery);
            var sourceReconciliations = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Where(item => item.SessionId == "session-approval")
                .ToArray();
            sourceReconciliations.Should().HaveCount(2);
            var sourceReconciliation = sourceReconciliations[^1];
            await agent.HandleChatRecoveryContinuationRequestedAsync(sourceReconciliation);

            approvalTool.ExecuteCount.Should().Be(1,
                "source recovery must adopt the approved result");
            continuationTool.ExecuteCount.Should().Be(1,
                "target recovery must adopt the sealed external result");
            llm.CallCount.Should().Be(3);
            var targetTerminal = agent.State.Sessions[continuationSessionId];
            targetTerminal.Completed.Should().BeTrue();
            targetTerminal.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
            targetTerminal.FinalContent.Should().Be("recovered approval completion");
            var sourceTerminal = agent.State.Sessions["session-approval"];
            sourceTerminal.Completed.Should().BeTrue();
            sourceTerminal.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
            sourceTerminal.ToolResults.Should().HaveCount(2);
            sourceTerminal.ToolResults.Should().OnlyContain(static result => result.Success);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    completed.Success && completed.SessionId == "session-approval");
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenApprovalRecoveryFails_ShouldTerminalizeSourceWithoutRerun()
        {
            var eventStore = new InMemoryEventStore();
            var tool = new ApprovalRequiredWorkflowTool();
            var llm = new ApprovalThenThrowWorkflowIntentLlmProvider(tool.Name);
            var registry = new FixedToolSetRegistry(
                "studio.write",
                new FixedToolSource(tool));
            var tokenProvider = new RotatingWorkflowCallerAccessTokenProvider();
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-approval-recovery-failure",
                toolSetRegistry: registry,
                callerAccessTokenProvider: tokenProvider);

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            llm.CallCount.Should().Be(1);
            agent.State.Sessions["session-approval"].Completed.Should().BeFalse();
            agent.State.PendingApproval.Should().NotBeNull();
            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = "approval-failure-continuation",
            });
            var recovery = publisher.Published.Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().ContainSingle(
                    "published={0}; pending={1}; sessions={2}",
                    string.Join(',', publisher.Published.Select(static item => item.evt.GetType().Name)),
                    agent.State.PendingApproval?.RequestId ?? "<null>",
                    string.Join(',', agent.State.Sessions.Select(static pair =>
                        $"{pair.Key}:{pair.Value.Outcome}:{pair.Value.FailureCode}"))).Which;

            await agent.HandleChatRecoveryContinuationRequestedAsync(recovery);
            var sourceReconciliations = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Where(item => item.SessionId == "session-approval")
                .ToArray();
            sourceReconciliations.Should().HaveCount(2);
            var sourceReconciliation = sourceReconciliations[^1];
            await agent.HandleChatRecoveryContinuationRequestedAsync(sourceReconciliation);

            llm.CallCount.Should().Be(2);
            tool.ExecuteCount.Should().Be(1);
            agent.State.PendingApproval.Should().BeNull();
            var continuationTerminal = agent.State.Sessions["approval-failure-continuation"];
            continuationTerminal.Completed.Should().BeTrue();
            continuationTerminal.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            continuationTerminal.FailureCode.Should().Be("APPROVAL_CONTINUATION_FAILED");
            var sourceTerminal = agent.State.Sessions["session-approval"];
            sourceTerminal.Completed.Should().BeTrue();
            sourceTerminal.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
            sourceTerminal.FailureCode.Should().Be("APPROVAL_CONTINUATION_FAILED");
            sourceTerminal.ToolCalls.Should().ContainSingle(call =>
                call.CallId == "call-approval-failure");
            sourceTerminal.ToolResults.Should().ContainSingle(result =>
                result.CallId == "call-approval-failure" && result.Success);
            sourceTerminal.ToolReceipts.Should().ContainSingle(receipt =>
                receipt.CallId == "call-approval-failure" &&
                receipt.Status == AgentToolReceiptStatus.Success);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.Error.StartsWith(
                        "approval_continuation_failed:",
                        StringComparison.Ordinal));

            await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-approval-recovery-failure",
                toolSetRegistry: registry,
                callerAccessTokenProvider: tokenProvider);

            llm.CallCount.Should().Be(2,
                "committed continuation and source terminals must fence activation replay");
            tool.ExecuteCount.Should().Be(1);
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenApprovalIsDenied_ShouldFailOriginalWorkflowInvocation()
        {
            var eventStore = new InMemoryEventStore();
            var tool = new ApprovalRequiredWorkflowTool();
            var registry = new FixedToolSetRegistry(
                "studio.write",
                new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new ApprovalWorkflowIntentLlmProvider(tool.Name),
                "workflow-role-agent-denial",
                toolSetRegistry: registry);

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = false,
                Reason = "not allowed",
                ContinuationTurnId = "approval-denial",
            });

            tool.ExecuteCount.Should().Be(0);
            agent.State.PendingApproval.Should().BeNull();
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    !completed.Success &&
                    completed.RunId == "run-approval" &&
                    completed.StepId == "step-approval" &&
                    completed.SessionId == "session-approval" &&
                    completed.Error.Contains("approval_denied", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenContinuationNeedsAnotherApproval_ShouldReconcileEveryDirectParent()
        {
            const string firstContinuationSessionId = "approval-continuation-1";
            const string secondContinuationSessionId = "approval-continuation-2";
            var eventStore = new InMemoryEventStore();
            var tool = new ApprovalRequiredWorkflowTool();
            var tokenProvider = new RotatingWorkflowCallerAccessTokenProvider();
            var registry = new FixedToolSetRegistry(
                "studio.write",
                new FixedToolSource(tool));
            var llm = new TwoApprovalWorkflowIntentLlmProvider(tool.Name);
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-two-approvals",
                toolSetRegistry: registry,
                callerAccessTokenProvider: tokenProvider);

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            llm.CallCount.Should().Be(1);
            agent.State.Sessions["session-approval"].Completed.Should().BeFalse();
            agent.State.PendingApproval.Should().NotBeNull();
            var firstRequestId = agent.State.PendingApproval.RequestId;
            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = firstRequestId,
                Approved = true,
                ContinuationTurnId = firstContinuationSessionId,
            });
            var continuation = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().ContainSingle().Which;

            await agent.HandleChatRecoveryContinuationRequestedAsync(continuation);

            tool.ExecuteCount.Should().Be(1);
            agent.State.PendingApproval.Should().NotBeNull();
            agent.State.PendingApproval.RequestId.Should().NotBe(firstRequestId);
            publisher.Published.Select(static item => item.evt)
                .OfType<ToolApprovalRequestEvent>().Should().HaveCount(2);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>().Should().BeEmpty();

            var secondRequestId = agent.State.PendingApproval.RequestId;
            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = secondRequestId,
                Approved = true,
                ContinuationTurnId = secondContinuationSessionId,
            });
            var secondContinuationRecovery = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Last(item => item.SessionId == firstContinuationSessionId);

            await agent.HandleChatRecoveryContinuationRequestedAsync(secondContinuationRecovery);

            agent.State.Sessions[secondContinuationSessionId].Completed.Should().BeTrue();
            agent.State.Sessions[firstContinuationSessionId].Completed.Should().BeFalse();
            agent.State.Sessions["session-approval"].Completed.Should().BeFalse();
            var firstParentReconciliations = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Where(item => item.SessionId == firstContinuationSessionId)
                .ToArray();
            firstParentReconciliations.Should().HaveCount(2);
            var firstParentReconciliation = firstParentReconciliations[^1];

            await agent.HandleChatRecoveryContinuationRequestedAsync(firstParentReconciliation);

            agent.State.Sessions[firstContinuationSessionId].Completed.Should().BeTrue();
            agent.State.Sessions["session-approval"].Completed.Should().BeFalse();
            var originalParentReconciliations = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Where(item => item.SessionId == "session-approval")
                .ToArray();
            originalParentReconciliations.Should().HaveCount(2);
            var originalParentReconciliation = originalParentReconciliations[^1];

            await agent.HandleChatRecoveryContinuationRequestedAsync(originalParentReconciliation);

            tool.ExecuteCount.Should().Be(2);
            llm.CallCount.Should().Be(3);
            agent.State.PendingApproval.Should().BeNull();
            var firstContinuation = agent.State.Sessions[firstContinuationSessionId];
            firstContinuation.DirectParentRoleChatSessionId.Should().Be("session-approval");
            agent.State.Sessions[secondContinuationSessionId]
                .DirectParentRoleChatSessionId.Should().Be(firstContinuationSessionId);
            var original = agent.State.Sessions["session-approval"];
            original.Completed.Should().BeTrue();
            original.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
            original.ToolCalls.Should().HaveCount(2);
            original.ToolResults.Should().HaveCount(2);
            original.ToolResults.Should().OnlyContain(static result => result.Success);
            original.ToolReceipts.Should().HaveCount(2);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    completed.Success && completed.SessionId == "session-approval");

            await agent.HandleChatRecoveryContinuationRequestedAsync(firstParentReconciliation);
            await agent.HandleChatRecoveryContinuationRequestedAsync(originalParentReconciliation);

            tool.ExecuteCount.Should().Be(2, "terminal checkpoints must fence repeated recovery");
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>().Should().ContainSingle();
        }

        [Fact]
        public async Task WorkflowRoleGAgent_WhenOnlySecondLevelTargetRecovers_ShouldReconcileParentsInOrder()
        {
            const string firstContinuationSessionId = "approval-target-level-1";
            const string secondContinuationSessionId = "approval-target-level-2";
            var eventStore = new FailCompletionCheckpointEventStore(failureOrdinal: 3);
            var approvalTool = new ApprovalRequiredWorkflowTool();
            var continuationTool = new SuccessfulWorkflowTool("lookup_after_two_approvals");
            var llm = new TwoApprovalsThenToolWorkflowIntentLlmProvider(
                approvalTool.Name,
                continuationTool.Name);
            var registry = new FixedToolSetRegistry(
                "studio.write",
                new FixedToolSource(approvalTool, continuationTool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                llm,
                "workflow-role-agent-second-level-target-recovery",
                toolSetRegistry: registry,
                callerAccessTokenProvider: new RotatingWorkflowCallerAccessTokenProvider());
            var intent = ApprovalIntent(approvalTool.Name);
            intent.AgentToolScope.AllowedToolNames.Add(continuationTool.Name);

            await agent.HandleWorkflowLlmExecutionIntent(intent);
            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = firstContinuationSessionId,
            });
            var originalRecovery = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Single(item => item.SessionId == "session-approval");
            await agent.HandleChatRecoveryContinuationRequestedAsync(originalRecovery);

            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = agent.State.PendingApproval.RequestId,
                Approved = true,
                ContinuationTurnId = secondContinuationSessionId,
            });
            var firstTargetRecovery = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Single(item => item.SessionId == firstContinuationSessionId);
            await agent.HandleChatRecoveryContinuationRequestedAsync(firstTargetRecovery);

            approvalTool.ExecuteCount.Should().Be(2);
            continuationTool.ExecuteCount.Should().Be(1);
            llm.CallCount.Should().Be(3);
            agent.State.Sessions[secondContinuationSessionId].Completed.Should().BeFalse();
            agent.State.Sessions[firstContinuationSessionId].Completed.Should().BeFalse();
            agent.State.Sessions["session-approval"].Completed.Should().BeFalse();
            var secondTargetRecovery = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Should().ContainSingle(item => item.SessionId == secondContinuationSessionId).Which;

            await agent.HandleChatRecoveryContinuationRequestedAsync(secondTargetRecovery);

            approvalTool.ExecuteCount.Should().Be(2, "approved operations must adopt durable results");
            continuationTool.ExecuteCount.Should().Be(1, "target recovery must adopt its sealed result");
            llm.CallCount.Should().Be(4);
            agent.State.Sessions[secondContinuationSessionId].Completed.Should().BeTrue();
            agent.State.Sessions[firstContinuationSessionId].Completed.Should().BeFalse();
            agent.State.Sessions["session-approval"].Completed.Should().BeFalse();

            var firstParentReconciliations = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Where(item => item.SessionId == firstContinuationSessionId)
                .ToArray();
            firstParentReconciliations.Should().HaveCount(2);
            await agent.HandleChatRecoveryContinuationRequestedAsync(firstParentReconciliations[^1]);

            agent.State.Sessions[firstContinuationSessionId].Completed.Should().BeTrue();
            agent.State.Sessions["session-approval"].Completed.Should().BeFalse(
                "the second-level target must reconcile its direct parent before the original source");
            var originalParentReconciliations = publisher.Published
                .Select(static item => item.evt)
                .OfType<RoleChatRecoveryContinuationRequested>()
                .Where(item => item.SessionId == "session-approval")
                .ToArray();
            originalParentReconciliations.Should().HaveCount(2);
            await agent.HandleChatRecoveryContinuationRequestedAsync(originalParentReconciliations[^1]);

            var original = agent.State.Sessions["session-approval"];
            original.Completed.Should().BeTrue();
            original.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
            original.ToolResults.Should().HaveCount(3);
            original.ToolResults.Should().OnlyContain(static result => result.Success);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    completed.Success && completed.SessionId == "session-approval");

            await agent.HandleChatRecoveryContinuationRequestedAsync(secondTargetRecovery);
            await agent.HandleChatRecoveryContinuationRequestedAsync(firstParentReconciliations[^1]);
            approvalTool.ExecuteCount.Should().Be(2);
            continuationTool.ExecuteCount.Should().Be(1);
        }

        private static WorkflowLlmExecutionIntent ApprovalIntent(string toolName) => new()
        {
            RunId = "run-approval",
            StepId = "step-approval",
            SessionId = "session-approval",
            Prompt = "update service",
            Model = "workflow-approval-model",
            ScopeId = "scope-alpha",
            CallerCredential = new WorkflowCallerCredential
            {
                BearerToken = "original-bearer",
                Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
                NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                {
                    Platform = "lark",
                    Tenant = "tenant-alpha",
                    ExternalUserId = "user-alpha",
                    Scope = "scope-alpha",
                    BindingId = "binding-alpha",
                },
            },
            AgentToolScope = new WorkflowAgentToolScope
            {
                RestrictToolSets = true,
                RestrictAllowedToolNames = true,
                ToolSetRefs = { "studio.write" },
                AllowedToolNames = { toolName },
            },
        };

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
            var agent = new WorkflowRoleGAgent(
                UnexpectedAgentToolExecutionPort.Instance,
                new RecordingWorkflowIntentLlmProvider())
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

        private sealed class ApprovalRequiredWorkflowTool : IAgentTool
        {
            public int ExecuteCount { get; private set; }
            public List<string?> AccessTokens { get; } = [];
            public AgentToolExecutionContext? ExecutionContext { get; private set; }
            public string Name => "nyxid_service_update";
            public string Description => "Updates a connected service.";
            public string ParametersSchema => "{}";
            public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

            public AgentToolReceipt CreateSuccessReceipt(
                string callId,
                string toolName,
                string resultJson) => new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };

            public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                ExecuteCount++;
                AccessTokens.Add(AgentToolRequestContext.NyxIdAccessToken);
                ExecutionContext = AgentToolRequestContext.Current;
                return Task.FromResult("""{"updated":true}""");
            }
        }

        private sealed class ApprovalResumeDeadlineProbe
        {
            private readonly TaskCompletionSource _started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancellationObserved =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _neverCompletes =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;
            public Task CancellationObserved => _cancellationObserved.Task;

            public async Task HangAsync(CancellationToken ct)
            {
                _started.TrySetResult();
                try
                {
                    await _neverCompletes.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw;
                }
            }
        }

        private sealed class IgnoringCancellationPostTurnProbe
        {
            private readonly TaskCompletionSource _started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _neverCompletes =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _completed =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;
            public Task Completed => _completed.Task;

            public async Task HangIgnoringCancellationAsync()
            {
                _started.TrySetResult();
                await _neverCompletes.Task;
                _completed.TrySetResult();
            }

            public void Release() => _neverCompletes.TrySetResult();
        }

        private sealed class RecordingWorkflowCompletionCallbackScheduler
            : IActorRuntimeCallbackScheduler
        {
            public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

            public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
                RuntimeCallbackTimeoutRequest request,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                TimeoutRequests.Add(new RuntimeCallbackTimeoutRequest
                {
                    ActorId = request.ActorId,
                    CallbackId = request.CallbackId,
                    TriggerEnvelope = request.TriggerEnvelope.Clone(),
                    DueTime = request.DueTime,
                    DeliveryMode = request.DeliveryMode,
                });
                return Task.FromResult(new RuntimeCallbackLease(
                    request.ActorId,
                    request.CallbackId,
                    TimeoutRequests.Count,
                    RuntimeCallbackBackend.InMemory));
            }

            public Task<RuntimeCallbackLease> ScheduleTimerAsync(
                RuntimeCallbackTimerRequest request,
                CancellationToken ct = default) =>
                throw new NotSupportedException();

            public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
                Task.CompletedTask;
        }

        private sealed class ApprovalResumeDeadlineTool(
            ApprovalResumeDeadlineProbe deadlineProbe,
            bool hang) : IAgentTool
        {
            public string Name => "nyxid_service_update";
            public string Description => "Updates a connected service.";
            public string ParametersSchema => "{}";
            public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

            public async Task<string> ExecuteAsync(
                string argumentsJson,
                CancellationToken ct = default)
            {
                _ = argumentsJson;
                if (hang)
                    await deadlineProbe.HangAsync(ct);
                else
                    ct.ThrowIfCancellationRequested();
                return """{"updated":true}""";
            }
        }

        private sealed class ApprovalResumeDeadlineToolSource(
            IAgentTool tool,
            ApprovalResumeDeadlineProbe deadlineProbe,
            bool hangOnApprovalResume) : IAgentToolSource
        {
            private int _discoveryCount;

            public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
                CancellationToken ct = default)
            {
                var discoveryCount = Interlocked.Increment(ref _discoveryCount);
                if (hangOnApprovalResume && discoveryCount == 2)
                    await deadlineProbe.HangAsync(ct);
                else
                    ct.ThrowIfCancellationRequested();
                return [tool];
            }
        }

        private sealed class ApprovalResumeDeadlineTokenProvider(
            ApprovalResumeDeadlineProbe deadlineProbe,
            bool hang) : IWorkflowCallerAccessTokenProvider
        {
            public async Task<string> IssueAsync(
                WorkflowCallerNyxIdAuthority authority,
                CancellationToken ct = default)
            {
                _ = authority;
                if (hang)
                    await deadlineProbe.HangAsync(ct);
                else
                    ct.ThrowIfCancellationRequested();
                return "fresh-token";
            }
        }

        private sealed class LateApprovalResumeProbe(bool throwLateFailure)
        {
            private readonly TaskCompletionSource _started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancellationObserved =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _neverCompletes =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseAfterDeadline =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;
            public Task CancellationObserved => _cancellationObserved.Task;

            public void ReleaseAfterDeadline() => _releaseAfterDeadline.TrySetResult();

            public async Task<T> CompleteAfterDeadlineAsync<T>(T result, CancellationToken ct)
            {
                _started.TrySetResult();
                try
                {
                    await _neverCompletes.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                }

                await _releaseAfterDeadline.Task;
                if (throwLateFailure)
                    throw new InvalidOperationException("late approval resume failure");
                return result;
            }
        }

        private sealed class LateApprovalResumeTool(
            LateApprovalResumeProbe deadlineProbe,
            bool late) : IAgentTool
        {
            public string Name => "nyxid_service_update";
            public string Description => "Updates a connected service.";
            public string ParametersSchema => "{}";
            public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

            public Task<string> ExecuteAsync(
                string argumentsJson,
                CancellationToken ct = default)
            {
                _ = argumentsJson;
                return late
                    ? deadlineProbe.CompleteAfterDeadlineAsync("""{"updated":true}""", ct)
                    : Task.FromResult("""{"updated":true}""");
            }
        }

        private sealed class LateApprovalResumeToolSource(
            IAgentTool tool,
            LateApprovalResumeProbe deadlineProbe,
            bool lateOnApprovalResume) : IAgentToolSource
        {
            private int _discoveryCount;

            public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
                CancellationToken ct = default)
            {
                var discoveryCount = Interlocked.Increment(ref _discoveryCount);
                return lateOnApprovalResume && discoveryCount == 2
                    ? deadlineProbe.CompleteAfterDeadlineAsync<IReadOnlyList<IAgentTool>>([tool], ct)
                    : Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
            }
        }

        private sealed class LateApprovalResumeTokenProvider(
            LateApprovalResumeProbe deadlineProbe,
            bool late) : IWorkflowCallerAccessTokenProvider
        {
            public Task<string> IssueAsync(
                WorkflowCallerNyxIdAuthority authority,
                CancellationToken ct = default)
            {
                _ = authority;
                return late
                    ? deadlineProbe.CompleteAfterDeadlineAsync("fresh-token", ct)
                    : Task.FromResult("fresh-token");
            }
        }

        private sealed class BlockingCancelRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
        {
            private readonly TaskCompletionSource _cancelStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancelCancellationObserved =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _neverCompletes =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task CancelStarted => _cancelStarted.Task;
            public Task CancelCancellationObserved => _cancelCancellationObserved.Task;

            public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
                RuntimeCallbackTimeoutRequest request,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new RuntimeCallbackLease(
                    request.ActorId,
                    request.CallbackId,
                    1,
                    RuntimeCallbackBackend.InMemory));
            }

            public Task<RuntimeCallbackLease> ScheduleTimerAsync(
                RuntimeCallbackTimerRequest request,
                CancellationToken ct = default) =>
                throw new NotSupportedException();

            public async Task CancelAsync(
                RuntimeCallbackLease lease,
                CancellationToken ct = default)
            {
                _ = lease;
                _cancelStarted.TrySetResult();
                try
                {
                    await _neverCompletes.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _cancelCancellationObserved.TrySetResult();
                    throw;
                }
            }

            public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
                Task.CompletedTask;
        }

        private sealed class RecordingTerminalOrderEventStore(List<string> operationLog) : IEventStore
        {
            private readonly InMemoryEventStore _inner = new();
            public List<StateEvent[]> Batches { get; } = [];

            public async Task<EventStoreCommitResult> AppendAsync(
                string agentId,
                IEnumerable<StateEvent> events,
                long expectedVersion,
                CancellationToken ct = default)
            {
                var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
                Batches.Add(batch);
                var result = await _inner.AppendAsync(agentId, batch, expectedVersion, ct);
                if (batch.Any(static stateEvent =>
                        stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor)))
                {
                    operationLog.Add("actor-terminal-committed");
                }

                return result;
            }

            public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
                string agentId,
                long? fromVersion = null,
                CancellationToken ct = default) =>
                _inner.GetEventsAsync(agentId, fromVersion, ct);

            public Task<long> GetVersionAsync(
                string agentId,
                CancellationToken ct = default) =>
                _inner.GetVersionAsync(agentId, ct);

            public Task<long> DeleteEventsUpToAsync(
                string agentId,
                long toVersion,
                CancellationToken ct = default) =>
                _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
        }

        private sealed class BlockingWorkflowSuccessCompletionEventStore : IEventStore
        {
            private readonly TaskCompletionSource _appendStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancellationObserved =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _neverCompletes =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public InMemoryEventStore Inner { get; } = new();
            public Task SuccessCompletionAppendStarted => _appendStarted.Task;
            public Task CancellationObserved => _cancellationObserved.Task;

            public async Task<EventStoreCommitResult> AppendAsync(
                string agentId,
                IEnumerable<StateEvent> events,
                long expectedVersion,
                CancellationToken ct = default)
            {
                var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
                if (batch.Any(IsSuccessfulRoleCompletion))
                {
                    _appendStarted.TrySetResult();
                    try
                    {
                        await _neverCompletes.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _cancellationObserved.TrySetResult();
                        throw;
                    }
                }

                return await Inner.AppendAsync(agentId, batch, expectedVersion, ct);
            }

            public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
                string agentId,
                long? fromVersion = null,
                CancellationToken ct = default) =>
                Inner.GetEventsAsync(agentId, fromVersion, ct);

            public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
                Inner.GetVersionAsync(agentId, ct);

            public Task<long> DeleteEventsUpToAsync(
                string agentId,
                long toVersion,
                CancellationToken ct = default) =>
                Inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

            private static bool IsSuccessfulRoleCompletion(StateEvent stateEvent) =>
                stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
                stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>().Outcome ==
                RoleChatSessionOutcome.Completed;
        }

        private sealed class LateReturningCommittedWorkflowSuccessEventStore : IEventStore
        {
            private readonly TaskCompletionSource _successCommitCompleted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _deadlineObserved =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public InMemoryEventStore Inner { get; } = new();
            public Task SuccessCommitCompleted => _successCommitCompleted.Task;
            public Task DeadlineObserved => _deadlineObserved.Task;

            public async Task<EventStoreCommitResult> AppendAsync(
                string agentId,
                IEnumerable<StateEvent> events,
                long expectedVersion,
                CancellationToken ct = default)
            {
                var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
                if (!batch.Any(IsSuccessfulRoleCompletion))
                    return await Inner.AppendAsync(agentId, batch, expectedVersion, ct);

                var committed = await Inner.AppendAsync(
                    agentId,
                    batch,
                    expectedVersion,
                    CancellationToken.None);
                _successCommitCompleted.TrySetResult();
                using var registration = ct.Register(() => _deadlineObserved.TrySetResult());
                await _deadlineObserved.Task;
                return committed;
            }

            public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
                string agentId,
                long? fromVersion = null,
                CancellationToken ct = default) =>
                Inner.GetEventsAsync(agentId, fromVersion, ct);

            public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
                Inner.GetVersionAsync(agentId, ct);

            public Task<long> DeleteEventsUpToAsync(
                string agentId,
                long toVersion,
                CancellationToken ct = default) =>
                Inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

            private static bool IsSuccessfulRoleCompletion(StateEvent stateEvent) =>
                stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
                stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>().Outcome ==
                RoleChatSessionOutcome.Completed;
        }

        private sealed class FailCompletionCheckpointEventStore(int failureOrdinal = 1) : IEventStore
        {
            private readonly InMemoryEventStore _inner = new();
            private int _completionCheckpointAppends;

            public Task<EventStoreCommitResult> AppendAsync(
                string agentId,
                IEnumerable<StateEvent> events,
                long expectedVersion,
                CancellationToken ct = default)
            {
                var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
                if (batch.Any(stateEvent =>
                        stateEvent.EventData.Is(RoleChatRecoveryCheckpointUpdatedEvent.Descriptor) &&
                        stateEvent.EventData.Unpack<RoleChatRecoveryCheckpointUpdatedEvent>()
                            .Checkpoint.ToolCompletions.Count > 0) &&
                    Interlocked.Increment(ref _completionCheckpointAppends) == failureOrdinal)
                {
                    throw new InvalidOperationException("completion checkpoint append failed");
                }

                return _inner.AppendAsync(agentId, batch, expectedVersion, ct);
            }

            public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
                string agentId,
                long? fromVersion = null,
                CancellationToken ct = default) =>
                _inner.GetEventsAsync(agentId, fromVersion, ct);

            public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
                _inner.GetVersionAsync(agentId, ct);

            public Task<long> DeleteEventsUpToAsync(
                string agentId,
                long toVersion,
                CancellationToken ct = default) =>
                _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
        }

        private sealed class LateReturningClearPendingEventStore : IEventStore
        {
            private readonly TaskCompletionSource _clearPendingCommitted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseLateReturn =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _blocked;

            public InMemoryEventStore Inner { get; } = new();
            public Task ClearPendingCommitted => _clearPendingCommitted.Task;

            public void ReleaseLateReturn() => _releaseLateReturn.TrySetResult();

            public async Task<EventStoreCommitResult> AppendAsync(
                string agentId,
                IEnumerable<StateEvent> events,
                long expectedVersion,
                CancellationToken ct = default)
            {
                var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
                var result = await Inner.AppendAsync(agentId, batch, expectedVersion, ct);
                if (batch.Any(static stateEvent =>
                        stateEvent.EventData.Is(ClearPendingApprovalEvent.Descriptor)) &&
                    Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
                {
                    _clearPendingCommitted.TrySetResult();
                    await _releaseLateReturn.Task;
                }

                return result;
            }

            public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
                string agentId,
                long? fromVersion = null,
                CancellationToken ct = default) =>
                Inner.GetEventsAsync(agentId, fromVersion, ct);

            public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
                Inner.GetVersionAsync(agentId, ct);

            public Task<long> DeleteEventsUpToAsync(
                string agentId,
                long toVersion,
                CancellationToken ct = default) =>
                Inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
        }

        private sealed class RotatingWorkflowCallerAccessTokenProvider
            : IWorkflowCallerAccessTokenProvider
        {
            public List<WorkflowCallerNyxIdAuthority> Authorities { get; } = [];

            public Task<string> IssueAsync(
                WorkflowCallerNyxIdAuthority authority,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                Authorities.Add(authority.Clone());
                return Task.FromResult($"fresh-token-{Authorities.Count}");
            }
        }

        private sealed class RecordingNyxIdHandler : HttpMessageHandler
        {
            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "keys": [] }"""),
                });
            }
        }

        private sealed class FixedToolSource(params IAgentTool[] tools) : IAgentToolSource
        {
            public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
            }
        }

        private sealed class FixedToolSetRegistry(string name, IAgentToolSource source) : IToolSetRegistry
        {
            public IReadOnlyList<string> GetRegisteredNames() => [name];

            public ToolSetResolveResult Resolve(string? requestedName) =>
                string.Equals(requestedName, name, StringComparison.Ordinal)
                    ? ToolSetResolveResult.Success(name, [source])
                    : ToolSetResolveResult.Failure(new ToolSetResolveError(
                        ToolSetResolveError.UnknownNameCode,
                        requestedName ?? string.Empty,
                        "unknown",
                        [name]));
        }

        private sealed class AuthorizationRequiredWorkflowIntentLlmProvider
            : WorkflowIntentLlmProviderBase
        {
            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk
                {
                    ToolReceipt = new AgentToolReceipt
                    {
                        CallId = "call-authorization-required",
                        ToolName = "calendar_list_events",
                        Status = AgentToolReceiptStatus.AuthorizationRequired,
                        ErrorCode = "authorization_required",
                        ErrorMessage = "Connect Calendar to continue.",
                        AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                        {
                            ServiceSlug = "calendar",
                            ReasonCode = "service_not_connected",
                            SafeMessage = "Connect Calendar to continue.",
                        },
                    },
                };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                await Task.CompletedTask;
            }
        }

        private sealed class ToolThenThrowWorkflowIntentLlmProvider(string toolName)
            : WorkflowIntentLlmProviderBase
        {
            private int _calls;

            public int CallCount => Volatile.Read(ref _calls);

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call-partial-failure",
                            Name = toolName,
                            ArgumentsJson = "{}",
                        },
                    };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                    yield break;
                }

                await Task.CompletedTask;
                Exception? failure = new InvalidOperationException("later provider round failed");
                if (failure is not null)
                    throw failure;
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
            }
        }

        private sealed class ApprovalWorkflowIntentLlmProvider(string toolName)
            : WorkflowIntentLlmProviderBase
        {
            private int _calls;
            public int CallCount => Volatile.Read(ref _calls);

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call-approval",
                            Name = toolName,
                            ArgumentsJson = "{}",
                        },
                    };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                    yield break;
                }

                yield return new LLMStreamChunk
                {
                    DeltaContent = "approved completion",
                    Usage = new TokenUsage(11, 7, 18),
                };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
            }
        }

        private sealed class ApprovalThenToolWorkflowIntentLlmProvider(
            string approvalToolName,
            string continuationToolName) : WorkflowIntentLlmProviderBase
        {
            private int _calls;

            public int CallCount => Volatile.Read(ref _calls);

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                var call = Interlocked.Increment(ref _calls);
                if (call <= 2)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = call == 1 ? "call-approval-target" : "call-target-recovery",
                            Name = call == 1 ? approvalToolName : continuationToolName,
                            ArgumentsJson = "{}",
                        },
                    };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                    yield break;
                }

                yield return new LLMStreamChunk { DeltaContent = "recovered approval completion" };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
            }
        }

        private sealed class ApprovalThenThrowWorkflowIntentLlmProvider(string toolName)
            : WorkflowIntentLlmProviderBase
        {
            private int _calls;

            public int CallCount => Volatile.Read(ref _calls);

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call-approval-failure",
                            Name = toolName,
                            ArgumentsJson = "{}",
                        },
                    };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                    yield break;
                }

                await Task.CompletedTask;
                Exception? failure = new InvalidOperationException("approval provider failed");
                if (failure is not null)
                    throw failure;
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
            }
        }

        private sealed class LateAfterCancellationWorkflowIntentLlmProvider(bool yieldLateChunk)
            : WorkflowIntentLlmProviderBase
        {
            public const string LateContent = "late workflow content";

            private readonly TaskCompletionSource _streamStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancellationObserved =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _neverCompletes =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseAfterCancellation =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task StreamStarted => _streamStarted.Task;
            public Task CancellationObserved => _cancellationObserved.Task;

            public void ReleaseAfterCancellation() => _releaseAfterCancellation.TrySetResult();

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                _streamStarted.TrySetResult();
                try
                {
                    await _neverCompletes.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                }

                await _releaseAfterCancellation.Task;
                if (yieldLateChunk)
                    yield return new LLMStreamChunk { DeltaContent = LateContent };
            }
        }

        private sealed class ApprovalThenLateWorkflowIntentLlmProvider(
            string toolName,
            bool yieldLateChunk) : WorkflowIntentLlmProviderBase
        {
            private readonly TaskCompletionSource _continuationStreamStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancellationObserved =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _neverCompletes =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseAfterCancellation =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _streamCount;

            public Task ContinuationStreamStarted => _continuationStreamStarted.Task;
            public Task CancellationObserved => _cancellationObserved.Task;

            public void ReleaseAfterCancellation() => _releaseAfterCancellation.TrySetResult();

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                var streamCount = Interlocked.Increment(ref _streamCount);
                if (streamCount == 1)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call-approval-deadline",
                            Name = toolName,
                            ArgumentsJson = "{}",
                        },
                    };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                    yield break;
                }

                _continuationStreamStarted.TrySetResult();
                try
                {
                    await _neverCompletes.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                }

                await _releaseAfterCancellation.Task;
                if (yieldLateChunk)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaContent = LateAfterCancellationWorkflowIntentLlmProvider.LateContent,
                    };
                }
            }
        }

        private sealed class TwoApprovalWorkflowIntentLlmProvider(string toolName)
            : WorkflowIntentLlmProviderBase
        {
            private int _calls;

            public int CallCount => Volatile.Read(ref _calls);

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                var call = Interlocked.Increment(ref _calls);
                if (call is 1 or 2)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = $"call-approval-{call}",
                            Name = toolName,
                            ArgumentsJson = "{}",
                        },
                    };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                    yield break;
                }

                yield return new LLMStreamChunk { DeltaContent = "approval pending" };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
            }
        }

        private sealed class TwoApprovalsThenToolWorkflowIntentLlmProvider(
            string approvalToolName,
            string continuationToolName) : WorkflowIntentLlmProviderBase
        {
            private int _calls;

            public int CallCount => Volatile.Read(ref _calls);

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                var call = Interlocked.Increment(ref _calls);
                if (call <= 3)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = $"call-two-approval-{call}",
                            Name = call <= 2 ? approvalToolName : continuationToolName,
                            ArgumentsJson = "{}",
                        },
                    };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                    yield break;
                }

                yield return new LLMStreamChunk { DeltaContent = "recovered two-approval completion" };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
            }
        }

        private sealed class InvalidResultReferenceSecretVault : ISecretVault
        {
            private readonly InMemorySecretVault _inner = new();

            public async Task<StoreSecretResult> PutAsync(
                StoreSecretRequest request,
                CancellationToken ct = default)
            {
                var stored = await _inner.PutAsync(request, ct);
                if (!request.Purpose.EndsWith(".result.v1", StringComparison.Ordinal))
                    return stored;

                var invalidReference = stored.Reference.Clone();
                invalidReference.OwnerScopeKey = "invalid-owner";
                return new StoreSecretResult(invalidReference);
            }

            public Task<ResolveSecretResult> ResolveAsync(
                ResolveSecretRequest request,
                CancellationToken ct = default) =>
                _inner.ResolveAsync(request, ct);

            public Task<RotateSecretResult> RotateAsync(
                RotateSecretRequest request,
                CancellationToken ct = default) =>
                _inner.RotateAsync(request, ct);

            public Task<RevokeSecretResult> RevokeAsync(
                RevokeSecretRequest request,
                CancellationToken ct = default) =>
                _inner.RevokeAsync(request, ct);
        }

}
