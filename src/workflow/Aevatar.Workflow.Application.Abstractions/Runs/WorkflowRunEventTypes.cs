namespace Aevatar.Workflow.Application.Abstractions.Runs;

public static class WorkflowRunEventTypes
{
    public const string RunStarted = "RUN_STARTED";
    public const string RunFinished = "RUN_FINISHED";
    public const string RunError = "RUN_ERROR";
    public const string StepStarted = "STEP_STARTED";
    public const string StepFinished = "STEP_FINISHED";
    public const string TextMessageStart = "TEXT_MESSAGE_START";
    public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";
    public const string TextMessageEnd = "TEXT_MESSAGE_END";
    public const string StateSnapshot = "STATE_SNAPSHOT";
    public const string ToolCallStart = "TOOL_CALL_START";
    public const string ToolCallEnd = "TOOL_CALL_END";
    public const string Custom = "CUSTOM";
}
