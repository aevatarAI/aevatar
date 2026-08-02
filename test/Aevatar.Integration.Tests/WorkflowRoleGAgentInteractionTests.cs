using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
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
                    "step-completion-publisher-deadline:" + firstSessionId);

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
            var initialScheduler = new RecordingWorkflowCompletionCallbackScheduler();
            var (initial, initialPublisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new RecordingWorkflowIntentLlmProvider(),
                actorId,
                callbackScheduler: initialScheduler);
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
                callbackScheduler: recoveredScheduler);

            recovered.State.Sessions[sessionId].WorkflowLlmCompletionDeliveryStatus.Should()
                .Be(WorkflowLlmCompletionDeliveryStatus.Dispatched);
            recovered.State.Sessions[sessionId].WorkflowLlmCompletionDeliveryAttempt.Should().Be(1);
            recoveredPublisher.PublicationsWithOptions
                .Where(static publication =>
                    publication.Event is WorkflowLlmInvocationCompletedEvent completed &&
                    completed.SessionId == sessionId)
                .Should().ContainSingle()
                .Which.Options!.Delivery!.OperationId.Should().Be(
                    "workflow-llm-terminal:run-activation-retry:step-activation-retry:" + sessionId);

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
            publisher.Sent.Select(static item => item.evt)
                .OfType<ChatRequestEvent>().Should().BeEmpty();
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
            var continuation = publisher.Sent
                .Select(static item => item.evt)
                .OfType<ChatRequestEvent>()
                .Should().ContainSingle().Which;

            var execution = agent.HandleChatRequest(continuation);
            await llmProvider.ContinuationStreamStarted;
            timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
            await llmProvider.CancellationObserved;
            llmProvider.ReleaseAfterCancellation();
            await execution;

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
            publisher.Sent.Select(static item => item.evt)
                .OfType<ChatRequestEvent>().Should().BeEmpty();
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
            publisher.Sent.Select(static item => item.evt)
                .OfType<ChatRequestEvent>().Should().BeEmpty();
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
            publisher.Sent.Select(static item => item.evt)
                .OfType<ChatRequestEvent>().Should().BeEmpty();
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
            publisher.BeforeSendAsync = (evt, ct) => evt is ChatRequestEvent
                ? sendProbe.CompleteAfterDeadlineAsync(true, ct)
                : Task.CompletedTask;

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

            var queuedContinuation = publisher.Sent
                .Select(static item => item.evt)
                .OfType<ChatRequestEvent>()
                .Should().ContainSingle().Which;
            var providerCallsAfterTimeout = llmProvider.CallCount;
            var tokenRefreshesAfterTimeout = tokenProvider.Authorities.Count;
            var toolExecutionsAfterTimeout = tool.ExecuteCount;

            await agent.HandleChatRequest(queuedContinuation);

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
                .Should().OnlyContain(completed =>
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

            var continuation = publisher.Sent
                .Select(static item => item.evt)
                .OfType<ChatRequestEvent>()
                .Should().ContainSingle().Subject;
            await agent.HandleChatRequest(continuation);

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
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(completed =>
                    completed.Success &&
                    completed.RunId == "run-approval" &&
                    completed.StepId == "step-approval" &&
                    completed.SessionId == "session-approval" &&
                    completed.Content == "approved completion");
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
        public async Task WorkflowRoleGAgent_WhenContinuationNeedsAnotherApproval_ShouldSuspendAgain()
        {
            var eventStore = new InMemoryEventStore();
            var tool = new ApprovalRequiredWorkflowTool();
            var tokenProvider = new RotatingWorkflowCallerAccessTokenProvider();
            var registry = new FixedToolSetRegistry(
                "studio.write",
                new FixedToolSource(tool));
            var (agent, publisher) = await CreateActivatedWorkflowRoleAgentAsync(
                eventStore,
                new TwoApprovalWorkflowIntentLlmProvider(tool.Name),
                "workflow-role-agent-two-approvals",
                toolSetRegistry: registry,
                callerAccessTokenProvider: tokenProvider);

            await agent.HandleWorkflowLlmExecutionIntent(ApprovalIntent(tool.Name));
            var firstRequestId = agent.State.PendingApproval.RequestId;
            await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = firstRequestId,
                Approved = true,
                ContinuationTurnId = "approval-continuation-1",
            });
            var continuation = publisher.Sent
                .Select(static item => item.evt)
                .OfType<ChatRequestEvent>()
                .Should().ContainSingle().Subject;

            await agent.HandleChatRequest(continuation);

            tool.ExecuteCount.Should().Be(1);
            agent.State.PendingApproval.Should().NotBeNull();
            agent.State.PendingApproval.RequestId.Should().NotBe(firstRequestId);
            publisher.Published.Select(static item => item.evt)
                .OfType<ToolApprovalRequestEvent>().Should().HaveCount(2);
            publisher.Published.Select(static item => item.evt)
                .OfType<WorkflowLlmInvocationCompletedEvent>().Should().BeEmpty();
        }

        private static WorkflowLlmExecutionIntent ApprovalIntent(string toolName) => new()
        {
            RunId = "run-approval",
            StepId = "step-approval",
            SessionId = "session-approval",
            Prompt = "update service",
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

                yield return new LLMStreamChunk { DeltaContent = "approved completion" };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
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

                if (streamCount == 2)
                {
                    yield return new LLMStreamChunk { DeltaContent = "approval pending" };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
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

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                var call = Interlocked.Increment(ref _calls);
                if (call is 1 or 3)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = $"call-approval-{(call + 1) / 2}",
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

}
