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

public sealed class WorkflowGAgentWorkflowCallContractTests : WorkflowGAgentTestBase
{
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

            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
            await BindInteractiveWorkflowRunDefinitionAsync(agent,
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
}
