using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Runtime;

public class WorkflowRunStatePatchAssemblerTests
{
    [Fact]
    public void BuildPatch_ShouldReturnNullWhenStateDidNotChange()
    {
        var state = new WorkflowRunState
        {
            WorkflowName = "demo",
            WorkflowYaml = "name: demo",
            Compiled = true,
            Status = "active",
            RunId = "run-1",
        };
        var assembler = CreateAssembler();

        var patch = assembler.BuildPatch(state, state.Clone());

        patch.Should().BeNull();
    }

    [Fact]
    public void BuildPatch_AndApplyPatch_ShouldPreserveCapabilitySlices()
    {
        var current = new WorkflowRunState
        {
            WorkflowName = "demo",
            WorkflowYaml = "name: demo",
            Compiled = true,
        };
        var next = current.Clone();
        next.RunId = "run-1";
        next.Status = "suspended";
        next.ActiveStepId = "wait-1";
        next.Variables["input"] = "hello";
        next.PendingHumanGates["wait-1"] = new WorkflowPendingHumanGateState
        {
            StepId = "wait-1",
            GateType = "human_input",
            ResumeToken = "resume-1",
            Prompt = "Please reply",
            TimeoutSeconds = 60,
        };
        next.PendingSignalWaits["signal-1"] = new WorkflowPendingSignalWaitState
        {
            StepId = "signal-1",
            SignalName = "approval",
            WaitToken = "wait-1",
            TimeoutMs = 5000,
        };

        var assembler = CreateAssembler();

        var patch = assembler.BuildPatch(current, next);

        patch.Should().NotBeNull();
        patch!.Lifecycle.Should().NotBeNull();
        patch.Variables.Should().NotBeNull();
        patch.PendingHumanGates.Should().NotBeNull();
        patch.PendingSignalWaits.Should().NotBeNull();

        var applied = assembler.ApplyPatch(current, patch);

        applied.RunId.Should().Be("run-1");
        applied.Status.Should().Be("suspended");
        applied.ActiveStepId.Should().Be("wait-1");
        applied.Variables.Should().ContainKey("input").WhoseValue.Should().Be("hello");
        applied.PendingHumanGates.Should().ContainKey("wait-1");
        applied.PendingSignalWaits.Should().ContainKey("signal-1");
    }

    private static WorkflowRunStatePatchAssembler CreateAssembler() =>
        new(
        [
            new WorkflowRunBindingPatchContributor(),
            new WorkflowRunLifecyclePatchContributor(),
            new WorkflowRunExecutionPatchContributor(),
            new WorkflowHumanInteractionPatchContributor(),
            new WorkflowControlFlowPatchContributor(),
        ]);
}
