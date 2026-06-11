using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;
using Aevatar.Workflow.Sdk.Session;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Sdk.Tests.Session;

public sealed class RunSessionTrackerTests
{
    [Fact]
    public void Track_ShouldCaptureContextAndBuildResumeAndSignalRequests()
    {
        var tracker = new RunSessionTracker();

        tracker.Track(CustomFrame("aevatar.run.context", new WorkflowRunContextPayload
        {
            ActorId = "actor-1",
            WorkflowName = "auto",
            CommandId = "cmd-1",
        }));
        tracker.Track(CustomFrame("aevatar.human_input.request", new WorkflowHumanInputRequestCustomPayload
        {
            RunId = "run-1",
            StepId = "approval-1",
            SuspensionType = "human_approval",
        }));
        tracker.Track(CustomFrame("aevatar.workflow.waiting_signal", new WorkflowWaitingSignalCustomPayload
        {
            RunId = "run-1",
            StepId = "wait-1",
            SignalName = "ops_window_open",
        }));

        var snapshot = tracker.Snapshot;
        snapshot.ActorId.Should().Be("actor-1");
        snapshot.CommandId.Should().Be("cmd-1");
        snapshot.RunId.Should().Be("run-1");
        snapshot.StepId.Should().Be("wait-1");
        snapshot.LastSignalName.Should().Be("ops_window_open");

        var resume = tracker.CreateResumeRequest(
            "scope-a",
            approved: true,
            userInput: "approved",
            editedContent: "approved draft",
            feedback: "ship it",
            serviceId: "orders");
        resume.ScopeId.Should().Be("scope-a");
        resume.ServiceId.Should().Be("orders");
        resume.ActorId.Should().Be("actor-1");
        resume.RunId.Should().Be("run-1");
        resume.StepId.Should().Be("wait-1");
        // Refactor (issue1326): Session tracking keeps the start command id for observation, but does not reuse it for resume.
        resume.CommandId.Should().BeNull();
        resume.EditedContent.Should().Be("approved draft");
        resume.Feedback.Should().Be("ship it");

        var signal = tracker.CreateSignalRequest("scope-a", payload: "window=open", serviceId: "orders");
        signal.ScopeId.Should().Be("scope-a");
        signal.ServiceId.Should().Be("orders");
        signal.ActorId.Should().Be("actor-1");
        signal.RunId.Should().Be("run-1");
        signal.SignalName.Should().Be("ops_window_open");
        signal.StepId.Should().Be("wait-1");
        signal.CommandId.Should().BeNull();
    }

    [Fact]
    public void CreateResumeAndSignalRequest_ShouldUseExplicitCommandIdOnly()
    {
        var tracker = new RunSessionTracker();

        tracker.Track(CustomFrame("aevatar.run.context", new WorkflowRunContextPayload
        {
            ActorId = "actor-1",
            WorkflowName = "auto",
            CommandId = "start-cmd",
        }));
        tracker.Track(CustomFrame("aevatar.human_input.request", new WorkflowHumanInputRequestCustomPayload
        {
            RunId = "run-1",
            StepId = "approval-1",
        }));
        tracker.Track(CustomFrame("aevatar.workflow.waiting_signal", new WorkflowWaitingSignalCustomPayload
        {
            RunId = "run-1",
            StepId = "wait-1",
            SignalName = "ops_window_open",
        }));

        var resume = tracker.CreateResumeRequest(
            "scope-a",
            approved: true,
            commandId: "resume-cmd",
            serviceId: "orders");
        var signal = tracker.CreateSignalRequest(
            "scope-a",
            commandId: "signal-cmd",
            serviceId: "orders");

        // Refactor (issue1326): Explicit control command ids remain caller owned; tracked start id is never a fallback.
        resume.CommandId.Should().Be("resume-cmd");
        signal.CommandId.Should().Be("signal-cmd");
    }

