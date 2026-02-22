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

    [Fact]
    public void WorkflowEvents_ShouldSupportMergeHashToStringAndDescriptor()
    {
        var start = new StartWorkflowEvent
        {
            WorkflowName = "wf-merge",
            Input = "in",
        };
        start.Parameters["k"] = "v";

        var mergedStart = new StartWorkflowEvent();
        mergedStart.MergeFrom(start);
        mergedStart.Should().BeEquivalentTo(start);
        mergedStart.GetHashCode().Should().Be(start.GetHashCode());
        mergedStart.ToString().Should().Contain("workflowName");
        ((IMessage)mergedStart).Descriptor.Name.Should().Be(nameof(StartWorkflowEvent));

        var completed = new WorkflowCompletedEvent
        {
            WorkflowName = "wf-merge",
            Success = false,
            Output = "",
            Error = "boom",
        };
        var mergedCompleted = new WorkflowCompletedEvent();
        mergedCompleted.MergeFrom(completed);
        mergedCompleted.Should().BeEquivalentTo(completed);
        mergedCompleted.CalculateSize().Should().BeGreaterThan(0);
        mergedCompleted.ToString().Should().Contain("workflowName");
        ((IMessage)mergedCompleted).Descriptor.Name.Should().Be(nameof(WorkflowCompletedEvent));
        completed.Equals((object?)null).Should().BeFalse();

        var request = new StepRequestEvent
        {
            StepId = "s2",
            StepType = "transform",
            Input = "x",
            TargetRole = "worker",
        };
        request.Parameters["op"] = "uppercase";
        var mergedRequest = new StepRequestEvent();
        mergedRequest.MergeFrom(request);
        mergedRequest.Should().BeEquivalentTo(request);
        mergedRequest.GetHashCode().Should().Be(request.GetHashCode());
        mergedRequest.ToString().Should().Contain("stepId");
        ((IMessage)mergedRequest).Descriptor.Name.Should().Be(nameof(StepRequestEvent));
        request.Equals((object?)null).Should().BeFalse();

        var stepCompleted = new StepCompletedEvent
        {
            StepId = "s2",
            Success = false,
            Output = "",
            Error = "err",
            WorkerId = "w2",
        };
        stepCompleted.Metadata["m"] = "n";
        var mergedStepCompleted = new StepCompletedEvent();
        mergedStepCompleted.MergeFrom(stepCompleted);
        mergedStepCompleted.Should().BeEquivalentTo(stepCompleted);
        mergedStepCompleted.CalculateSize().Should().BeGreaterThan(0);
        mergedStepCompleted.ToString().Should().Contain("stepId");
        ((IMessage)mergedStepCompleted).Descriptor.Name.Should().Be(nameof(StepCompletedEvent));
        stepCompleted.Equals((object?)null).Should().BeFalse();
    }

    [Fact]
    public void WorkflowEvents_ShouldValidateNullAssignments()
    {
        var start = new StartWorkflowEvent();
        var completed = new WorkflowCompletedEvent();
        var request = new StepRequestEvent();
        var stepCompleted = new StepCompletedEvent();

        Action setStartWorkflowName = () => start.WorkflowName = null!;
        Action setCompletedOutput = () => completed.Output = null!;
        Action setRequestStepId = () => request.StepId = null!;
        Action setStepCompletedWorker = () => stepCompleted.WorkerId = null!;

        setStartWorkflowName.Should().Throw<ArgumentNullException>();
        setCompletedOutput.Should().Throw<ArgumentNullException>();
        setRequestStepId.Should().Throw<ArgumentNullException>();
        setStepCompletedWorker.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WorkflowAbstractionsReflection_ShouldExposeAllMessages()
    {
        WorkflowExecutionMessagesReflection.Descriptor.Should().NotBeNull();
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(StartWorkflowEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(WorkflowCompletedEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(StepRequestEvent));
        WorkflowExecutionMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(StepCompletedEvent));
    }
}
