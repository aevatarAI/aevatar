using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal static class WorkflowOutputFrameMapper
{
    public static WorkflowOutputFrame Map(WorkflowRunEvent evt)
    {
        return evt switch
        {
            WorkflowRunStartedEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                ThreadId = e.ThreadId,
            },
            WorkflowRunFinishedEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                ThreadId = e.ThreadId,
                Result = e.Result,
            },
            WorkflowRunErrorEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                Message = e.Message,
                Code = e.Code,
            },
            WorkflowStepStartedEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                StepName = e.StepName,
            },
            WorkflowStepFinishedEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                StepName = e.StepName,
            },
            WorkflowTextMessageStartEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
                Role = e.Role,
            },
            WorkflowTextMessageContentEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
                Delta = e.Delta,
            },
            WorkflowTextMessageEndEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                MessageId = e.MessageId,
            },
            WorkflowStateSnapshotEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                Snapshot = e.Snapshot,
            },
            WorkflowToolCallStartEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                ToolCallId = e.ToolCallId,
                ToolName = e.ToolName,
            },
            WorkflowToolCallEndEvent e => new WorkflowOutputFrame
            {
                Type = e.Type,
                Timestamp = e.Timestamp,
                ToolCallId = e.ToolCallId,
                Result = e.Result,
            },
            WorkflowCustomEvent e => new WorkflowOutputFrame
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
