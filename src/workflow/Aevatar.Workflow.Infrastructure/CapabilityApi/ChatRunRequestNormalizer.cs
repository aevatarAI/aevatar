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
    private readonly record struct NormalizedChatContext(
        IReadOnlyDictionary<string, string> Metadata,
        string? ScopeId,
        WorkflowChatRunStartError Error)
    {
        public bool Succeeded => Error == WorkflowChatRunStartError.None;
    }

    public static ChatRunRequestNormalizationResult Normalize(
        ChatInput input,
        WorkflowCapabilitiesDocument? capabilities = null,
        IReadOnlyDictionary<string, string>? defaultMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var normalizedAgentId = NormalizeAgentId(input.AgentId);
        var normalizedContext = NormalizeContext(input.ScopeId, input.Metadata, defaultMetadata);
        if (!normalizedContext.Succeeded)
            return ChatRunRequestNormalizationResult.Failed(normalizedContext.Error);

        var normalizedMetadata = normalizedContext.Metadata;
        var requestedWorkflowName = NormalizeWorkflowName(input.Workflow);
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(input.WorkflowYamls);
        var legacyWorkflowYaml = input.WorkflowYaml;
        var hasLegacyWorkflowYaml = legacyWorkflowYaml != null;
        var normalizedPromptInput = string.IsNullOrWhiteSpace(input.Prompt) ? string.Empty : input.Prompt.Trim();

        if (normalizedPromptInput.Length == 0)
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.PromptRequired);

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
                    Metadata: normalizedMetadata,
                    ScopeId: normalizedContext.ScopeId));
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
                    Metadata: normalizedMetadata,
                    ScopeId: normalizedContext.ScopeId));
        }

        return ChatRunRequestNormalizationResult.Success(
            new WorkflowChatRunRequest(
                Prompt: normalizedPrompt,
                WorkflowName: null,
                ActorId: normalizedAgentId,
                SessionId: NormalizeSessionId(input.SessionId),
                WorkflowYamls: null,
                Metadata: normalizedMetadata,
                ScopeId: normalizedContext.ScopeId));
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

    private static NormalizedChatContext NormalizeContext(
        string? explicitScopeId,
        IDictionary<string, string>? metadata,
        IReadOnlyDictionary<string, string>? defaultMetadata)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalizedScopeId = NormalizeScopeId(explicitScopeId);
        if (defaultMetadata is { Count: > 0 })
        {
            foreach (var (key, value) in defaultMetadata)
            {
                if (!AddNormalizedMetadataEntry(normalized, ref normalizedScopeId, key, value))
                    return new NormalizedChatContext(normalized, null, WorkflowChatRunStartError.ConflictingScopeId);
            }
        }

        if (metadata is { Count: > 0 })
        {
            foreach (var (key, value) in metadata)
            {
                if (!AddNormalizedMetadataEntry(normalized, ref normalizedScopeId, key, value))
                    return new NormalizedChatContext(normalized, null, WorkflowChatRunStartError.ConflictingScopeId);
            }
        }

        return new NormalizedChatContext(normalized, normalizedScopeId, WorkflowChatRunStartError.None);
    }

    private static bool AddNormalizedMetadataEntry(
        IDictionary<string, string> metadata,
        ref string? scopeId,
        string key,
        string value)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
            return true;

        if (IsScopeMetadataKey(normalizedKey))
        {
            return TryAssignScopeId(ref scopeId, normalizedValue);
        }

        metadata[normalizedKey] = normalizedValue;
        return true;
    }

    private static bool IsScopeMetadataKey(string key) =>
        string.Equals(key, "scope_id", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, WorkflowRunCommandMetadataKeys.ScopeId, StringComparison.OrdinalIgnoreCase);

    private static bool TryAssignScopeId(ref string? currentScopeId, string? candidateScopeId)
    {
        var normalizedCandidate = NormalizeScopeId(candidateScopeId);
        if (normalizedCandidate == null)
            return true;

        if (string.IsNullOrWhiteSpace(currentScopeId))
        {
            currentScopeId = normalizedCandidate;
            return true;
        }

        return string.Equals(currentScopeId, normalizedCandidate, StringComparison.Ordinal);
    }

    private static string? NormalizeScopeId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId) ? null : scopeId.Trim();

    private static string? NormalizeAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();

    private static string? NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
}
