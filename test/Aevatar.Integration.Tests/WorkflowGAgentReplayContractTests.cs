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
