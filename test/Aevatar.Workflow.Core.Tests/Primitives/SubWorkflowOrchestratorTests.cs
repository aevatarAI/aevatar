using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class SubWorkflowOrchestratorTests
{
    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenParentStepMissing_ShouldPublishFailure()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                ParentRunId = " parent-run ",
                ParentStepId = " ",
                WorkflowName = "sub_flow",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().ContainSingle();
        var failure = harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.RunId.Should().Be("parent-run");
        failure.StepId.Should().BeEmpty();
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("parent_step_id");
        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Persisted.Should().BeEmpty();
        harness.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenWorkflowNameMissing_ShouldPublishFailure()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = " ",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().ContainSingle();
        harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Which.Error
            .Should().Contain("missing workflow parameter");
        harness.Runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenLifecycleUnsupported_ShouldPublishFailure()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = "sub_flow",
                Lifecycle = "invalid",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().ContainSingle();
        harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Which.Error
            .Should().Contain(WorkflowCallLifecycle.AllowedValuesText);
        harness.Runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenDefinitionActorMissing_ShouldPublishFailure()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-1",
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = "sub_flow",
            },
            new WorkflowRunState(),
            CancellationToken.None);

        harness.Published.Should().ContainSingle();
        harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Which.Error
            .Should().Contain("definition actor");
        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDefinitionResolvedAsync_WhenSingletonBindingExistsAndActorIsAlive_ShouldReuseActor()
    {
        const string definitionActorId = "workflow-definition:sub_flow";
        const string childActorId = "owner-1:workflow:workflow-definition-sub_flow";
        var harness = CreateHarness();
        harness.Runtime.StoredActors[definitionActorId] = new RecordingActor(definitionActorId);
        harness.Runtime.StoredActors[childActorId] = new RecordingActor(childActorId);

        var state = new WorkflowRunState();
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "sub_flow",
            ChildActorId = childActorId,
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = definitionActorId,
            DefinitionVersion = 7,
        });

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-1",
                ParentRunId = "parent-run",
                ParentStepId = "step-a",
                WorkflowName = "sub_flow",
                Input = "payload-a",
                Lifecycle = WorkflowCallLifecycle.Singleton,
            },
            state,
            CancellationToken.None);

        harness.Persisted.Should().ContainSingle(x => x is SubWorkflowDefinitionResolutionRegisteredEvent);
        harness.Sent.Should().ContainSingle(x => x.TargetActorId == definitionActorId);
        var resolutionState = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered(
            state,
            harness.Persisted.OfType<SubWorkflowDefinitionResolutionRegisteredEvent>().Single());

        harness.Persisted.Clear();
        harness.Sent.Clear();

        await harness.Orchestrator.HandleDefinitionResolvedAsync(
            new SubWorkflowDefinitionResolvedEvent
            {
                InvocationId = "invoke-1",
                Definition = new WorkflowDefinitionSnapshot
                {
                    DefinitionActorId = definitionActorId,
                    WorkflowName = "sub_flow",
                    WorkflowYaml = "name: sub_flow\nroles: []\nsteps: []\n",
                    DefinitionVersion = 7,
                },
            },
            resolutionState,
            CancellationToken.None);

        harness.Runtime.CreateRequests.Should().BeEmpty();
        harness.Runtime.Linked.Should().BeEmpty();
        harness.Persisted.Should().ContainSingle(x => x is SubWorkflowInvocationRegisteredEvent);
        harness.Persisted.Should().NotContain(x => x is SubWorkflowBindingUpsertedEvent);
        harness.Sent.Should().ContainSingle(x => x.TargetActorId == childActorId);
        var start = harness.Sent.Single().Message.Should().BeOfType<StartWorkflowEvent>().Subject;
        start.RunId.Should().Be("invoke-1");
        start.Parameters["workflow_call.parent_run_id"].Should().Be("parent-run");
        start.Parameters["workflow_call.parent_step_id"].Should().Be("step-a");
    }

    [Fact]
    public async Task HandleDefinitionResolvedAsync_WhenBindingStale_ShouldCreateAndBindNewChildActor()
    {
        const string definitionActorId = "workflow-definition:sub flow";
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.InlineWorkflowYamls["sub flow"] = "name: inline\nroles: []\nsteps: []\n";
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "invoke-2",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub flow",
            DefinitionActorId = definitionActorId,
            Input = "payload-b",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-2"] = 0;
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "sub flow",
            ChildActorId = "owner-1:workflow:workflow-definition-sub-flow",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = definitionActorId,
            DefinitionVersion = 1,
        });

        await harness.Orchestrator.HandleDefinitionResolvedAsync(
            new SubWorkflowDefinitionResolvedEvent
            {
                InvocationId = "invoke-2",
                Definition = new WorkflowDefinitionSnapshot
                {
                    DefinitionActorId = definitionActorId,
                    WorkflowName = "sub flow",
                    WorkflowYaml = "name: sub flow\nroles: []\nsteps: []\n",
                    ScopeId = "scope-a",
                    DefinitionVersion = 2,
                },
            },
            state,
            CancellationToken.None);

        harness.Runtime.CreateRequests.Should().ContainSingle();
        var createdRequest = harness.Runtime.CreateRequests.Single();
        createdRequest.AgentType.Should().Be(typeof(WorkflowRunGAgent));
        createdRequest.RequestedId.Should().Be("owner-1:workflow:workflow-definition-sub-flow");
        harness.Runtime.Linked.Should().ContainSingle(x =>
            x.ParentId == "owner-1" &&
            x.ChildId == "owner-1:workflow:workflow-definition-sub-flow");
        harness.Persisted.OfType<SubWorkflowBindingUpsertedEvent>().Should().ContainSingle(x =>
            x.WorkflowName == "sub flow" &&
            x.ChildActorId == "owner-1:workflow:workflow-definition-sub-flow" &&
            x.DefinitionActorId == definitionActorId &&
            x.DefinitionVersion == 2);
        harness.Persisted.OfType<SubWorkflowInvocationRegisteredEvent>().Should().ContainSingle(x =>
            x.ChildRunId == "invoke-2" &&
            x.DefinitionActorId == definitionActorId &&
            x.DefinitionVersion == 2);
        state.ScopeId = "scope-a";
        var childActor = harness.Runtime.StoredActors["owner-1:workflow:workflow-definition-sub-flow"];
        childActor.LastHandledEnvelope.Should().NotBeNull();
        childActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowRunDefinitionEvent.Descriptor).Should().BeTrue();
        var bindEvent = childActor.LastHandledEnvelope.Payload.Unpack<BindWorkflowRunDefinitionEvent>();
        bindEvent.RunId.Should().Be("invoke-2");
        bindEvent.WorkflowName.Should().Be("sub flow");
        bindEvent.DefinitionActorId.Should().Be(definitionActorId);
        bindEvent.InlineWorkflowYamls.Should().ContainKey("sub flow");
    }

    [Fact]
    public async Task HandleInvokeRequestedAsync_WhenCreateRaces_ShouldReuseRacedChildActor()
    {
        const string childActorId = "owner-1:workflow:sub_flow";
        var racedActor = new RecordingActor(childActorId);
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.InlineWorkflowYamls["sub_flow"] = "name: sub_flow\nroles: []\nsteps: []\n";
        harness.Runtime.EnqueueGet(childActorId, null);
        harness.Runtime.EnqueueGet(childActorId, racedActor);
        harness.Runtime.FailCreateActorIds.Add(childActorId);

        await harness.Orchestrator.HandleInvokeRequestedAsync(
            new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = "invoke-race",
                ParentRunId = "parent-run",
                ParentStepId = "step-race",
                WorkflowName = "sub_flow",
                Lifecycle = WorkflowCallLifecycle.Singleton,
            },
            state,
            CancellationToken.None);

        racedActor.LastHandledEnvelope.Should().NotBeNull();
        racedActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowRunDefinitionEvent.Descriptor).Should().BeTrue();
        harness.Persisted.Should().Contain(x => x is SubWorkflowBindingUpsertedEvent);
        harness.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDefinitionResolveFailedAsync_WhenPendingResolutionExists_ShouldClearAndPublishFailure()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "invoke-failed",
            ParentRunId = "parent-run",
            ParentStepId = "step-failed",
            WorkflowName = "sub_flow",
            DefinitionActorId = "workflow-definition:sub_flow",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-failed"] = 0;

        await harness.Orchestrator.HandleDefinitionResolveFailedAsync(
            new SubWorkflowDefinitionResolveFailedEvent
            {
                InvocationId = "invoke-failed",
                Error = "definition lookup failed",
            },
            state,
            CancellationToken.None);

        harness.Persisted.Should().ContainSingle(x => x is SubWorkflowDefinitionResolutionClearedEvent);
        var failure = harness.Published.Should().ContainSingle().Subject.Message.Should().BeOfType<StepCompletedEvent>().Subject;
        failure.RunId.Should().Be("parent-run");
        failure.StepId.Should().Be("step-failed");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Be("definition lookup failed");
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenRunIdMissingOrUnknown_ShouldReturnFalse()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState();

        var missingRunId = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent { RunId = " " },
            "child-1",
            state,
            CancellationToken.None);
        var unknownRunId = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent { RunId = "child-404" },
            "child-1",
            state,
            CancellationToken.None);

        missingRunId.Should().BeFalse();
        unknownRunId.Should().BeFalse();
        harness.Persisted.Should().BeEmpty();
        harness.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenPublisherMismatch_ShouldReturnTrueWithoutCompleting()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-1",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            ChildActorId = "child-1",
            ChildRunId = "child-run-1",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        var handled = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent
            {
                RunId = "child-run-1",
                Success = true,
                Output = "done",
            },
            "child-2",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.Should().BeEmpty();
        harness.Published.Should().BeEmpty();
        harness.Runtime.Destroyed.Should().BeEmpty();
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenTransientChildCompletes_ShouldPublishParentCompletionAndCleanup()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-2",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            ChildActorId = "child-transient",
            ChildRunId = "child-run-2",
            Lifecycle = WorkflowCallLifecycle.Transient,
        });

        var handled = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent
            {
                RunId = "child-run-2",
                Success = true,
                Output = "child-output",
            },
            "child-transient",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>().Should().ContainSingle(x =>
            x.InvocationId == "invoke-2" &&
            x.Success);
        var parentCompletion = harness.Published.Single().Message.Should().BeOfType<StepCompletedEvent>().Subject;
        parentCompletion.StepId.Should().Be("step-b");
        parentCompletion.RunId.Should().Be("parent-run");
        parentCompletion.Annotations["workflow_call.child_actor_id"].Should().Be("child-transient");
        parentCompletion.Annotations["workflow_call.child_run_id"].Should().Be("child-run-2");
        harness.Runtime.Unlinked.Should().ContainSingle("child-transient");
        harness.Runtime.Destroyed.Should().ContainSingle("child-transient");
    }

    [Fact]
    public async Task TryHandleCompletionAsync_WhenChildActorIdMissing_ShouldCompleteWithoutCleanup()
    {
        var harness = CreateHarness();
        var state = BuildStateWithPending(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-3",
            ParentRunId = "parent-run",
            ParentStepId = "step-c",
            WorkflowName = "sub_flow",
            ChildActorId = " ",
            ChildRunId = "child-run-3",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        var handled = await harness.Orchestrator.TryHandleCompletionAsync(
            new WorkflowCompletedEvent
            {
                RunId = "child-run-3",
                Success = false,
                Error = "failed",
            },
            "publisher-ignored",
            state,
            CancellationToken.None);

        handled.Should().BeTrue();
        harness.Persisted.OfType<SubWorkflowInvocationCompletedEvent>().Should().ContainSingle(x =>
            !x.Success);
        harness.Published.Should().ContainSingle();
        harness.Runtime.Unlinked.Should().BeEmpty();
        harness.Runtime.Destroyed.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupPendingInvocationsForRunAsync_ShouldPersistCompletions_AndCleanupOnlyNonSingletonChildren()
    {
        var harness = CreateHarness();
        var state = new WorkflowRunState();
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "indexed-singleton",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            ChildActorId = "child-singleton",
            ChildRunId = "child-run-a",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-run-a"] = 0;
        state.PendingChildRunIdsByParentRunId["parent-run"] = new WorkflowRunState.Types.ChildRunIdSet
        {
            ChildRunIds = { "child-run-a" },
        };
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "scan-transient",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            ChildActorId = "child-transient",
            ChildRunId = "child-run-b",
            Lifecycle = WorkflowCallLifecycle.Transient,
        });

        await harness.Orchestrator.CleanupPendingInvocationsForRunAsync(" parent-run ", state, CancellationToken.None);

        harness.Persisted.Should().HaveCount(2);
        harness.Persisted.Should().OnlyContain(x => x is SubWorkflowInvocationCompletedEvent);
        harness.Runtime.Unlinked.Should().ContainSingle("child-transient");
        harness.Runtime.Destroyed.Should().ContainSingle("child-transient");
        harness.Runtime.Unlinked.Should().NotContain("child-singleton");
    }

    [Fact]
    public void ApplyStateTransitions_ShouldMaintainBindingAndInvocationIndexes()
    {
        var state = new WorkflowRunState();
        state = SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted(state, new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = "sub_flow",
            ChildActorId = "child-1",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 1,
        });
        state = SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted(state, new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = "sub_flow",
            ChildActorId = "child-2",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 2,
        });
        state = SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted(state, new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = " ",
            ChildActorId = "child-ignored",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        state.SubWorkflowBindings.Should().ContainSingle();
        state.SubWorkflowBindings.Single().ChildActorId.Should().Be("child-2");
        state.SubWorkflowBindings.Single().DefinitionVersion.Should().Be(2);

        state = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered(state, new SubWorkflowDefinitionResolutionRegisteredEvent
        {
            InvocationId = "invoke-a",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            DefinitionActorId = "workflow-definition:sub_flow",
            Input = "payload-a",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.PendingSubWorkflowDefinitionResolutions.Should().ContainSingle(x => x.InvocationId == "invoke-a");
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-a"].Should().Be(0);

        state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state, new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = "invoke-a",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            ChildActorId = "child-2",
            ChildRunId = "child-run-a",
            Lifecycle = WorkflowCallLifecycle.Singleton,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 2,
        });
        state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state, new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = "invoke-b",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            ChildActorId = "child-3",
            ChildRunId = "child-run-b",
            Lifecycle = WorkflowCallLifecycle.Transient,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 2,
        });
        state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state, new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = "invoke-a",
            ParentRunId = "parent-run",
            ParentStepId = "step-a2",
            WorkflowName = "sub_flow",
            ChildActorId = "child-4",
            ChildRunId = "child-run-a",
            Lifecycle = WorkflowCallLifecycle.Scope,
            DefinitionActorId = "workflow-definition:sub_flow",
            DefinitionVersion = 2,
        });

        state.PendingSubWorkflowInvocations.Should().HaveCount(2);
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().NotContainKey("invoke-a");
        state.PendingSubWorkflowDefinitionResolutions.Should().BeEmpty();
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-run-a"].Should().BeGreaterThanOrEqualTo(0);
        state.PendingChildRunIdsByParentRunId["parent-run"].ChildRunIds.Should().Contain(["child-run-a", "child-run-b"]);

        state = SubWorkflowOrchestrator.ApplySubWorkflowInvocationCompleted(state, new SubWorkflowInvocationCompletedEvent
        {
            InvocationId = "invoke-a",
            ChildRunId = "child-run-a",
        });

        state.PendingSubWorkflowInvocations.Should().ContainSingle(x => x.ChildRunId == "child-run-b");
        state.PendingSubWorkflowInvocationIndexByChildRunId.Should().ContainKey("child-run-b");
        state.PendingSubWorkflowInvocationIndexByChildRunId.Should().NotContainKey("child-run-a");
        state.PendingChildRunIdsByParentRunId["parent-run"].ChildRunIds.Should().ContainSingle(x => x == "child-run-b");
    }

    [Fact]
    public void PruneIdleSubWorkflowBindings_ShouldKeepReferencedAndPendingSingletons()
    {
        var state = new WorkflowRunState();
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_ref",
            ChildActorId = "child-ref",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_nested",
            ChildActorId = "child-nested",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_pending",
            ChildActorId = "child-pending",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_idle",
            ChildActorId = "child-idle",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = "wf_transient",
            ChildActorId = "child-transient",
            Lifecycle = WorkflowCallLifecycle.Transient,
        });
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-pending",
            ParentRunId = "parent-run",
            ParentStepId = "step-pending",
            WorkflowName = "wf_pending",
            ChildActorId = "child-pending",
            ChildRunId = "child-run-pending",
            Lifecycle = WorkflowCallLifecycle.Singleton,
        });

        var workflow = new WorkflowDefinition
        {
            Name = "wf-parent",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "call-root",
                    Type = "workflow_call",
                    Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["workflow"] = "wf_ref",
                        ["lifecycle"] = WorkflowCallLifecycle.Singleton,
                    },
                    Children =
                    [
                        new StepDefinition
                        {
                            Id = "call-nested",
                            Type = "workflow_call",
                            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["workflow"] = "wf_nested",
                                ["lifecycle"] = WorkflowCallLifecycle.Singleton,
                            },
                        },
                    ],
                },
            ],
        };

        SubWorkflowOrchestrator.PruneIdleSubWorkflowBindings(state, workflow);

        state.SubWorkflowBindings.Select(x => x.WorkflowName).Should().BeEquivalentTo("wf_ref", "wf_nested", "wf_pending");
    }

    private static OrchestratorHarness CreateHarness()
    {
        var runtime = new RecordingActorRuntime();
        var persisted = new List<IMessage>();
        var published = new List<PublishedMessage>();
        var sent = new List<SentMessage>();

        var orchestrator = new SubWorkflowOrchestrator(
            runtime,
            runtime,
            () => "owner-1",
            () => NullLogger.Instance,
            (evt, _) =>
            {
                persisted.Add(evt);
                return Task.CompletedTask;
            },
            (events, _) =>
            {
                persisted.AddRange(events);
                return Task.CompletedTask;
            },
            (evt, direction, _) =>
            {
                published.Add(new PublishedMessage(evt, direction));
                return Task.CompletedTask;
            },
            (targetActorId, evt, _) =>
            {
                sent.Add(new SentMessage(targetActorId, evt));
                return Task.CompletedTask;
            });

        return new OrchestratorHarness(orchestrator, runtime, persisted, published, sent);
    }

    private static WorkflowRunState BuildStateWithPending(
        params WorkflowRunState.Types.PendingSubWorkflowInvocation[] pendingInvocations)
    {
        var state = new WorkflowRunState();
        for (var i = 0; i < pendingInvocations.Length; i++)
        {
            var pending = pendingInvocations[i];
            state.PendingSubWorkflowInvocations.Add(pending);
            if (!string.IsNullOrWhiteSpace(pending.ChildRunId))
                state.PendingSubWorkflowInvocationIndexByChildRunId[pending.ChildRunId] = i;

            if (!string.IsNullOrWhiteSpace(pending.ParentRunId) &&
                !string.IsNullOrWhiteSpace(pending.ChildRunId))
            {
                if (!state.PendingChildRunIdsByParentRunId.TryGetValue(pending.ParentRunId, out var childRuns))
                {
                    childRuns = new WorkflowRunState.Types.ChildRunIdSet();
                    state.PendingChildRunIdsByParentRunId[pending.ParentRunId] = childRuns;
                }

                childRuns.ChildRunIds.Add(pending.ChildRunId);
            }
        }

        return state;
    }

    private sealed record OrchestratorHarness(
        SubWorkflowOrchestrator Orchestrator,
        RecordingActorRuntime Runtime,
        List<IMessage> Persisted,
        List<PublishedMessage> Published,
        List<SentMessage> Sent);

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        private readonly Dictionary<string, Queue<IActor?>> _queuedGets = new(StringComparer.Ordinal);
        private int _createdCount;

        public Dictionary<string, RecordingActor> StoredActors { get; } = new(StringComparer.Ordinal);

        public List<(global::System.Type AgentType, string? RequestedId)> CreateRequests { get; } = [];

        public List<(string ParentId, string ChildId)> Linked { get; } = [];

        public List<string> Unlinked { get; } = [];

        public List<string> Destroyed { get; } = [];

        public HashSet<string> FailCreateActorIds { get; } = new(StringComparer.Ordinal);

        public void EnqueueGet(string actorId, IActor? actor)
        {
            if (!_queuedGets.TryGetValue(actorId, out var queue))
            {
                queue = new Queue<IActor?>();
                _queuedGets[actorId] = queue;
            }

            queue.Enqueue(actor);
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var resolvedId = id ?? $"created-{++_createdCount}";
            CreateRequests.Add((agentType, resolvedId));
            if (FailCreateActorIds.Contains(resolvedId))
                throw new InvalidOperationException($"create failed for {resolvedId}");

            var actor = new RecordingActor(resolvedId);
            StoredActors[resolvedId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Destroyed.Add(id);
            StoredActors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            if (_queuedGets.TryGetValue(id, out var queue) && queue.Count > 0)
            {
                var queuedActor = queue.Dequeue();
                if (queuedActor is RecordingActor recordingActor)
                    StoredActors[id] = recordingActor;

                return Task.FromResult(queuedActor);
            }

            return Task.FromResult<IActor?>(StoredActors.TryGetValue(id, out var actor) ? actor : null);
        }

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actor = await GetAsync(actorId) ?? throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(StoredActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Linked.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Unlinked.Add(childId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new StubAgent(id + ":agent");

        public EventEnvelope? LastHandledEnvelope { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastHandledEnvelope = envelope;
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<global::System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<global::System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record PublishedMessage(
        IMessage Message,
        TopologyAudience Direction);

    private sealed record SentMessage(
        string TargetActorId,
        IMessage Message);
}
