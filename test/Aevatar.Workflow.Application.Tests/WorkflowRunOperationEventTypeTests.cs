using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunOperationEventTypeTests
{
    [Fact]
    public void GetEventType_ShouldMapModelOperationLifecycleEvents()
    {
        WorkflowRunEventTypes.GetEventType(new WorkflowRunEventEnvelope
            {
                ModelCallStart = new WorkflowModelCallStartEventPayload(),
            })
            .Should().Be(WorkflowRunEventTypes.ModelCallStart);
        WorkflowRunEventTypes.GetEventType(new WorkflowRunEventEnvelope
            {
                ModelCallEnd = new WorkflowModelCallEndEventPayload(),
            })
            .Should().Be(WorkflowRunEventTypes.ModelCallEnd);
    }
}
