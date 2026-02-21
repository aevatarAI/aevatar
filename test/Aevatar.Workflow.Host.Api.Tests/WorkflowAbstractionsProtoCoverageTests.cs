using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowAbstractionsProtoCoverageTests
{
    [Fact]
    public void StartWorkflowEvent_ShouldCloneAndRoundtrip()
    {
        var evt = new StartWorkflowEvent
        {
            WorkflowName = "wf",
            Input = "{\"a\":1}",
        };
        evt.Parameters["k"] = "v";

        var clone = evt.Clone();
        clone.Should().BeEquivalentTo(evt);

        var parsed = StartWorkflowEvent.Parser.ParseFrom(evt.ToByteArray());
        parsed.WorkflowName.Should().Be("wf");
        parsed.Input.Should().Contain("a");
        parsed.Parameters["k"].Should().Be("v");
    }

    [Fact]
    public void WorkflowCompletedEvent_ShouldMergeAndCompare()
    {
        var source = new WorkflowCompletedEvent
        {
            WorkflowName = "wf",
            Success = true,
            Output = "ok",
            Error = "",
        };

        var target = new WorkflowCompletedEvent();
        target.MergeFrom(source);

        target.Should().BeEquivalentTo(source);
        target.Equals(new WorkflowCompletedEvent { WorkflowName = "wf-2" }).Should().BeFalse();
    }

    [Fact]
    public void StepRequestAndCompletedEvents_ShouldRoundtripAndKeepMaps()
    {
        var request = new StepRequestEvent
        {
            StepId = "s1",
            StepType = "llm_call",
            Input = "hello",
            TargetRole = "assistant",
        };
        request.Parameters["temperature"] = "0.1";

        var completed = new StepCompletedEvent
        {
            StepId = "s1",
            Success = true,
            Output = "world",
            Error = "",
            WorkerId = "worker-1",
        };
        completed.Metadata["latency_ms"] = "12";

        var parsedRequest = StepRequestEvent.Parser.ParseFrom(request.ToByteArray());
        parsedRequest.StepType.Should().Be("llm_call");
        parsedRequest.Parameters["temperature"].Should().Be("0.1");

        var parsedCompleted = StepCompletedEvent.Parser.ParseFrom(completed.ToByteArray());
        parsedCompleted.WorkerId.Should().Be("worker-1");
        parsedCompleted.Metadata["latency_ms"].Should().Be("12");

        parsedCompleted.Clone().Should().BeEquivalentTo(parsedCompleted);
    }
}
