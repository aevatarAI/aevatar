using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowRunStartRecoveryContractTests
{
    [Fact]
    public void PendingWorkflowStart_ShouldUseStableTypedProtoFields()
    {
        var startedField = WorkflowRunExecutionStartedEvent.Descriptor
            .FindFieldByName("pending_start_workflow");
        var stateField = WorkflowRunState.Descriptor
            .FindFieldByName("pending_start_workflow");

        startedField.Should().NotBeNull();
        startedField.FieldNumber.Should().Be(15);
        startedField.MessageType.Should().Be(StartWorkflowEvent.Descriptor);
        stateField.Should().NotBeNull();
        stateField.FieldNumber.Should().Be(65);
        stateField.MessageType.Should().Be(StartWorkflowEvent.Descriptor);
    }
}
