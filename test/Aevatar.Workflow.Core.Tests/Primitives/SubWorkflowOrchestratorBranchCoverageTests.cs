using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class SubWorkflowOrchestratorBranchCoverageTests
{
    [Fact]
    public void ApplySubWorkflowBindingUpserted_ShouldMatchExistingDefinitionScopedBinding()
    {
        var state = new WorkflowRunState();
        state.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = string.Empty,
            ChildActorId = "child-old",
            Lifecycle = "scope",
            DefinitionActorId = "def-1",
            DefinitionVersion = 1
        });

        var next = SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted(state, new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = " ",
            ChildActorId = " child-new ",
            Lifecycle = "scope",
            DefinitionActorId = " def-1 ",
            DefinitionVersion = 9
        });

        next.SubWorkflowBindings.Should().ContainSingle();
        next.SubWorkflowBindings[0].ChildActorId.Should().Be("child-new");
        next.SubWorkflowBindings[0].DefinitionVersion.Should().Be(9);

        var unchanged = SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted(next, new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = "workflow-a",
            ChildActorId = " ",
            Lifecycle = "scope"
        });

        unchanged.SubWorkflowBindings.Should().ContainSingle();
        unchanged.SubWorkflowBindings[0].ChildActorId.Should().Be("child-new");
    }

    [Fact]
    public void ApplySubWorkflowDefinitionResolutionCleared_ShouldReindexMovedTail()
    {
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "inv-1"
        });
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "inv-2"
        });
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "inv-3"
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["inv-1"] = 0;
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["inv-2"] = 1;
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["inv-3"] = 2;

        var next = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionCleared(state, new SubWorkflowDefinitionResolutionClearedEvent
        {
            InvocationId = "inv-2"
        });

        next.PendingSubWorkflowDefinitionResolutions.Should().HaveCount(2);
        next.PendingSubWorkflowDefinitionResolutions.Select(x => x.InvocationId).Should().BeEquivalentTo(["inv-1", "inv-3"]);
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().Contain("inv-1", 0);
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().Contain("inv-3", 1);
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().NotContainKey("inv-2");
    }

    [Fact]
    public void ApplySubWorkflowInvocationCompleted_ShouldRemoveByChildRunAndInvocationAndCleanIndexes()
    {
        var state = new WorkflowRunState();
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "inv-1",
            ParentRunId = "parent-1",
            ChildRunId = "child-1"
        });
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "inv-2",
            ParentRunId = "parent-1",
            ChildRunId = "child-2"
        });
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "inv-1",
            ParentRunId = "parent-2",
            ChildRunId = "child-3"
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-1"] = 0;
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-2"] = 1;
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-3"] = 2;
        state.PendingChildRunIdsByParentRunId["parent-1"] = new WorkflowRunState.Types.ChildRunIdSet
        {
            ChildRunIds = { "child-1", "child-2" }
        };
        state.PendingChildRunIdsByParentRunId["parent-2"] = new WorkflowRunState.Types.ChildRunIdSet
        {
            ChildRunIds = { "child-3" }
        };

        var next = SubWorkflowOrchestrator.ApplySubWorkflowInvocationCompleted(state, new SubWorkflowInvocationCompletedEvent
        {
            InvocationId = "inv-1",
            ChildRunId = "child-2"
        });

        next.PendingSubWorkflowInvocations.Should().BeEmpty();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
        next.PendingChildRunIdsByParentRunId.Should().BeEmpty();
    }

    [Fact]
    public void ApplySubWorkflowInvocationRegistered_ShouldReplaceExistingInvocationAndChildRunMappings()
    {
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "inv-1"
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["inv-1"] = 0;
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "inv-1",
            ParentRunId = "parent-1",
            ChildRunId = "child-old"
        });
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "inv-2",
            ParentRunId = "parent-2",
            ChildRunId = "child-new"
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-old"] = 0;
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-new"] = 1;
        state.PendingChildRunIdsByParentRunId["parent-1"] = new WorkflowRunState.Types.ChildRunIdSet
        {
            ChildRunIds = { "child-old" }
        };
        state.PendingChildRunIdsByParentRunId["parent-2"] = new WorkflowRunState.Types.ChildRunIdSet
        {
            ChildRunIds = { "child-new" }
        };

        var next = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state, new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = "inv-1",
            ParentRunId = "parent-3",
            ParentStepId = "step-3",
            WorkflowName = "workflow-a",
            ChildActorId = "actor-3",
            ChildRunId = "child-new",
            Lifecycle = "scope",
            DefinitionActorId = "def-3",
            DefinitionVersion = 7
        });

        next.PendingSubWorkflowDefinitionResolutions.Should().BeEmpty();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().BeEmpty();
        next.PendingSubWorkflowInvocations.Should().ContainSingle();
        next.PendingSubWorkflowInvocations[0].InvocationId.Should().Be("inv-1");
        next.PendingSubWorkflowInvocations[0].ChildRunId.Should().Be("child-new");
        next.PendingSubWorkflowInvocations[0].ParentRunId.Should().Be("parent-3");
        next.PendingSubWorkflowInvocationIndexByChildRunId.Should().Contain("child-new", 0);
        next.PendingSubWorkflowInvocationIndexByChildRunId.Should().NotContainKey("child-old");
        next.PendingChildRunIdsByParentRunId.Should().ContainKey("parent-3");
        next.PendingChildRunIdsByParentRunId.Should().NotContainKey("parent-1");
        next.PendingChildRunIdsByParentRunId.Should().NotContainKey("parent-2");
    }
}
