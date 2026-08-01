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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Any = Google.Protobuf.WellKnownTypes.Any;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowGAgentExecutionTests : WorkflowGAgentTestBase
{
        [Fact]
        public async Task WorkflowGAgent_WhenSwitchingWorkflowName_ShouldThrow()
        {
            var agent = CreateDefinitionAgent();
            await BindInteractiveWorkflowDefinitionAsync(agent, BuildValidWorkflowYaml("role_a", "RoleA"), "wf_a");

            var act = () => BindInteractiveWorkflowDefinitionAsync(agent, BuildValidWorkflowYaml("role_a", "RoleA"), "wf_b");

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*cannot switch*");
        }

        [Fact]
        public async Task WorkflowGAgent_WhenYamlInvalid_ShouldMarkInvalidAndDescribe()
        {
            var agent = CreateDefinitionAgent();

            await BindInteractiveWorkflowDefinitionAsync(agent, "", "wf_invalid");
            var description = await agent.GetDescriptionAsync();

            agent.State.Compiled.Should().BeFalse();
            agent.State.CompilationError.Should().Be("workflow yaml is empty");
            description.Should().Contain("invalid");
            description.Should().Contain("wf_invalid");
        }

        [Fact]
        public async Task WorkflowRunGAgent_WhenNotCompiled_ShouldPublishFailureResponse()
        {
            var publisher = new RecordingEventPublisher();
            var runtime = new RecordingActorRuntime();
            var agent = CreateRunAgent(runtime: runtime);
            agent.EventPublisher = publisher;
            await BindInteractiveWorkflowRunDefinitionAsync(agent, "definition-1", "", "wf_invalid", runId: "run-invalid");

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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_valid",
                runId: "run-1");

            await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "first", SessionId = "s1" });
            await agent.HandleChatRequest(new WorkflowChatRequestEvent { Prompt = "second", SessionId = "s2" });

            runtime.CreateCalls.Should().Be(0);
            runtime.CreateByKindCalls.Should().ContainSingle().Which.Should().Be((
                "workflow.role-agent",
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
        public async Task WorkflowRunGAgent_ShouldRenderConversationContextIntoExecutionInput()
        {
            var publisher = new RecordingEventPublisher();
            var runtime = new RecordingActorRuntime();
            var agent = CreateRunAgent(runtime: runtime);
            agent.EventPublisher = publisher;
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_valid",
                runId: "run-1");

            await agent.HandleChatRequest(new WorkflowChatRequestEvent
            {
                Prompt = "team01",
                SessionId = "s1",
                ConversationContext = new WorkflowConversationContext
                {
                    ScopeId = "scope-a",
                    ConversationId = "conversation-alpha",
                    StateVersion = 3,
                    MaxMessageCount = 24,
                    Messages =
                    {
                        new WorkflowConversationMessage
                        {
                            Sequence = 1,
                            TurnId = "turn-previous",
                            Role = WorkflowConversationRole.User,
                            Content = "Create a workflow that generates fund analysis reports.",
                        },
                        new WorkflowConversationMessage
                        {
                            Sequence = 2,
                            TurnId = "turn-previous",
                            Role = WorkflowConversationRole.Assistant,
                            Content = "Choose a Team: team01 or team02.",
                        },
                    },
                },
            });

            var start = publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().Single();
            start.Input.Should().Contain("[user] Create a workflow that generates fund analysis reports.");
            start.Input.Should().Contain("[assistant] Choose a Team: team01 or team02.");
            start.Input.Should().Contain("<current_user_message>\nteam01\n</current_user_message>");
            start.Input.Should().Contain("team01");
            start.Input.Split("team01", StringSplitOptions.None).Should().HaveCount(3);
        }

        [Fact]
        public async Task WorkflowRunGAgent_ShouldRenderTypedConversationEnvelope_WhenConversationContextHasNoMessages()
        {
            var publisher = new RecordingEventPublisher();
            var runtime = new RecordingActorRuntime();
            var agent = CreateRunAgent(runtime: runtime);
            agent.EventPublisher = publisher;
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_valid",
                runId: "run-1");

            await agent.HandleChatRequest(new WorkflowChatRequestEvent
            {
                Prompt = "team01",
                SessionId = "s1",
                ConversationContext = new WorkflowConversationContext
                {
                    ScopeId = "scope-a",
                    ConversationId = "conversation-alpha",
                    StateVersion = 7,
                    MaxMessageCount = 24,
                },
            });

            var start = publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().Single();
            start.Input.Should().Contain("<conversation_context>");
            start.Input.Should().Contain("</conversation_context>");
            start.Input.Should().Contain("<current_user_message>\nteam01\n</current_user_message>");
            start.Input.Should().NotBe("team01");
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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

            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
        public async Task WorkflowRunGAgent_CommittedObservation_ShouldRedactExecutionContextAndCapturedSecrets()
        {
            var eventStore = new InMemoryEventStore();
            var publisher = new RecordingEventPublisher();
            var agent = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent, "workflow-run-redaction");
            agent.EventPublisher = publisher;
            agent.CommittedStateEventPublisher = publisher;

            await agent.ActivateAsync();
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_redaction",
                runId: "run-redaction");

            await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
                agent,
                new WorkflowCallerCredential
                {
                    BearerToken = "secret",
                });
            await WorkflowRequestMetadataRuntimeContextAccess.SetLlmControlAsync(
                agent,
                new WorkflowLlmControlContext
                {
                    ModelOverride = "model",
                    MaxToolRoundsOverride = 2,
                    UserMemoryPrompt = "memory",
                });

            await agent.UpsertExecutionStateAsync("scope-a", Any.Pack(new StringValue { Value = "state-a" }));
            var captureCtx = new TestEventHandlerContext(agent.Services, agent, NullLogger.Instance);
            await SecureInputRuntimeContextAccess.SetCapturedValueAsync(
                captureCtx,
                "run-redaction",
                "api_key",
                "sk-secret",
                CancellationToken.None);

            agent.State.ExecutionContext.Llm!.ModelOverride.Should().Be("model");
            agent.State.ExecutionContext.CallerCredential!.BearerToken.Should().BeEmpty();
            var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(agent);
            credential.Found.Should().BeTrue();
            credential.Credential.BearerToken.Should().Be("secret");
            agent.State.ExecutionStates[SecureInputStateAccess.ModuleStateKey]
                .Unpack<SecureInputModuleState>()
                .Captured["run-redaction::api_key"]
                .ValueReference.Should().NotBeNull();

            var observedState = publisher.Published
                .Select(x => x.evt)
                .OfType<CommittedStateEventPublished>()
                .Last()
                .StateRoot
                .Unpack<WorkflowRunState>();

            observedState.ExecutionContext.Llm!.ModelOverride.Should().Be("model");
            observedState.ExecutionContext.Llm.MaxToolRoundsOverride.Should().Be(2);
            observedState.ExecutionContext.Llm.UserMemoryPrompt.Should().Be("memory");
            observedState.ExecutionContext.CallerCredential!.BearerToken.Should().BeEmpty();
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
            observedContextEvent[0].ExecutionContextDelta.CallerCredential!.BearerToken.Should().BeEmpty();
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
        public async Task WorkflowRunGAgent_WhenRunCompletes_ShouldCleanupRoleActors()
        {
            var runtime = new RecordingActorRuntime();
            var agent = CreateRunAgent(
                runtime: runtime);

            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
        public async Task WorkflowRunGAgent_ShouldPersistObservedWorkflowCommandId_FromChatMetadata()
        {
            var eventStore = new InMemoryEventStore();
            var agent = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent, "workflow-run-command");

            agent.RunId.Should().Be("workflow-run-command");

            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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

            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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

            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
}
