using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf.Reflection;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowToolCallContinuationContractTests
{
    [Fact]
    public void ContinuationIdentifiers_ShouldPreservePersistedWireNumbers()
    {
        (MessageDescriptor Descriptor, int FieldNumber)[] contracts =
        [
            (WorkflowToolCallAttemptCompletedEvent.Descriptor, 9),
            (WorkflowToolCallTimeoutFiredEvent.Descriptor, 6),
            (WorkflowToolCallRetryFiredEvent.Descriptor, 6),
            (WorkflowToolCallExecutionRecoveryFiredEvent.Descriptor, 6),
            (PendingToolCallApprovalState.Descriptor, 21),
            (PendingToolCallExecutionState.Descriptor, 21),
        ];

        foreach (var (descriptor, fieldNumber) in contracts)
        {
            var continuationId = descriptor.FindFieldByName("continuation_id");
            continuationId.Should().NotBeNull();
            continuationId!.FieldNumber.Should().Be(fieldNumber);
            descriptor.FindFieldByName("continuation_token").Should().BeNull();
        }
    }
}
