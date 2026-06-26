using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowRunBoardContractBoundaryTests
{
    [Fact]
    public void WorkflowRunState_ShouldNotExposeBoardExecutionState()
    {
        WorkflowRunState.Descriptor.Fields.InDeclarationOrder()
            .Should().NotContain(field => field.Name == "board_execution");

        WorkflowStateReflection.Descriptor.MessageTypes.Select(type => type.Name)
            .Should().NotContain([
                "WorkflowRunBoardExecutionState",
                "WorkflowRunBoardNodeState",
                "WorkflowRunBoardSummaryState",
            ]);

        WorkflowStateReflection.Descriptor.EnumTypes.Select(type => type.Name)
            .Should().NotContain([
                "WorkflowRunBoardCompletionStatus",
                "WorkflowRunBoardNodeStatus",
            ]);
    }

    [Fact]
    public void WorkflowExecutionEvents_ShouldNotExposeBoardOnlyPayloadFields()
    {
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Select(type => type.Name)
            .Should().NotContain([
                "WorkflowStepBoardProgress",
                "WorkflowStepBoardSummary",
            ]);

        StepRequestEvent.Descriptor.Fields.InDeclarationOrder()
            .Select(field => field.Name)
            .Should().NotContain(["requested_at_unix_ms", "board_progress"]);

        StepCompletedEvent.Descriptor.Fields.InDeclarationOrder()
            .Select(field => field.Name)
            .Should().NotContain(["completed_at_unix_ms", "board_summary"]);

        WorkflowRunExecutionStartedEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().NotContain(field => field.Name == "started_at_unix_ms");

        WorkflowStoppedEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().NotContain(field => field.Name == "stopped_at_unix_ms");

        WorkflowSuspendedEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().NotContain(field => field.Name == "suspended_at_unix_ms");

        WorkflowRunStoppedEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().NotContain(field => field.Name == "stopped_at_unix_ms");

        WaitingForSignalEvent.Descriptor.Fields.InDeclarationOrder()
            .Should().NotContain(field => field.Name == "waiting_at_unix_ms");
    }
}
