using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
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

public sealed class WorkflowGAgentReplayContractTests : WorkflowGAgentTestBase
{
        [Fact]
        public async Task WorkflowGAgent_ReplayContract_ShouldRestoreBoundDefinitionAfterReactivate()
        {
            var eventStore = new InMemoryEventStore();
            var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
            };

            WorkflowGAgent agent1 = CreateDefinitionAgent(eventStore);
            await agent1.ActivateAsync();
            await BindInteractiveWorkflowDefinitionAsync(agent1,
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_replay",
                inlineWorkflowYamls);
            await agent1.DeactivateAsync();

            var persisted = await eventStore.GetEventsAsync(agent1.Id);
            persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(BindWorkflowDefinitionEvent), StringComparison.Ordinal));

            WorkflowGAgent agent2 = CreateDefinitionAgent(eventStore);
            await agent2.ActivateAsync();

            agent2.State.WorkflowName.Should().Be("wf_replay");
            agent2.State.Compiled.Should().BeTrue();
            agent2.State.InlineWorkflowYamls.Should().ContainKey("sub_flow");
            (await agent2.GetDescriptionAsync()).Should().Contain("compiled");
        }

        [Fact]
        public async Task WorkflowRunGAgent_ReplayContract_ShouldRestoreTerminalStateAfterReactivate()
        {
            var eventStore = new InMemoryEventStore();
            var publisher = new RecordingEventPublisher();

            var agent1 = CreateRunAgent(eventStore: eventStore);
            agent1.EventPublisher = publisher;
            await agent1.ActivateAsync();
            await BindInteractiveWorkflowRunDefinitionAsync(agent1,
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_replay",
                runId: "run-replay");
            await SeedRuntimeContextAsync(agent1);
            await agent1.UpsertExecutionStateAsync(
                WorkflowExecutionKernel.ModuleStateKey,
                Any.Pack(new WorkflowExecutionKernelState
                {
                    Active = true,
                    RunId = "run-replay",
                    CurrentStepId = "step-terminal",
                    CurrentStepInput = "terminal-input",
                    CurrentStepDispatchPending = true,
                    Variables =
                    {
                        ["result"] = "terminal-fact",
                    },
                }));
            await agent1.HandleWorkflowCompleted(new WorkflowCompletedEvent
            {
                WorkflowName = "wf_replay",
                RunId = "run-replay",
                Success = true,
                Output = "done",
            });
            AssertRuntimeContextCleared(agent1);
            AssertTerminalKernelState(agent1);
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
            AssertTerminalKernelState(agent2);

            publisher.Published.Select(x => x.evt).OfType<WorkflowLlmInvocationCompletedEvent>()
                .Should().ContainSingle(x => x.Content == "done");
        }

        [Theory]
        [InlineData("completed", "done", "")]
        [InlineData("failed", "", "terminal-failure")]
        [InlineData("stopped", "", "terminal-stop")]
        public async Task WorkflowRunGAgent_ReplayContract_LateSameRunBindAndStart_ShouldNotReopenTerminalRun(
            string terminalStatus,
            string expectedOutput,
            string expectedError)
        {
            const string actorId = "workflow-run-terminal-monotonic";
            const string runId = "run-terminal-monotonic";
            var eventStore = new InMemoryEventStore();
            var agent1 = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent1, actorId);
            await agent1.ActivateAsync();
            var originalYaml = BuildValidWorkflowYaml("role_a", "RoleA");
            await BindInteractiveWorkflowRunDefinitionAsync(agent1,
                "definition-1",
                originalYaml,
                "wf_terminal",
                runId: runId);

            if (terminalStatus == "stopped")
            {
                await agent1.HandleWorkflowStopped(new WorkflowStoppedEvent
                {
                    RunId = runId,
                    Reason = expectedError,
                });
            }
            else
            {
                await agent1.HandleWorkflowCompleted(new WorkflowCompletedEvent
                {
                    WorkflowName = "wf_terminal",
                    RunId = runId,
                    Success = terminalStatus == "completed",
                    Output = expectedOutput,
                    Error = expectedError,
                });
            }

            var terminalState = agent1.State.Clone();
            await agent1.DeactivateAsync();

            var version = await eventStore.GetVersionAsync(actorId);
            var lateBind = new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = "definition-2",
                WorkflowName = "wf_replayed",
                WorkflowYaml = BuildValidWorkflowYaml("role_b", "RoleB"),
                RunId = runId,
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                ReusePolicy = WorkflowRunActorReusePolicy.SingleRun,
            };
            var lateStart = new WorkflowRunExecutionStartedEvent
            {
                RunId = runId,
                WorkflowName = "wf_replayed",
                Input = "late-input",
                StartedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            };
            var lateCompletedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(
                new DateTime(2026, 8, 14, 5, 6, 7), DateTimeKind.Utc));
            await eventStore.AppendAsync(actorId,
            [
                StateEventFor(actorId, version + 1, lateBind),
                StateEventFor(actorId, version + 2, lateStart),
                StateEventFor(actorId, version + 3, new WorkflowCompletedEvent
                {
                    RunId = runId,
                    WorkflowName = "wf_late",
                    Success = terminalStatus != "completed",
                    Output = "late-output",
                    Error = "late-error",
                }),
                StateEventFor(actorId, version + 4, new WorkflowStoppedEvent
                {
                    RunId = runId,
                    Reason = "late-workflow-stop",
                    CompletedAtUtc = lateCompletedAt,
                }),
                StateEventFor(actorId, version + 5, new WorkflowRunStoppedEvent
                {
                    RunId = runId,
                    Reason = "late-run-stop",
                    CompletedAtUtc = lateCompletedAt,
                }),
                StateEventFor(actorId, version + 6, new WorkflowRunTerminalTimingRecordedEvent
                {
                    RunId = runId,
                    CompletedAtUtc = lateCompletedAt,
                }),
            ], version);

            var agent2 = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent2, actorId);
            await agent2.ActivateAsync();

            agent2.State.Equals(terminalState).Should().BeTrue();
            agent2.State.Status.Should().Be(terminalStatus);
            agent2.State.RunId.Should().Be(runId);
            agent2.State.WorkflowName.Should().Be("wf_terminal");
            agent2.State.WorkflowYaml.Should().Be(originalYaml);
            agent2.State.FinalOutput.Should().Be(expectedOutput);
            agent2.State.FinalError.Should().Be(expectedError);
        }

        [Fact]
        public async Task WorkflowRunGAgent_ReplayContract_LateLegacyBindWithoutRunId_ShouldNotReopenTerminalRun()
        {
            const string actorId = "workflow-run-terminal-legacy-bind";
            const string runId = "run-terminal-legacy-bind";
            var eventStore = new InMemoryEventStore();
            var agent1 = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent1, actorId);
            await agent1.ActivateAsync();
            await BindInteractiveWorkflowRunDefinitionAsync(agent1,
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_terminal",
                runId: runId);
            await agent1.HandleWorkflowCompleted(new WorkflowCompletedEvent
            {
                WorkflowName = "wf_terminal",
                RunId = runId,
                Success = true,
                Output = "done",
            });
            var terminalState = agent1.State.Clone();
            await agent1.DeactivateAsync();

            var version = await eventStore.GetVersionAsync(actorId);
            await eventStore.AppendAsync(actorId,
            [
                StateEventFor(actorId, version + 1, new BindWorkflowRunDefinitionEvent
                {
                    DefinitionActorId = "definition-legacy",
                    WorkflowName = "wf_legacy",
                    WorkflowYaml = BuildValidWorkflowYaml("role_b", "RoleB"),
                    RunId = string.Empty,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                }),
            ], version);

            var agent2 = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent2, actorId);
            await agent2.ActivateAsync();

            agent2.State.Equals(terminalState).Should().BeTrue();
        }

        [Fact]
        public async Task WorkflowRunGAgent_ReplayContract_MultipleLegacyBinds_ShouldRestoreLatestRun()
        {
            const string actorId = "workflow-run-legacy-multiple-binds";
            var eventStore = new InMemoryEventStore();
            await eventStore.AppendAsync(actorId,
            [
                StateEventFor(actorId, 1, new BindWorkflowRunDefinitionEvent
                {
                    DefinitionActorId = "definition-legacy-first",
                    WorkflowName = "wf_legacy_first",
                    WorkflowYaml = BuildValidWorkflowYaml("role_a", "RoleA", workflowName: "wf_legacy_first"),
                    RunId = "run-legacy-first",
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    ReusePolicy = WorkflowRunActorReusePolicy.Unspecified,
                    BindingGeneration = 0,
                    ReuseAuthorityActorId = string.Empty,
                }),
                StateEventFor(actorId, 2, new BindWorkflowRunDefinitionEvent
                {
                    DefinitionActorId = "definition-legacy-latest",
                    WorkflowName = "wf_legacy_latest",
                    WorkflowYaml = BuildValidWorkflowYaml("role_b", "RoleB", workflowName: "wf_legacy_latest"),
                    RunId = "run-legacy-latest",
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    ReusePolicy = WorkflowRunActorReusePolicy.Unspecified,
                    BindingGeneration = 0,
                    ReuseAuthorityActorId = string.Empty,
                }),
            ], expectedVersion: 0);

            var replayed = CreateRunAgent(eventStore: eventStore);
            SetAgentId(replayed, actorId);
            await replayed.ActivateAsync();

            replayed.State.DefinitionActorId.Should().Be("definition-legacy-latest");
            replayed.State.WorkflowName.Should().Be("wf_legacy_latest");
            replayed.State.RunId.Should().Be("run-legacy-latest");
            replayed.State.Status.Should().Be("bound");
            replayed.State.ReusePolicy.Should().Be(WorkflowRunActorReusePolicy.Unspecified);
            replayed.State.BindingGeneration.Should().Be(0);
            replayed.State.ReuseAuthorityActorId.Should().BeEmpty();
        }

        [Theory]
        [InlineData("completed", "done", "")]
        [InlineData("failed", "", "terminal-failure")]
        [InlineData("stopped", "", "terminal-stop")]
        public async Task WorkflowRunGAgent_ReplayContract_LateDifferentRunStartWithoutBind_ShouldNotReopenTerminalRun(
            string terminalStatus,
            string expectedOutput,
            string expectedError)
        {
            const string actorId = "workflow-run-terminal-orphan-start";
            const string runId = "run-terminal-orphan-start";
            var eventStore = new InMemoryEventStore();
            var agent1 = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent1, actorId);
            await agent1.ActivateAsync();
            await BindInteractiveWorkflowRunDefinitionAsync(agent1,
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_terminal",
                runId: runId);
            if (terminalStatus == "stopped")
            {
                await agent1.HandleWorkflowStopped(new WorkflowStoppedEvent
                {
                    RunId = runId,
                    Reason = expectedError,
                });
            }
            else
            {
                await agent1.HandleWorkflowCompleted(new WorkflowCompletedEvent
                {
                    WorkflowName = "wf_terminal",
                    RunId = runId,
                    Success = terminalStatus == "completed",
                    Output = expectedOutput,
                    Error = expectedError,
                });
            }

            var terminalState = agent1.State.Clone();
            await agent1.DeactivateAsync();
            var version = await eventStore.GetVersionAsync(actorId);
            await eventStore.AppendAsync(actorId,
            [
                StateEventFor(actorId, version + 1, new WorkflowRunExecutionStartedEvent
                {
                    RunId = "run-unbound-next",
                    WorkflowName = "wf_unbound_next",
                    Input = "late-input",
                    StartedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
            ], version);

            var agent2 = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent2, actorId);
            await agent2.ActivateAsync();

            agent2.State.Equals(terminalState).Should().BeTrue();
        }

        [Fact]
        public async Task WorkflowRunGAgent_ReplayContract_DifferentRunBindAndStart_ShouldNotReplaceTerminalRun()
        {
            const string actorId = "workflow-run-terminal-next-run";
            const string firstRunId = "run-terminal-first";
            const string nextRunId = "run-terminal-next";
            var firstStartedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(new DateTime(2026, 8, 14, 1, 2, 3), DateTimeKind.Utc));
            var nextStartedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(new DateTime(2026, 8, 14, 2, 3, 4), DateTimeKind.Utc));
            var eventStore = new InMemoryEventStore();
            var agent1 = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent1, actorId);
            await agent1.ActivateAsync();
            await BindInteractiveWorkflowRunDefinitionAsync(agent1,
                "definition-1",
                BuildValidWorkflowYaml("role_a", "RoleA"),
                "wf_first",
                runId: firstRunId);
            await agent1.DeactivateAsync();

            var version = await eventStore.GetVersionAsync(actorId);
            var source = new WorkflowArtifactSourceIdentity
            {
                PublisherActorId = "role-first",
                CommittedEventId = "artifact-first",
                CommittedStateVersion = 7,
            };
            await eventStore.AppendAsync(actorId,
            [
                StateEventFor(actorId, version + 1, new WorkflowRunExecutionStartedEvent
                {
                    RunId = firstRunId,
                    WorkflowName = "wf_first",
                    Input = "first-input",
                    CurrentTurnId = "turn-first",
                    StartedAtUtc = firstStartedAt,
                }),
                StateEventFor(actorId, version + 2, new WorkflowRoleReplyRecordedEvent
                {
                    RunId = firstRunId,
                    RoleActorId = "role-first",
                    RoleId = "role-first",
                    Source = source,
                }),
                StateEventFor(actorId, version + 3, new WorkflowInteractiveActionHandoffDispatchedEvent
                {
                    HandoffId = "handoff-first",
                    Request = new WorkflowInteractiveActionRequestWirePayload
                    {
                        ActorId = "interactive-first",
                        Action = "service.connect",
                    },
                    TerminalContinuation = new WorkflowLlmInvocationCompletedEvent
                    {
                        RunId = firstRunId,
                        Success = true,
                    },
                }),
                StateEventFor(actorId, version + 4, new WorkflowCompletedEvent
                {
                    RunId = firstRunId,
                    WorkflowName = "wf_first",
                    Success = true,
                    Output = "first-done",
                }),
                StateEventFor(actorId, version + 5, new BindWorkflowRunDefinitionEvent
                {
                    DefinitionActorId = "definition-2",
                    WorkflowName = "wf_next",
                    WorkflowYaml = BuildValidWorkflowYaml("role_b", "RoleB"),
                    RunId = nextRunId,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    ReusePolicy = WorkflowRunActorReusePolicy.SingleRun,
                }),
                StateEventFor(actorId, version + 6, new WorkflowRunExecutionStartedEvent
                {
                    RunId = nextRunId,
                    WorkflowName = "wf_next",
                    Input = "next-input",
                    CurrentTurnId = "turn-next",
                    StartedAtUtc = nextStartedAt,
                }),
                StateEventFor(actorId, version + 7, new BindWorkflowRunDefinitionEvent
                {
                    DefinitionActorId = "definition-stale",
                    WorkflowName = "wf_first",
                    WorkflowYaml = BuildValidWorkflowYaml("role_a", "RoleA"),
                    RunId = firstRunId,
                    ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    ReusePolicy = WorkflowRunActorReusePolicy.SingleRun,
                }),
                StateEventFor(actorId, version + 8, new WorkflowCompletedEvent
                {
                    RunId = firstRunId,
                    WorkflowName = "wf_first",
                    Success = false,
                    Error = "late-first-run-completion",
                }),
                StateEventFor(actorId, version + 9, new WorkflowRunTerminalTimingRecordedEvent
                {
                    RunId = firstRunId,
                    CompletedAtUtc = Timestamp.FromDateTime(DateTime.SpecifyKind(
                        new DateTime(2026, 8, 14, 3, 4, 5), DateTimeKind.Utc)),
                }),
                StateEventFor(actorId, version + 10, new WorkflowRoleReplyRecordedEvent
                {
                    RunId = nextRunId,
                    RoleActorId = "role-next",
                    RoleId = "role-next",
                    Source = new WorkflowArtifactSourceIdentity
                    {
                        PublisherActorId = "role-next",
                        CommittedEventId = "artifact-next-stale",
                        CommittedStateVersion = 8,
                    },
                }),
            ], version);

            var agent2 = CreateRunAgent(eventStore: eventStore);
            SetAgentId(agent2, actorId);
            await agent2.ActivateAsync();

            agent2.State.RunId.Should().Be(firstRunId);
            agent2.State.Status.Should().Be("completed");
            agent2.State.WorkflowName.Should().Be("wf_first");
            agent2.State.StartedAtUtc.Should().BeEquivalentTo(firstStartedAt);
            agent2.State.CurrentTurnId.Should().Be("turn-first");
            agent2.State.ProcessedArtifactSources.Should().ContainSingle();
            agent2.State.ProcessedArtifactSources[0].CommittedEventId.Should().Be("artifact-first");
            agent2.State.InteractiveActionHandoffs.Should().ContainSingle();
            agent2.State.FinalOutput.Should().Be("first-done");
            agent2.State.FinalError.Should().BeEmpty();
        }

        private static StateEvent StateEventFor(string actorId, long version, IMessage evt) =>
            new()
            {
                AgentId = actorId,
                EventId = $"late-terminal-event-{version}",
                EventType = evt.Descriptor.FullName,
                EventData = Any.Pack(evt),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = version,
            };

        private static void AssertTerminalKernelState(WorkflowRunGAgent agent)
        {
            var kernel = agent.State.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
                .Unpack<WorkflowExecutionKernelState>();
            kernel.Active.Should().BeFalse();
            kernel.RunId.Should().BeEmpty();
            kernel.CurrentStepDispatchPending.Should().BeFalse();
            kernel.CurrentStepId.Should().Be("step-terminal");
            kernel.Variables.Should().Contain("result", "terminal-fact");
        }

        [Fact]
        public async Task WorkflowRunGAgent_ReplayContract_ShouldRestoreExecutionContextAfterChatStart()
        {
            var eventStore = new InMemoryEventStore();
            var runtimeSecretStore = new InMemoryRuntimeSecretStore();
            var runtime = new RecordingActorRuntime();
            var agent1 = CreateRunAgent(runtime: runtime, eventStore: eventStore, runtimeSecretStore: runtimeSecretStore);
            SetAgentId(agent1, "workflow-run-context-replay");

            await agent1.ActivateAsync();
            await BindInteractiveWorkflowRunDefinitionAsync(agent1,
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
                CallerCredential = new WorkflowCallerCredential
                {
                    BearerToken = " secret ",
                },
                LlmControl = new WorkflowLlmControlContext
                {
                    ModelOverride = " model-main ",
                    MaxToolRoundsOverride = 4,
                    UserMemoryPrompt = " memory-main ",
                },
            });

            agent1.State.ExecutionContext.CallerCredential!.BearerToken.Should().BeEmpty();
            agent1.State.ExecutionContext.CallerCredential.RuntimeSecretReference.Should().NotBeNull();
            var credential1 = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(agent1);
            credential1.Found.Should().BeTrue();
            credential1.Credential.BearerToken.Should().Be("secret");
            agent1.State.ExecutionContext.Llm!.ModelOverride.Should().Be("model-main");
            await agent1.DeactivateAsync();

            var persisted = await eventStore.GetEventsAsync(agent1.Id);
            persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(WorkflowRunExecutionStartedEvent), StringComparison.Ordinal));

            var agent2 = CreateRunAgent(runtime: runtime, eventStore: eventStore, runtimeSecretStore: runtimeSecretStore);
            SetAgentId(agent2, "workflow-run-context-replay");
            await agent2.ActivateAsync();

            agent2.State.ExecutionContext.CallerCredential!.BearerToken.Should().BeEmpty();
            agent2.State.ExecutionContext.CallerCredential.RuntimeSecretReference.Should().NotBeNull();
            var credential2 = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(agent2);
            credential2.Found.Should().BeTrue();
            credential2.Credential.BearerToken.Should().Be("secret");
            agent2.State.ExecutionContext.Llm!.ModelOverride.Should().Be("model-main");
            agent2.State.ExecutionContext.Llm.MaxToolRoundsOverride.Should().Be(4);
            agent2.State.ExecutionContext.Llm.UserMemoryPrompt.Should().Be("memory-main");
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent1,
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
}
