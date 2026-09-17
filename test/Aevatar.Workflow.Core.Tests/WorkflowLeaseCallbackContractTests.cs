using Aevatar.Workflow.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowLeaseCallbackContractTests
{
    [Fact]
    public void ExpirationFenceIdentifier_ShouldPreservePersistedWireNumber()
    {
        var holderFenceId = WorkflowLeaseExpirationFiredEvent.Descriptor.FindFieldByName("holder_fence_id");

        holderFenceId.Should().NotBeNull();
        holderFenceId!.FieldNumber.Should().Be(2);
        WorkflowLeaseExpirationFiredEvent.Descriptor.FindFieldByName("holder_token").Should().BeNull();
    }
}
