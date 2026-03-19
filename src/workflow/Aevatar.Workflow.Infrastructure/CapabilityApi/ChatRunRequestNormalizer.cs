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
        var requestedWorkflowName = NormalizeWorkflowName(input.Workflow);
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(input.WorkflowYamls);
        var legacyWorkflowYaml = input.WorkflowYaml;
        var hasLegacyWorkflowYaml = legacyWorkflowYaml != null;
        var normalizedPromptInput = string.IsNullOrWhiteSpace(input.Prompt) ? string.Empty : input.Prompt.Trim();

        if (hasLegacyWorkflowYaml && string.IsNullOrWhiteSpace(legacyWorkflowYaml))
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml);

        if (hasLegacyWorkflowYaml && inlineWorkflowYamls.Count > 0)
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml);

        if (hasLegacyWorkflowYaml)
            inlineWorkflowYamls = [legacyWorkflowYaml!];

        var normalizedPrompt = WorkflowAuthoringSkillPromptAugmentor.AugmentPrompt(
            normalizedPromptInput,
            requestedWorkflowName,
            inlineWorkflowYamls.Count > 0,
            normalizedMetadata,
            capabilities);

        if (inlineWorkflowYamls.Count > 0)
        {
            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    Prompt: normalizedPrompt,
                    WorkflowName: string.IsNullOrWhiteSpace(requestedWorkflowName) ? null : requestedWorkflowName,
                    ActorId: normalizedAgentId,
                    SessionId: NormalizeSessionId(input.SessionId),
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
                    SessionId: NormalizeSessionId(input.SessionId),
                    WorkflowYamls: null,
                    Metadata: normalizedMetadata));
        }

        return ChatRunRequestNormalizationResult.Success(
            new WorkflowChatRunRequest(
                Prompt: normalizedPrompt,
                WorkflowName: null,
                ActorId: normalizedAgentId,
                SessionId: NormalizeSessionId(input.SessionId),
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

        if (string.Equals(normalizedKey, "scope_id", StringComparison.Ordinal) &&
            metadata.ContainsKey(WorkflowRunCommandMetadataKeys.ScopeId))
        {
            return;
        }

        var canonicalKey = string.Equals(normalizedKey, "scope_id", StringComparison.Ordinal)
            ? WorkflowRunCommandMetadataKeys.ScopeId
            : normalizedKey;
        metadata[canonicalKey] = normalizedValue;
    }

    private static string? NormalizeAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();

    private static string? NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
}
