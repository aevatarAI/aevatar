namespace Aevatar.Workflow.Application.Abstractions.Runs;

public abstract record WorkflowRunEvent
{
    public abstract string Type { get; }

    public long? Timestamp { get; init; }
}

public sealed record WorkflowRunStartedEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.RunStarted;

    public required string ThreadId { get; init; }
}

public sealed record WorkflowRunFinishedEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.RunFinished;

    public required string ThreadId { get; init; }

    public object? Result { get; init; }
}

public sealed record WorkflowRunErrorEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.RunError;

    public required string Message { get; init; }

    public string? Code { get; init; }
}

public sealed record WorkflowStepStartedEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.StepStarted;

    public required string StepName { get; init; }
}

public sealed record WorkflowStepFinishedEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.StepFinished;

    public required string StepName { get; init; }
}

public sealed record WorkflowTextMessageStartEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.TextMessageStart;

    public required string MessageId { get; init; }

    public required string Role { get; init; }
}

public sealed record WorkflowTextMessageContentEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.TextMessageContent;

    public required string MessageId { get; init; }

    public required string Delta { get; init; }
}

public sealed record WorkflowTextMessageEndEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.TextMessageEnd;

    public required string MessageId { get; init; }
}

public sealed record WorkflowStateSnapshotEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.StateSnapshot;

    public required object Snapshot { get; init; }
}

public sealed record WorkflowToolCallStartEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.ToolCallStart;

    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }
}

public sealed record WorkflowToolCallEndEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.ToolCallEnd;

    public required string ToolCallId { get; init; }

    public string? Result { get; init; }
}

public sealed record WorkflowCustomEvent : WorkflowRunEvent
{
    public override string Type => WorkflowRunEventTypes.Custom;

    public required string Name { get; init; }

    public object? Value { get; init; }
}
