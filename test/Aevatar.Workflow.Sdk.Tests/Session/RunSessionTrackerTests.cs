using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;
using Aevatar.Workflow.Sdk.Session;
using FluentAssertions;

namespace Aevatar.Workflow.Sdk.Tests.Session;

public sealed class RunSessionTrackerTests
{
    [Fact]
    public void Track_ShouldCaptureContextAndBuildResumeAndSignalRequests()
    {
        var tracker = new RunSessionTracker();

        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = "aevatar.run.context",
            Value = ParseObject("""{"actorId":"actor-1","workflowName":"auto","commandId":"cmd-1"}"""),
        });
        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = "aevatar.human_input.request",
            Value = ParseObject("""{"runId":"run-1","stepId":"approval-1","suspensionType":"human_approval"}"""),
        });
        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = "aevatar.workflow.waiting_signal",
            Value = ParseObject("""{"runId":"run-1","stepId":"wait-1","signalName":"ops_window_open"}"""),
        });

        var snapshot = tracker.Snapshot;
        snapshot.ActorId.Should().Be("actor-1");
        snapshot.CommandId.Should().Be("cmd-1");
        snapshot.RunId.Should().Be("run-1");
        snapshot.StepId.Should().Be("wait-1");
        snapshot.LastSignalName.Should().Be("ops_window_open");

        var resume = tracker.CreateResumeRequest("scope-a", approved: true, userInput: "approved", serviceId: "orders");
        resume.ScopeId.Should().Be("scope-a");
        resume.ServiceId.Should().Be("orders");
        resume.ActorId.Should().Be("actor-1");
        resume.RunId.Should().Be("run-1");
        resume.StepId.Should().Be("wait-1");
        resume.CommandId.Should().Be("cmd-1");

        var signal = tracker.CreateSignalRequest("scope-a", payload: "window=open", serviceId: "orders");
        signal.ScopeId.Should().Be("scope-a");
        signal.ServiceId.Should().Be("orders");
        signal.ActorId.Should().Be("actor-1");
        signal.RunId.Should().Be("run-1");
        signal.SignalName.Should().Be("ops_window_open");
        signal.StepId.Should().Be("wait-1");
        signal.CommandId.Should().Be("cmd-1");
    }

    [Fact]
    public void CreateSignalRequest_ShouldAllowExplicitStepOverride()
    {
        var tracker = new RunSessionTracker();
        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = "aevatar.run.context",
            Value = ParseObject("""{"actorId":"actor-1","workflowName":"auto","commandId":"cmd-1"}"""),
        });
        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = "aevatar.workflow.waiting_signal",
            Value = ParseObject("""{"runId":"run-1","stepId":"wait-1","signalName":"ops_window_open"}"""),
        });

        var signal = tracker.CreateSignalRequest(
            "scope-a",
            payload: "window=open",
            stepId: "wait-override",
            serviceId: "orders");

        signal.StepId.Should().Be("wait-override");
        signal.SignalName.Should().Be("ops_window_open");
    }

    [Fact]
    public void Track_ShouldSupportPascalCasePayload()
    {
        var tracker = new RunSessionTracker();

        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = "aevatar.run.context",
            Value = ParseObject("""{"ActorId":"actor-p","WorkflowName":"auto","CommandId":"cmd-p"}"""),
        });
        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = "aevatar.step.request",
            Value = ParseObject("""{"RunId":"run-p","StepId":"step-p"}"""),
        });

        tracker.Snapshot.ActorId.Should().Be("actor-p");
        tracker.Snapshot.RunId.Should().Be("run-p");
        tracker.Snapshot.StepId.Should().Be("step-p");
    }

    [Fact]
    public void CreateResumeRequest_WhenContextIncomplete_ShouldThrowInvalidRequest()
    {
        var tracker = new RunSessionTracker();
        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = "aevatar.run.context",
            Value = ParseObject("""{"actorId":"actor-1","commandId":"cmd-1"}"""),
        });

        var act = () => tracker.CreateResumeRequest("scope-a", approved: true, serviceId: "orders");
        var ex = act.Should().Throw<AevatarWorkflowException>();
        ex.Which.Kind.Should().Be(AevatarWorkflowErrorKind.InvalidRequest);
    }

    [Fact]
    public void Track_ShouldCaptureSignalContextFromBufferedEvents()
    {
        var tracker = new RunSessionTracker();
        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = WorkflowCustomEventNames.RunContext,
            Value = ParseObject("""{"actorId":"actor-1","workflowName":"auto","commandId":"cmd-1"}"""),
        });
        tracker.Track(new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.Custom,
            Name = WorkflowCustomEventNames.SignalBuffered,
            Value = ParseObject("""{"runId":"run-buf","stepId":"wait-buf","signalName":"buffered_ready"}"""),
        });

        tracker.Snapshot.RunId.Should().Be("run-buf");
        tracker.Snapshot.StepId.Should().Be("wait-buf");
        tracker.Snapshot.LastSignalName.Should().Be("buffered_ready");
    }

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
