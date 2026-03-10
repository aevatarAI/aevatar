using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal readonly record struct ChatRunRequestNormalizationResult(
    WorkflowChatRunRequest? Request,
    WorkflowChatRunStartError Error)
{
    public bool Succeeded => Error == WorkflowChatRunStartError.None && Request != null;

    public static ChatRunRequestNormalizationResult Success(WorkflowChatRunRequest request) =>
        new(request, WorkflowChatRunStartError.None);

    public static ChatRunRequestNormalizationResult Failed(WorkflowChatRunStartError error) =>
        new(null, error);
}

internal static class ChatRunRequestNormalizer
{
    public static ChatRunRequestNormalizationResult Normalize(ChatInput input)
    {
        var normalizedAgentId = NormalizeAgentId(input.AgentId);
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(input.WorkflowYamls);
        if (inlineWorkflowYamls.Count > 0)
        {
            // Inline YAML bundle has explicit precedence over workflow-name lookup.
            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    input.Prompt,
                    WorkflowName: null,
                    normalizedAgentId,
                    SessionId: NormalizeSessionId(input.SessionId),
                    WorkflowYamls: inlineWorkflowYamls));
        }

        var requestedWorkflowName = NormalizeWorkflowName(input.Workflow);
        if (!string.IsNullOrWhiteSpace(requestedWorkflowName))
        {
            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    input.Prompt,
                    requestedWorkflowName,
                    normalizedAgentId,
                    SessionId: NormalizeSessionId(input.SessionId),
                    WorkflowYamls: null));
        }

        // Public default mode: only actor-creation requests route to auto.
        var defaultWorkflowName = string.IsNullOrWhiteSpace(normalizedAgentId)
            ? WorkflowRunBehaviorOptions.AutoWorkflowName
            : null;
        return ChatRunRequestNormalizationResult.Success(
            new WorkflowChatRunRequest(
                input.Prompt,
                defaultWorkflowName,
                normalizedAgentId,
                SessionId: NormalizeSessionId(input.SessionId),
                WorkflowYamls: null));
    }

    private static IReadOnlyList<string> NormalizeInlineWorkflowYamls(IReadOnlyList<string>? workflowYamls)
    {
        if (workflowYamls == null || workflowYamls.Count == 0)
            return [];

        var normalized = new List<string>(workflowYamls.Count);
        foreach (var yaml in workflowYamls)
            normalized.Add(yaml ?? string.Empty);
        return normalized;
    }

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();

    private static string? NormalizeAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();

    private static string? NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
}
