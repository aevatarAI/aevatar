using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Infrastructure.Workflows;

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
    public static ChatRunRequestNormalizationResult Normalize(
        ChatInput input,
        IFileBackedWorkflowNameCatalog fileBackedWorkflowNames)
    {
        var normalizedMetadata = NormalizeMetadata(input.Metadata);
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(input.WorkflowYamls);
        if (inlineWorkflowYamls.Count > 0)
        {
            // Inline YAML bundle has explicit precedence over workflow-name lookup.
            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    input.Prompt,
                    WorkflowName: null,
                    input.AgentId,
                    inlineWorkflowYamls,
                    normalizedMetadata));
        }

        var requestedWorkflowName = NormalizeWorkflowName(input.Workflow);
        if (!string.IsNullOrWhiteSpace(requestedWorkflowName))
        {
            if (!fileBackedWorkflowNames.Contains(requestedWorkflowName))
                return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.WorkflowNotFound);

            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    input.Prompt,
                    requestedWorkflowName,
                    input.AgentId,
                    WorkflowYamls: null,
                    normalizedMetadata));
        }

        // Public default mode: prompt-only requests route to auto.
        return ChatRunRequestNormalizationResult.Success(
            new WorkflowChatRunRequest(
                input.Prompt,
                WorkflowRunBehaviorOptions.AutoWorkflowName,
                input.AgentId,
                WorkflowYamls: null,
                normalizedMetadata));
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

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;
            normalized[normalizedKey] = normalizedValue;
        }

        return normalized;
    }
}
