using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowRuntimeCallbackLeaseStateCodecTests
{
    [Fact]
    public void ToStateAndRuntime_ShouldPreserveSlotEpoch()
    {
        var lease = new RuntimeCallbackLease(
            "actor-1",
            "callback-1",
            1,
            RuntimeCallbackBackend.Dedicated)
        {
            SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
        };

        var state = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        state.Should().NotBeNull();
        state!.SlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.OrleansSchedulerV2);

        var restored = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(state);
        restored.Should().NotBeNull();
        restored!.SlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.OrleansSchedulerV2);
        restored.Generation.Should().Be(1);
        restored.CallbackId.Should().Be("callback-1");
    }
}
