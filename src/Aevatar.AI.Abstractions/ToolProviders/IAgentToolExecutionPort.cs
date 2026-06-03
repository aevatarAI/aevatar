namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>Executes agent tools through the configured tool-call policy pipeline.</summary>
public interface IAgentToolExecutionPort
{
    Task<AgentToolExecutionResult> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct);
}

public sealed record AgentToolExecutionRequest(
    IAgentTool Tool,
    string ToolName,
    string ToolCallId,
    string ArgumentsJson);

public sealed record AgentToolExecutionResult(
    AgentToolExecutionStatus Status,
    string? ResultJson,
    string? ErrorMessage)
{
    public static AgentToolExecutionResult Succeeded(string resultJson) =>
        new(AgentToolExecutionStatus.Succeeded, resultJson, null);

    public static AgentToolExecutionResult Failed(string errorMessage) =>
        new(AgentToolExecutionStatus.Failed, null, errorMessage);
}

public enum AgentToolExecutionStatus
{
    Succeeded = 0,
    ApprovalDenied = 1,
    ApprovalTimedOut = 2,
    ApprovalPending = 3,
    MiddlewareTerminated = 4,
    Failed = 5,
}
