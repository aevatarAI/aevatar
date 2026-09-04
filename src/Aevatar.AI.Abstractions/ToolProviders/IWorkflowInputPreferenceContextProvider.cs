namespace Aevatar.AI.Abstractions.ToolProviders;

public interface IWorkflowInputPreferenceContextProvider
{
    ValueTask<WorkflowInputPreferenceContext> ReadAsync(
        WorkflowInputPreferenceContextRequest request,
        CancellationToken ct = default);
}

public sealed record WorkflowInputPreferenceContextRequest(
    string WorkflowId,
    string Prompt,
    AgentToolExecutionContext? ToolContext);

public sealed record WorkflowInputPreferenceContext(
    IReadOnlyList<WorkflowInputPreferenceContextSource> Sources)
{
    public static WorkflowInputPreferenceContext Empty { get; } = new([]);
}

public sealed record WorkflowInputPreferenceContextSource(
    string ToolName,
    string OperationId,
    string PathTemplate,
    string DataJson);
