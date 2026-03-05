using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

internal static class AGUIEventToWorkflowRunEventMapper
{
    public static WorkflowRunEvent Map(Aevatar.Presentation.AGUI.AGUIEvent evt)
    {
        return evt switch
        {
            Aevatar.Presentation.AGUI.RunStartedEvent e => new WorkflowRunStartedEvent
            {
                Timestamp = e.Timestamp,
                ThreadId = e.ThreadId,
            },
            Aevatar.Presentation.AGUI.RunFinishedEvent e => new WorkflowRunFinishedEvent
            {
                Timestamp = e.Timestamp,
                ThreadId = e.ThreadId,
                Result = e.Result,
            },
            Aevatar.Presentation.AGUI.RunErrorEvent e => new WorkflowRunErrorEvent
            {
                Timestamp = e.Timestamp,
                Message = e.Message,
                Code = e.Code,
            },
            Aevatar.Presentation.AGUI.StepStartedEvent e => new WorkflowStepStartedEvent
            {
                Timestamp = e.Timestamp,
                StepName = e.StepName,
            },
            Aevatar.Presentation.AGUI.StepFinishedEvent e => new WorkflowStepFinishedEvent
            {
                Timestamp = e.Timestamp,
                StepName = e.StepName,
            },
            Aevatar.Presentation.AGUI.TextMessageStartEvent e => new WorkflowTextMessageStartEvent
            {
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
                Role = e.Role,
            },
            Aevatar.Presentation.AGUI.TextMessageContentEvent e => new WorkflowTextMessageContentEvent
            {
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
                Delta = e.Delta,
            },
            Aevatar.Presentation.AGUI.TextMessageEndEvent e => new WorkflowTextMessageEndEvent
            {
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
            },
            Aevatar.Presentation.AGUI.StateSnapshotEvent e => new WorkflowStateSnapshotEvent
            {
                Timestamp = e.Timestamp,
                Snapshot = e.Snapshot,
            },
            Aevatar.Presentation.AGUI.ToolCallStartEvent e => new WorkflowToolCallStartEvent
            {
                Timestamp = e.Timestamp,
                ToolCallId = e.ToolCallId,
                ToolName = e.ToolName,
            },
            Aevatar.Presentation.AGUI.ToolCallEndEvent e => new WorkflowToolCallEndEvent
            {
                Timestamp = e.Timestamp,
                ToolCallId = e.ToolCallId,
                Result = e.Result,
            },
            Aevatar.Presentation.AGUI.HumanInputRequestEvent e => new WorkflowCustomEvent
            {
                Timestamp = e.Timestamp,
                Name = "aevatar.human_input.request",
                Value = new
                {
                    e.RunId,
                    e.StepId,
                    e.SuspensionType,
                    e.Prompt,
                    e.TimeoutSeconds,
                    e.Metadata,
                },
            },
            Aevatar.Presentation.AGUI.HumanInputResponseEvent e => new WorkflowCustomEvent
            {
                Timestamp = e.Timestamp,
                Name = "aevatar.human_input.response",
                Value = new
                {
                    e.RunId,
                    e.StepId,
                    e.Approved,
                    e.UserInput,
                },
            },
            Aevatar.Presentation.AGUI.CustomEvent e => new WorkflowCustomEvent
            {
                Timestamp = e.Timestamp,
                Name = e.Name,
                Value = e.Value,
            },
            _ => new WorkflowCustomEvent
            {
                Timestamp = evt.Timestamp,
                Name = evt.Type,
                Value = null,
            },
        };
    }
}
