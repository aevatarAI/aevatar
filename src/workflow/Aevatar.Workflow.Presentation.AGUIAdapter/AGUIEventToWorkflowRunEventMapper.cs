using AGUI = Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

internal static class AGUIEventToWorkflowRunEventMapper
{
    public static WorkflowRunEvent Map(AGUI.AGUIEvent evt)
    {
        return evt switch
        {
            AGUI.RunStartedEvent e => new WorkflowRunStartedEvent
            {
                Timestamp = e.Timestamp,
                ThreadId = e.ThreadId,
                RunId = e.RunId,
            },
            AGUI.RunFinishedEvent e => new WorkflowRunFinishedEvent
            {
                Timestamp = e.Timestamp,
                ThreadId = e.ThreadId,
                RunId = e.RunId,
                Result = e.Result,
            },
            AGUI.RunErrorEvent e => new WorkflowRunErrorEvent
            {
                Timestamp = e.Timestamp,
                Message = e.Message,
                RunId = e.RunId,
                Code = e.Code,
            },
            AGUI.StepStartedEvent e => new WorkflowStepStartedEvent
            {
                Timestamp = e.Timestamp,
                StepName = e.StepName,
            },
            AGUI.StepFinishedEvent e => new WorkflowStepFinishedEvent
            {
                Timestamp = e.Timestamp,
                StepName = e.StepName,
            },
            AGUI.TextMessageStartEvent e => new WorkflowTextMessageStartEvent
            {
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
                Role = e.Role,
            },
            AGUI.TextMessageContentEvent e => new WorkflowTextMessageContentEvent
            {
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
                Delta = e.Delta,
            },
            AGUI.TextMessageEndEvent e => new WorkflowTextMessageEndEvent
            {
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
            },
            AGUI.StateSnapshotEvent e => new WorkflowStateSnapshotEvent
            {
                Timestamp = e.Timestamp,
                Snapshot = e.Snapshot,
            },
            AGUI.ToolCallStartEvent e => new WorkflowToolCallStartEvent
            {
                Timestamp = e.Timestamp,
                ToolCallId = e.ToolCallId,
                ToolName = e.ToolName,
            },
            AGUI.ToolCallEndEvent e => new WorkflowToolCallEndEvent
            {
                Timestamp = e.Timestamp,
                ToolCallId = e.ToolCallId,
                Result = e.Result,
            },
            AGUI.CustomEvent e => new WorkflowCustomEvent
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