    [Fact]
    public void Track_ShouldCaptureToolApprovalPendingContextForResumeRequest()
    {
        var tracker = new RunSessionTracker();

        tracker.Track(CustomFrame("aevatar.run.context", new WorkflowRunContextPayload
        {
            ActorId = "actor-1",
            WorkflowName = "auto",
        }));
        tracker.Track(CustomFrame(WorkflowCustomEventNames.ToolApprovalPending, new WorkflowToolApprovalSuspensionCustomPayload
        {
            RunId = "run-1",
            StepId = "tool-step",
            ExecutionId = "exec-1",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ApprovalRequestId = "approval-1",
            ArgumentsJson = "{}",
        }));

        var snapshot = tracker.Snapshot;
        snapshot.RunId.Should().Be("run-1");
        snapshot.StepId.Should().Be("tool-step");
        snapshot.ExecutionId.Should().Be("exec-1");
        snapshot.ApprovalRequestId.Should().Be("approval-1");
        snapshot.SuspensionType.Should().Be("tool_approval");

        var resume = tracker.CreateResumeRequest("scope-a", approved: true, serviceId: "orders");
        resume.ActorId.Should().Be("actor-1");
        resume.RunId.Should().Be("run-1");
        resume.StepId.Should().Be("tool-step");
        resume.ExecutionId.Should().Be("exec-1");
        resume.ApprovalRequestId.Should().Be("approval-1");
    }

    [Fact]
    public void CreateSignalRequest_ShouldAllowExplicitStepOverride()
    {
        var tracker = new RunSessionTracker();
        tracker.Track(CustomFrame("aevatar.run.context", new WorkflowRunContextPayload
        {
            ActorId = "actor-1",
            WorkflowName = "auto",
            CommandId = "cmd-1",
        }));
        tracker.Track(CustomFrame("aevatar.workflow.waiting_signal", new WorkflowWaitingSignalCustomPayload
        {
            RunId = "run-1",
            StepId = "wait-1",
            SignalName = "ops_window_open",
        }));

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

        tracker.Track(CustomFrame("aevatar.run.context", new WorkflowRunContextPayload
        {
            ActorId = "actor-p",
            WorkflowName = "auto",
            CommandId = "cmd-p",
        }));
        tracker.Track(CustomFrame("aevatar.step.request", new WorkflowStepRequestCustomPayload
        {
            RunId = "run-p",
            StepId = "step-p",
        }));

        tracker.Snapshot.ActorId.Should().Be("actor-p");
        tracker.Snapshot.RunId.Should().Be("run-p");
        tracker.Snapshot.StepId.Should().Be("step-p");
    }

    [Fact]
    public void CreateResumeRequest_WhenContextIncomplete_ShouldThrowInvalidRequest()
    {
        var tracker = new RunSessionTracker();
        tracker.Track(CustomFrame("aevatar.run.context", new WorkflowRunContextPayload
        {
            ActorId = "actor-1",
            CommandId = "cmd-1",
        }));

        var act = () => tracker.CreateResumeRequest("scope-a", approved: true, serviceId: "orders");
        var ex = act.Should().Throw<AevatarWorkflowException>();
        ex.Which.Kind.Should().Be(AevatarWorkflowErrorKind.InvalidRequest);
    }

    [Fact]
    public void Track_ShouldCaptureSignalContextFromBufferedEvents()
    {
        var tracker = new RunSessionTracker();
        tracker.Track(CustomFrame(WorkflowCustomEventNames.RunContext, new WorkflowRunContextPayload
        {
            ActorId = "actor-1",
            WorkflowName = "auto",
            CommandId = "cmd-1",
        }));
        tracker.Track(CustomFrame(WorkflowCustomEventNames.SignalBuffered, new WorkflowSignalBufferedCustomPayload
        {
            RunId = "run-buf",
            StepId = "wait-buf",
            SignalName = "buffered_ready",
        }));

        tracker.Snapshot.RunId.Should().Be("run-buf");
        tracker.Snapshot.StepId.Should().Be("wait-buf");
        tracker.Snapshot.LastSignalName.Should().Be("buffered_ready");
    }

    private static WorkflowRunEventEnvelope CustomFrame<TPayload>(string name, TPayload payload)
        where TPayload : class, IMessage =>
        new()
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = name,
                Payload = Any.Pack(payload),
            },
        };
}
