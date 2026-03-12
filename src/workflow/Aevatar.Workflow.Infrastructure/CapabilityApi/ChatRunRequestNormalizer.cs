using Aevatar.Workflow.Application.Abstractions.Queries;
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
    public static ChatRunRequestNormalizationResult Normalize(
        ChatInput input,
        WorkflowCapabilitiesDocument? capabilities = null,
        IReadOnlyDictionary<string, string>? defaultMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var normalizedAgentId = NormalizeAgentId(input.AgentId);
        var normalizedMetadata = NormalizeMetadata(input.Metadata, defaultMetadata);
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(input.WorkflowYamls);
        var requestedWorkflowName = NormalizeWorkflowName(input.Workflow);
        var normalizedPrompt = WorkflowAuthoringSkillPromptAugmentor.AugmentPrompt(
            input.Prompt,
            requestedWorkflowName,
            inlineWorkflowYamls.Count > 0,
            normalizedMetadata,
            capabilities);

        if (inlineWorkflowYamls.Count > 0)
        {
            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    Prompt: normalizedPrompt,
                    WorkflowName: null,
                    ActorId: normalizedAgentId,
                    WorkflowYamls: inlineWorkflowYamls,
                    Metadata: normalizedMetadata));
        }

        if (!string.IsNullOrWhiteSpace(requestedWorkflowName))
        {
            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    Prompt: normalizedPrompt,
                    WorkflowName: requestedWorkflowName,
                    ActorId: normalizedAgentId,
                    WorkflowYamls: null,
                    Metadata: normalizedMetadata));
        }

        var defaultWorkflowName = string.IsNullOrWhiteSpace(normalizedAgentId)
            ? WorkflowRunBehaviorOptions.AutoWorkflowName
            : null;
        return ChatRunRequestNormalizationResult.Success(
            new WorkflowChatRunRequest(
                Prompt: normalizedPrompt,
                WorkflowName: defaultWorkflowName,
                ActorId: normalizedAgentId,
                WorkflowYamls: null,
                Metadata: normalizedMetadata));
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

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(
        IDictionary<string, string>? metadata,
        IReadOnlyDictionary<string, string>? defaultMetadata)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (defaultMetadata is { Count: > 0 })
        {
            foreach (var (key, value) in defaultMetadata)
                AddNormalizedMetadataEntry(normalized, key, value);
        }

        if (metadata is { Count: > 0 })
        {
            foreach (var (key, value) in metadata)
                AddNormalizedMetadataEntry(normalized, key, value);
        }

        return normalized;
    }

    private static void AddNormalizedMetadataEntry(
        IDictionary<string, string> metadata,
        string key,
        string value)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
            return;

        metadata[normalizedKey] = normalizedValue;
    }

    private static string? NormalizeAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();
}
