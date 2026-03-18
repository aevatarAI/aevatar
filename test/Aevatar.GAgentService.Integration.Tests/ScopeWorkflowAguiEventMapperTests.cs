using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeWorkflowAguiEventMapperTests
{
    [Fact]
    public void TryMap_WhenRunStopped_ShouldEmitCustomStoppedEvent()
    {
        var mapped = ScopeWorkflowAguiEventMapper.TryMap(
            new WorkflowRunEventEnvelope
            {
                Timestamp = 123,
                RunStopped = new WorkflowRunStoppedEventPayload
                {
                    RunId = "run-1",
                    Reason = "manual",
                },
            },
            out var aguiEvent);

        mapped.Should().BeTrue();
        aguiEvent.Should().NotBeNull();
        aguiEvent!.Timestamp.Should().Be(123);
        aguiEvent.Custom.Should().NotBeNull();
        aguiEvent.Custom.Name.Should().Be("aevatar.run.stopped");
        aguiEvent.Custom.Payload.Should().NotBeNull();
        var payload = aguiEvent.Custom.Payload.Unpack<WorkflowRunStoppedEventPayload>();
        payload.RunId.Should().Be("run-1");
        payload.Reason.Should().Be("manual");
    }

    [Fact]
    public void TryMap_WhenRunFinishedMissingRunId_ShouldFallBackToThreadId()
    {
        var mapped = ScopeWorkflowAguiEventMapper.TryMap(
            new WorkflowRunEventEnvelope
            {
                Timestamp = 456,
                RunFinished = new WorkflowRunFinishedEventPayload
                {
                    ThreadId = "thread-1",
                },
            },
            out var aguiEvent);

        mapped.Should().BeTrue();
        aguiEvent.Should().NotBeNull();
        aguiEvent!.RunFinished.Should().NotBeNull();
        aguiEvent.RunFinished.ThreadId.Should().Be("thread-1");
        aguiEvent.RunFinished.RunId.Should().Be("thread-1");
    }
}
