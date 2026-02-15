using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal static class WorkflowOutputFrameMapper
{
    public static WorkflowOutputFrame Map(AGUIEvent evt)
    {
        return evt switch
        {
            RunStartedEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                ThreadId = e.ThreadId,
                RunId = e.RunId,
            },
            RunFinishedEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                ThreadId = e.ThreadId,
                RunId = e.RunId,
                Result = e.Result,
            },
            RunErrorEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                RunId = e.RunId,
                Message = e.Message,
                Code = e.Code,
            },
            StepStartedEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                StepName = e.StepName,
            },
            StepFinishedEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                StepName = e.StepName,
            },
            TextMessageStartEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
                Role = e.Role,
            },
            TextMessageContentEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
                Delta = e.Delta,
            },
            TextMessageEndEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
            },
            StateSnapshotEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                Snapshot = e.Snapshot,
            },
            ToolCallStartEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                ToolCallId = e.ToolCallId,
                ToolName = e.ToolName,
            },
            ToolCallEndEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                ToolCallId = e.ToolCallId,
                Result = e.Result,
            },
            CustomEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                Name = e.Name,
                Value = e.Value,
            },
            _ => new WorkflowOutputFrame
            {
                Type = evt.Type,
                Timestamp = evt.Timestamp,
            },
        };
    }
}
