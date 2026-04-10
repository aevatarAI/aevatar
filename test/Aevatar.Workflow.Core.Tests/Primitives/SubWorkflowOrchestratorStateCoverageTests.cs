using System.Reflection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class SubWorkflowOrchestratorStateCoverageTests
{
    private static readonly MethodInfo TryResolveInlineWorkflowDefinitionSnapshotMethod = typeof(SubWorkflowOrchestrator)
        .GetMethod("TryResolveInlineWorkflowDefinitionSnapshot", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryResolveInlineWorkflowDefinitionSnapshot not found.");

    private static readonly MethodInfo ValidateDefinitionSnapshotOrThrowMethod = typeof(SubWorkflowOrchestrator)
        .GetMethod("ValidateDefinitionSnapshotOrThrow", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ValidateDefinitionSnapshotOrThrow not found.");

    [Fact]
    public void ApplySubWorkflowDefinitionResolutionRegistered_ShouldReplaceExistingEntry_AndPreserveDedicatedLease()
    {
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "invoke-a",
            WorkflowName = "old_flow",
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-a"] = 0;

        var next = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered(state,
            new SubWorkflowDefinitionResolutionRegisteredEvent
            {
                InvocationId = " invoke-a ",
                ParentRunId = " parent-run ",
                ParentStepId = " step-a ",
                WorkflowName = " child_flow ",
                DefinitionActorId = " definition-actor ",
                Input = "payload",
                Lifecycle = WorkflowCallLifecycle.Singleton,
                TimeoutCallbackId = " callback-1 ",
                TimeoutCallbackActorId = " owner-1 ",
                TimeoutCallbackGeneration = 7,
                TimeoutCallbackBackend = (int)WorkflowRuntimeCallbackBackendState.Dedicated,
                TimeoutMs = 12_000,
            });

        next.PendingSubWorkflowDefinitionResolutions.Should().ContainSingle();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-a"].Should().Be(0);

        var pending = next.PendingSubWorkflowDefinitionResolutions[0];
        pending.ParentRunId.Should().Be("parent-run");
        pending.ParentStepId.Should().Be("step-a");
        pending.WorkflowName.Should().Be("child_flow");
        pending.DefinitionActorId.Should().Be("definition-actor");
        pending.TimeoutCallbackId.Should().Be("callback-1");
        pending.TimeoutLease.Should().NotBeNull();
        pending.TimeoutLease!.ActorId.Should().Be("owner-1");
        pending.TimeoutLease.Backend.Should().Be(WorkflowRuntimeCallbackBackendState.Dedicated);
        pending.TimeoutMs.Should().Be(12_000);
    }

    [Fact]
    public void ApplySubWorkflowDefinitionResolutionRegistered_ShouldIgnoreBlankInvocation_AndAllowMissingLease()
    {
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "keep-me",
            WorkflowName = "child_flow",
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["keep-me"] = 0;

        var unchanged = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered(state,
            new SubWorkflowDefinitionResolutionRegisteredEvent
            {
                InvocationId = " ",
                WorkflowName = "ignored",
            });

        unchanged.PendingSubWorkflowDefinitionResolutions.Should().ContainSingle(x => x.InvocationId == "keep-me");

        var next = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered(state,
            new SubWorkflowDefinitionResolutionRegisteredEvent
            {
                InvocationId = "invoke-b",
                ParentRunId = "parent-run",
                ParentStepId = "step-b",
                WorkflowName = "child_flow",
                Lifecycle = WorkflowCallLifecycle.Transient,
                TimeoutCallbackId = " ",
            });

        next.PendingSubWorkflowDefinitionResolutions.Should().Contain(x => x.InvocationId == "invoke-b");
        next.PendingSubWorkflowDefinitionResolutions.Single(x => x.InvocationId == "invoke-b")
            .TimeoutLease.Should().BeNull();
    }

    [Fact]
    public void ApplySubWorkflowDefinitionResolutionCleared_ShouldRemoveIndexedResolution()
    {
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "invoke-a",
        });
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "invoke-b",
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-a"] = 0;
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-b"] = 1;

        var next = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionCleared(state,
            new SubWorkflowDefinitionResolutionClearedEvent
            {
                InvocationId = "invoke-a",
            });

        next.PendingSubWorkflowDefinitionResolutions.Should().ContainSingle(x => x.InvocationId == "invoke-b");
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().ContainKey("invoke-b");
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().NotContainKey("invoke-a");

        var unchanged = SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionCleared(next,
            new SubWorkflowDefinitionResolutionClearedEvent
            {
                InvocationId = "missing",
            });
        unchanged.PendingSubWorkflowDefinitionResolutions.Should().ContainSingle(x => x.InvocationId == "invoke-b");
    }

    [Fact]
    public void ApplySubWorkflowInvocationRegistered_ShouldDeduplicateByInvocationAndChildRun()
    {
        var state = new WorkflowRunState();
        state.PendingSubWorkflowDefinitionResolutions.Add(new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = "invoke-a",
        });
        state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId["invoke-a"] = 0;
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-old",
            ParentRunId = "parent-run",
            ChildRunId = "child-run-a",
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-run-a"] = 0;
        state.PendingChildRunIdsByParentRunId["parent-run"] = new WorkflowRunState.Types.ChildRunIdSet
        {
            ChildRunIds = { "child-run-a" },
        };

        var next = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state,
            new SubWorkflowInvocationRegisteredEvent
            {
                InvocationId = "invoke-a",
                ParentRunId = " parent-run ",
                ParentStepId = " step-a ",
                WorkflowName = " child_flow ",
                ChildActorId = " child-actor ",
                ChildRunId = " child-run-a ",
                Lifecycle = WorkflowCallLifecycle.Scope,
                DefinitionActorId = " definition-actor ",
                DefinitionVersion = 3,
            });

        next.PendingSubWorkflowDefinitionResolutions.Should().BeEmpty();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Should().BeEmpty();
        next.PendingSubWorkflowInvocations.Should().ContainSingle();
        next.PendingSubWorkflowInvocationIndexByChildRunId["child-run-a"].Should().Be(0);
        next.PendingChildRunIdsByParentRunId["parent-run"].ChildRunIds.Should().ContainSingle("child-run-a");

        var pending = next.PendingSubWorkflowInvocations[0];
        pending.InvocationId.Should().Be("invoke-a");
        pending.ParentStepId.Should().Be("step-a");
        pending.WorkflowName.Should().Be("child_flow");
        pending.ChildActorId.Should().Be("child-actor");
        pending.DefinitionActorId.Should().Be("definition-actor");
        pending.DefinitionVersion.Should().Be(3);
    }

    [Fact]
    public void ApplySubWorkflowInvocationRegistered_ShouldIgnoreIncompletePayload_AndCompletionShouldRemoveByChildRun()
    {
        var state = new WorkflowRunState();
        state.PendingSubWorkflowInvocations.Add(new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-a",
            ParentRunId = "parent-run",
            ChildRunId = "child-run-a",
        });
        state.PendingSubWorkflowInvocationIndexByChildRunId["child-run-a"] = 0;
        state.PendingChildRunIdsByParentRunId["parent-run"] = new WorkflowRunState.Types.ChildRunIdSet
        {
            ChildRunIds = { "child-run-a" },
        };

        var unchanged = SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered(state,
            new SubWorkflowInvocationRegisteredEvent
            {
                InvocationId = "invoke-b",
                ChildRunId = " ",
            });
        unchanged.PendingSubWorkflowInvocations.Should().ContainSingle(x => x.InvocationId == "invoke-a");

        var cleared = SubWorkflowOrchestrator.ApplySubWorkflowInvocationCompleted(state,
            new SubWorkflowInvocationCompletedEvent
            {
                ChildRunId = "child-run-a",
            });

        cleared.PendingSubWorkflowInvocations.Should().BeEmpty();
        cleared.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
        cleared.PendingChildRunIdsByParentRunId.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveInlineWorkflowDefinitionSnapshot_ShouldMatchCaseInsensitive_AndCopyInlineWorkflows()
    {
        var state = new WorkflowRunState
        {
            ScopeId = "scope-a",
        };
        state.InlineWorkflowYamls["child_flow"] = ValidWorkflowYaml("child_flow");
        state.InlineWorkflowYamls["helper_flow"] = ValidWorkflowYaml("helper_flow");

        var snapshot = InvokePrivateStatic<WorkflowDefinitionSnapshot?>(
            TryResolveInlineWorkflowDefinitionSnapshotMethod,
            "CHILD_FLOW",
            state);

        snapshot.Should().NotBeNull();
        snapshot!.WorkflowName.Should().Be("CHILD_FLOW");
        snapshot.ScopeId.Should().Be("scope-a");
        snapshot.DefinitionActorId.Should().BeEmpty();
        snapshot.InlineWorkflowYamls.Should().ContainKey("child_flow");
        snapshot.InlineWorkflowYamls.Should().ContainKey("helper_flow");
    }

    [Fact]
    public void ValidateDefinitionSnapshotOrThrow_ShouldRejectMissingYaml_AndNameMismatch()
    {
        var missingYaml = new WorkflowDefinitionSnapshot
        {
            WorkflowName = "child_flow",
            WorkflowYaml = " ",
        };

        FluentActions.Invoking(() => InvokePrivateStatic<object?>(
                ValidateDefinitionSnapshotOrThrowMethod,
                missingYaml))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*YAML is empty*");

        var mismatchedName = new WorkflowDefinitionSnapshot
        {
            WorkflowName = "child_flow",
            WorkflowYaml = ValidWorkflowYaml("other_flow"),
        };

        FluentActions.Invoking(() => InvokePrivateStatic<object?>(
                ValidateDefinitionSnapshotOrThrowMethod,
                mismatchedName))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*name mismatch*");
    }

    [Fact]
    public void ValidateDefinitionSnapshotOrThrow_ShouldWrapParseFailures()
    {
        var invalid = new WorkflowDefinitionSnapshot
        {
            WorkflowName = "child_flow",
            WorkflowYaml = "name: child_flow\nroles:\n  - id: worker\nsteps:\n  - id: broken\n    type: [",
        };

        FluentActions.Invoking(() => InvokePrivateStatic<object?>(
                ValidateDefinitionSnapshotOrThrowMethod,
                invalid))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("workflow_call*is invalid*");
    }

    private static T InvokePrivateStatic<T>(MethodInfo method, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(null, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static string ValidWorkflowYaml(string workflowName) =>
        $$"""
        name: {{workflowName}}
        roles:
          - id: assistant
            name: Assistant
        steps:
          - id: answer
            type: llm_call
            role: assistant
            parameters: {}
        """;
}
